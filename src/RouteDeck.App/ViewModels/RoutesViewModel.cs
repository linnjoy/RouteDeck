using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RouteDeck.App.Services;
using RouteDeck.Contracts;
using RouteDeck.Core.Changes;
using RouteDeck.Core.Diagnostics;
using RouteDeck.Core.Models;
using RouteDeck.Core.Serialization;

namespace RouteDeck.App.ViewModels;

public sealed partial class FindingItem(DiagnosticFinding finding, RoutesViewModel owner) : ObservableObject
{
    public DiagnosticFinding Finding { get; } = finding;

    public string Title => Finding.Title;

    public string Detail => Finding.Detail;

    public Wpf.Ui.Controls.InfoBarSeverity Severity => Finding.Level switch
    {
        FindingLevel.Problem => Wpf.Ui.Controls.InfoBarSeverity.Error,
        FindingLevel.Notice => Wpf.Ui.Controls.InfoBarSeverity.Warning,
        _ => Wpf.Ui.Controls.InfoBarSeverity.Informational,
    };

    public bool HasFix => Finding.Fix is not null;

    public string FixLabel => Finding.FixLabel ?? "修复";

    public bool CanBypass => Finding.TunnelLuid is not null;

    [RelayCommand]
    private Task FixAsync() => owner.ApplyFixAsync(Finding);

    [RelayCommand]
    private void Bypass() => owner.Bypass(null, Finding.TunnelLuid);

    [RelayCommand]
    private void Dismiss() => owner.Main.DismissFinding(Finding);
}

public sealed partial class RoutesViewModel : ObservableObject
{
    private List<RouteRow> _allRows = [];

    public RoutesViewModel(MainViewModel main)
    {
        Main = main;
        View = new ListCollectionView(new List<RouteRow>());
        _showSystemRoutes = main.Settings.ShowSystemRoutes;
    }

    public MainViewModel Main { get; }

    [ObservableProperty]
    private ListCollectionView _view;

    [ObservableProperty]
    private IReadOnlyList<FindingItem> _findingItems = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private RouteRow? _selected;

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private bool _showIPv6;

    [ObservableProperty]
    private bool _showSystemRoutes;

    [ObservableProperty]
    private string _summary = "";

    /// <summary>Route highlighted by the last path lookup.</summary>
    [ObservableProperty]
    private RouteKey? _highlighted;

    public bool HasSelection => Selected is not null;

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnShowIPv6Changed(bool value) => ApplyFilter();

    partial void OnShowSystemRoutesChanged(bool value)
    {
        Main.Settings.ShowSystemRoutes = value;
        Main.Settings.Save();
        ApplyFilter();
    }

    public void Rebuild()
    {
        NetworkSnapshot snapshot = Main.Snapshot;
        var flagged = Main.Findings
            .SelectMany(f => f.Routes.Select(r => (Key: r, f.Level)))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Max(x => x.Level));
        RouteKey? selectedKey = Selected?.Route.Key;

        _allRows = snapshot.Routes
            .Select(r => new RouteRow(r, snapshot.FindAdapter(r.InterfaceLuid), flagged.TryGetValue(r.Key, out FindingLevel level) ? level : null))
            .OrderByDescending(r => r.AdapterUp)
            .ThenBy(r => r.Adapter?.Kind == AdapterKind.Loopback)
            .ThenBy(r => r.GroupName, StringComparer.CurrentCulture)
            .ThenBy(r => r.Route.Destination.Length)
            .ThenBy(r => r.Destination, StringComparer.Ordinal)
            .ToList();

