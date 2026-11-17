using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RouteDeck.App.Services;
using RouteDeck.Core.Changes;
using RouteDeck.Core.Diagnostics;
using RouteDeck.Core.Models;
using StreamJsonRpc;

namespace RouteDeck.App.ViewModels;

public enum NoticeLevel
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record Notice(string Title, string Message, NoticeLevel Level);

public sealed record NavItem(string Title, string Symbol, object? Page, string? Hint = null)
{
    public bool IsEnabled => Page is not null;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDialogs _dialogs;
    private readonly DispatcherTimer _refreshDebounce;

    public MainViewModel(ServiceConnection connection, IDialogs dialogs, UiSettings settings)
    {
        Connection = connection;
        _dialogs = dialogs;
        Settings = settings;
        Routes = new RoutesViewModel(this);
        Path = new PathViewModel(this);
        History = new HistoryViewModel(this);
        Overview = new OverviewViewModel(this);
        NavItems =
        [
            new("总览", "Home24", Overview),
            new("路由表", "Router24", Routes),
            new("路径查询", "Search24", Path),
            new("操作历史", "History24", History),
            new("场景", "Layer24", null, "M2 推出"),
            new("自动规则", "Flash24", null, "M4 推出"),
        ];
        _selectedNav = NavItems[1];

        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshDebounce.Tick += (_, _) =>
        {
            _refreshDebounce.Stop();
            RefreshAsync().Forget(ex => Notify("刷新失败", ex.Message, NoticeLevel.Error));
        };

        connection.StateChanged += () => Ui(ScheduleRefresh);
        connection.OperationReported += (id, message) => Ui(() =>
        {
            if (id != 0 && message.Contains("已恢复")) Notify("已自动恢复", message, NoticeLevel.Warning);
            else if (id == 0) Notify("服务启动时的恢复", message, NoticeLevel.Warning);
            History.RefreshAsync().Forget(ex => Notify("刷新历史失败", ex.Message, NoticeLevel.Error));
        });
        connection.PendingCountdown += (pending, seconds) => Ui(() =>
        {
            Pending = pending;
            PendingSeconds = seconds;
        });
    }

    public ServiceConnection Connection { get; }

    public UiSettings Settings { get; }

    public RoutesViewModel Routes { get; }

    public PathViewModel Path { get; }

    public HistoryViewModel History { get; }

    public OverviewViewModel Overview { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    public ObservableCollection<Notice> Notices { get; } = [];

    [ObservableProperty]
    private NavItem _selectedNav;

    [ObservableProperty]
    private NetworkSnapshot _snapshot = NetworkSnapshot.Empty;

    [ObservableProperty]
    private IReadOnlyList<DiagnosticFinding> _findings = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    private PendingChange? _pending;

    [ObservableProperty]
    private int _pendingSeconds;

    [ObservableProperty]
    private string _quickQuery = "";

    public bool HasPending => Pending is not null;

    partial void OnSelectedNavChanged(NavItem value)
    {
        if (!value.IsEnabled) SelectedNav = NavItems[1];
    }

    public async Task InitializeAsync(string? initialQuery = null)
    {
        Connection.Start();
        await RefreshAsync();
        if (!string.IsNullOrWhiteSpace(initialQuery))
        {
            QuickQuery = initialQuery;
            QuickLookup();
        }
    }

    public void ScheduleRefresh()
    {
        _refreshDebounce.Stop();
        _refreshDebounce.Start();
    }

    public async Task RefreshAsync()
    {
        try
        {
            Snapshot = await Connection.GetSnapshotAsync();
            Findings = DiagnosticEngine.Run(Snapshot).Where(f => !Settings.DismissedFindings.Contains(f.Id)).ToList();
            Routes.Rebuild();
            Overview.Rebuild();
            if (Connection.Service is { } service && Pending is null)
                Pending = await service.GetPendingChangeAsync();
        }
        catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException)
        {
            Notify("刷新失败", ex.Message, NoticeLevel.Error);
        }
    }

    [RelayCommand]
    private void QuickLookup()
    {
        if (string.IsNullOrWhiteSpace(QuickQuery)) return;
        Path.Target = QuickQuery.Trim();
        SelectedNav = NavItems[2];
        Path.LookupCommand.Execute(null);
    }

    [RelayCommand]
    private void StartService()
    {
        if (!ServiceConnection.TryStartService()) Notify("没有启动服务", "需要管理员权限，或服务尚未安装（RouteDeck.Service.exe --install）。", NoticeLevel.Warning);
    }

    [RelayCommand]
    private async Task KeepPendingAsync()
    {
        if (Pending is not { } pending || Connection.Service is not { } service) return;
        await GuardAsync(async () =>
        {
            await service.ConfirmPendingChangeAsync(pending.OperationId);
            Pending = null;
            Notify("已保留更改", pending.Description, NoticeLevel.Success);
        });
    }