        FindingItems = Main.Findings.Select(f => new FindingItem(f, this)).ToList();
        ApplyFilter();
        Selected = selectedKey is null ? null : _allRows.FirstOrDefault(r => r.Route.Key == selectedKey);
    }

    private void ApplyFilter()
    {
        AddressFamily family = ShowIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        string filter = Filter.Trim();
        var rows = _allRows
            .Where(r => r.Route.Destination.Family == family)
            .Where(r => ShowSystemRoutes || !r.IsHousekeeping)
            .Where(r => filter.Length == 0 ||
                        r.Destination.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        r.Gateway.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        r.GroupName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        r.SourceLabel.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RouteRow.GroupName)));
        View = view;

        int hidden = _allRows.Count(r => r.Route.Destination.Family == family) - rows.Count;
        Summary = $"{rows.Count} 条" + (hidden > 0 ? $" · 已隐藏 {hidden} 条" : "");
    }

    public void Highlight(RouteKey key)
    {
        Highlighted = key;
        RouteRow? row = _allRows.FirstOrDefault(r => r.Route.Key == key);
        if (row is null) return;
        if (row.IsHousekeeping && !ShowSystemRoutes) ShowSystemRoutes = true;
        if ((row.Route.Destination.Family == AddressFamily.InterNetworkV6) != ShowIPv6) ShowIPv6 = !ShowIPv6;
        Filter = "";
        Selected = row;
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        RouteSpec? spec = Main.Dialogs.EditRoute(Main.Snapshot, null);
        if (spec is not null) await Main.ExecuteAsync(ChangeSets.Add(spec), "添加路由");
    }

    [RelayCommand]
    private async Task EditAsync()
    {
        if (Selected is not { } row) return;
        RouteSpec? spec = Main.Dialogs.EditRoute(Main.Snapshot, row.Route);
        if (spec is null || spec == row.Route.ToSpec()) return;
        await Main.ExecuteAsync(ChangeSets.Edit(row.Route, spec), "编辑路由");
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } row) return;
        await Main.ExecuteAsync(ChangeSets.Remove(row.Route), "删除路由");
    }

    [RelayCommand]
    private void CopyRouteAdd()
    {
        if (Selected is { } row) Clipboard.SetText(row.RouteAddCommand);
    }

    [RelayCommand]
    private void CopyPowerShell()
    {
        if (Selected is { } row) Clipboard.SetText(row.PowerShellCommand);
    }

    [RelayCommand]
    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"routes-{DateTime.Now:yyyyMMdd-HHmm}",
            Filter = "CSV 表格|*.csv|JSON|*.json",
        };
        if (dialog.ShowDialog() != true) return;

        if (dialog.FilterIndex == 2)
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(Main.Snapshot, RouteDeckJson.Options), Encoding.UTF8);
            return;
        }
        var csv = new StringBuilder("网卡,目标,网关,路由跃点,网卡跃点,有效跃点,来源,存储\r\n");
        foreach (RouteRow r in _allRows)
            csv.Append($"\"{r.Adapter?.Name}\",{r.Destination},{r.Gateway},{r.Route.Metric},{r.Route.InterfaceMetric},{r.EffectiveMetric},{r.SourceLabel},\"{r.StoreLabel}\"\r\n");
        File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
    }

    [RelayCommand]
    private void ShowBypass() => Bypass(null, null);

    public void Bypass(string? target, ulong? tunnelLuid)
    {
        BypassRequest? request = Main.Dialogs.Bypass(Main.Snapshot, target, tunnelLuid);
        if (request is null) return;
        try
        {
            ChangeSet plan = BypassPlanner.Plan(Main.Snapshot, request.Destination, request.ExitLuid, request.Gateway, request.Persistent);
            Main.ExecuteAsync(plan, "添加绕过路由").Forget(ex => Main.ShowError("添加绕过路由失败", ex.Message));
        }
        catch (ArgumentException ex)
        {
            Main.ShowError("无法添加绕过路由", ex.Message);
        }
    }

    public Task ApplyFixAsync(DiagnosticFinding finding) =>
        finding.Fix is null ? Task.CompletedTask : Main.ExecuteAsync(finding.Fix, finding.FixLabel ?? "修复");
}