    [RelayCommand]
    private async Task RevertPendingAsync()
    {
        if (Pending is not { } pending || Connection.Service is not { } service) return;
        await GuardAsync(async () =>
        {
            string message = await service.RevertPendingChangeAsync(pending.OperationId);
            Pending = null;
            Notify("已恢复", message, NoticeLevel.Info);
        });
    }

    public void DismissFinding(DiagnosticFinding finding)
    {
        Settings.DismissedFindings.Add(finding.Id);
        Settings.Save();
        Findings = Findings.Where(f => f.Id != finding.Id).ToList();
        Routes.Rebuild();
    }

    /// <summary>
    /// Every write goes through here: list the operations, warn about third-party routes and
    /// lockout risk, ask, then let the service apply it with journaling and rollback.
    /// </summary>
    public async Task<bool> ExecuteAsync(ChangeSet changeSet, string title)
    {
        if (Connection.Service is not { } service || !Connection.CanWrite)
        {
            _dialogs.ShowError("不能修改", Connection.IsOnline ? Connection.StatusText : "RouteDeck 服务未运行，当前是只读模式。可以在顶部点击“启动服务”。");
            return false;
        }

        var owned = Snapshot.Routes.Where(r => r.IsManual).Select(r => r.Key).ToHashSet();
        var warnings = new List<string>();
        var foreign = changeSet.Operations.Where(o => o.Kind != RouteOperationKind.Add && !owned.Contains(o.Route.Key)).ToList();
        foreach (RouteOperation op in foreign)
        {
            NetAdapter? adapter = Snapshot.FindAdapter(op.Route.InterfaceLuid);
            warnings.Add(adapter?.TunnelManager is string manager
                ? $"{op.Route.Destination} 由 {manager} 管理，修改后它可能重新创建这条路由或工作异常。"
                : $"{op.Route.Destination} 不是 RouteDeck 创建的路由。");
        }
        if (LockoutGuard.RiskReason(changeSet, Snapshot) is string risk)
            warnings.Add($"{risk}。应用后有 60 秒确认时间，不点“保留”会自动恢复。");

        if (!_dialogs.ConfirmChange(title, changeSet.Operations.Select(o => o.Describe()).ToList(), warnings, "应用"))
            return false;

        bool ok = false;
        await GuardAsync(async () =>
        {
            ExecuteResult result = await service.ExecuteAsync(changeSet, confirmForeign: foreign.Count > 0);
            if (!result.Succeeded)
            {
                string detail = result.Apply.Error ?? "未知错误";
                if (result.Apply.RollbackError is string rollback) detail += "\n\n" + rollback;
                else detail += "\n\n已完成的步骤都已回滚，网络保持原样。";
                _dialogs.ShowError("修改失败", detail);
                return;
            }
            ok = true;
            Pending = result.Pending;
            if (result.Pending is not null) PendingSeconds = (int)ChangeExecutor.ConfirmWindow.TotalSeconds;
            if (result.Verify is { Ok: false } verify)
                Notify("已应用，但验证发现问题", string.Join("\n", verify.Problems), NoticeLevel.Warning);
            else
                Notify("已应用", changeSet.Description, NoticeLevel.Success);
        });
        await RefreshAsync();
        await History.RefreshAsync();
        return ok;
    }

    public async Task UndoAsync(long operationId, string description)
    {
        if (Connection.Service is not { } service) return;
        await GuardAsync(async () =>
        {
            UndoPlan plan = await service.PreviewUndoAsync(operationId);
            if (!_dialogs.ConfirmUndo($"撤销 #{operationId}：{description}", plan)) return;
            ExecuteResult result = await service.ApplyUndoAsync(operationId);
            if (!result.Succeeded) _dialogs.ShowError("撤销失败", result.Apply.Error ?? "未知错误");
            else
            {
                Pending = result.Pending;
                Notify("已撤销", plan.HasConflicts ? "有冲突的项目已保留，没有改动。" : description, NoticeLevel.Success);
            }
        });
        await RefreshAsync();
        await History.RefreshAsync();
    }

    public void ShowError(string title, string message) => _dialogs.ShowError(title, message);

    public IDialogs Dialogs => _dialogs;

    public void Notify(string title, string message, NoticeLevel level)
    {
        var notice = new Notice(title, message, level);
        Notices.Add(notice);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(level == NoticeLevel.Error ? 15 : 8) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Notices.Remove(notice);
        };
        timer.Start();
    }

    [RelayCommand]
    private void CloseNotice(Notice notice) => Notices.Remove(notice);

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (RemoteInvocationException ex)
        {
            _dialogs.ShowError("服务拒绝了这个操作", ex.Message);
        }
        catch (ConnectionLostException)
        {
            _dialogs.ShowError("与服务的连接中断", "请稍后重试。如果修改已经开始，服务会根据日志自动处理。");
        }
    }

    private static void Ui(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else _ = dispatcher.BeginInvoke(action);
    }
}
