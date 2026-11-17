using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RouteDeck.Core.Changes;
using RouteDeck.Core.Models;
using UiButton = Wpf.Ui.Controls.Button;
using UiTextBox = Wpf.Ui.Controls.TextBox;

namespace RouteDeck.App.Services;

/// <summary>Plain WPF dialogs themed by WPF-UI's implicit styles.</summary>
public sealed class WindowDialogs(Window owner) : IDialogs
{
    private static readonly Brush WarningBrush = Frozen("#BA7517");
    private static readonly Brush ErrorBrush = Frozen("#E24B4A");

    public bool ConfirmChange(string title, IReadOnlyList<string> operations, IReadOnlyList<string> warnings, string confirmText)
    {
        var body = new StackPanel();
        foreach (string warning in warnings) body.Children.Add(Text("⚠ " + warning, WarningBrush, margin: new Thickness(0, 0, 0, 8)));
        body.Children.Add(Text("将执行以下操作：", muted: true, margin: new Thickness(0, 0, 0, 4)));
        foreach (string op in operations) body.Children.Add(Text("• " + op, margin: new Thickness(8, 0, 0, 2)));
        body.Children.Add(Text("每一步都会记录在操作历史中，可以随时撤销。", muted: true, margin: new Thickness(0, 12, 0, 0)));
        return Show(title, body, confirmText, primaryIsDanger: warnings.Count > 0) == true;
    }

    public void ShowError(string title, string message) => Show(title, Text(message), null);

    public bool ConfirmUndo(string title, UndoPlan plan)
    {
        var body = new StackPanel();
        if (plan.Items.Count == 0) body.Children.Add(Text("这次操作没有留下需要恢复的内容。"));
        foreach (UndoItem item in plan.Items)
        {
            (string label, Brush? brush) = item.State switch
            {
                UndoItemState.WillApply => ("将执行", null),
                UndoItemState.AlreadyDone => ("无需处理", null),
                _ => ("冲突，保留现状", WarningBrush),
            };
            body.Children.Add(Text($"[{label}] {item.Inverse.Describe()}", brush, margin: new Thickness(0, 0, 0, 2)));
            if (item.Note.Length > 0) body.Children.Add(Text(item.Note, muted: true, margin: new Thickness(16, 0, 0, 6)));
        }
        if (plan.HasConflicts)
            body.Children.Add(Text("标为冲突的项目在 RouteDeck 写入之后被其他程序或手动修改过，撤销时不会动它们。", WarningBrush, margin: new Thickness(0, 10, 0, 0)));
        return Show(title, body, "撤销") == true;
    }

    public RouteSpec? EditRoute(NetworkSnapshot snapshot, Route? existing)
    {
        var adapters = snapshot.Adapters.Where(a => a.Kind != AdapterKind.Loopback).OrderByDescending(a => a.IsUp).ThenBy(a => a.Name).ToList();
        var destination = new UiTextBox { PlaceholderText = "10.0.0.0/8 或 10.0.1.66", Text = existing?.Destination.ToString() ?? "" };
        var adapter = new ComboBox
        {
            ItemsSource = adapters.Select(a => $"{a.Name}{(a.IsUp ? "" : "（已断开）")}{(a.TunnelManager is null ? "" : $" · {a.TunnelManager}")}").ToList(),
            SelectedIndex = Math.Max(0, adapters.FindIndex(a => a.Luid == existing?.InterfaceLuid)),
        };
        var gateway = new UiTextBox { PlaceholderText = "留空表示在链路上（直连）", Text = existing?.NextHop?.ToString() ?? "" };
        var metric = new UiTextBox { Text = (existing?.Metric ?? 1).ToString() };
        var persistent = new CheckBox { Content = "持久（重启后保留）", IsChecked = existing?.InPersistentStore ?? false };
        var error = Text("", ErrorBrush);

        void SuggestGateway()
        {
            if (gateway.Text.Length > 0 || adapter.SelectedIndex < 0) return;
            NetAdapter a = adapters[adapter.SelectedIndex];
            gateway.PlaceholderText = a.Gateways.FirstOrDefault() is IPAddress g ? $"留空表示在链路上；该网卡网关是 {g}" : "留空表示在链路上（直连）";
        }
        adapter.SelectionChanged += (_, _) => SuggestGateway();
        SuggestGateway();

        var form = Form(("目标网段", destination), ("出口网卡", adapter), ("网关", gateway), ("路由跃点", metric), ("", persistent));
        form.Children.Add(error);

        RouteSpec? result = null;
        Show(existing is null ? "添加路由" : "编辑路由", form, "确定", validate: () =>
        {
            if (!IpPrefix.TryParse(destination.Text, out IpPrefix? prefix, out string? message)) return Fail(error, message!);
            if (adapter.SelectedIndex < 0) return Fail(error, "选择出口网卡");
            NetAdapter a = adapters[adapter.SelectedIndex];
            IPAddress? hop = null;
            if (gateway.Text.Trim().Length > 0)
            {
                if (!IPAddress.TryParse(gateway.Text.Trim(), out hop)) return Fail(error, "网关不是有效的 IP 地址");
                if (hop.AddressFamily != prefix.Value.Family) return Fail(error, "网关和目标必须同为 IPv4 或 IPv6");
                if (!a.IsTunnelLike && !hop.IsIPv6LinkLocal && a.Addresses.Count > 0 && !a.IsOnLink(hop))
                    return Fail(error, $"网关 {hop} 不在“{a.Name}”的子网内（{string.Join("、", a.Addresses.Where(x => x.Address.AddressFamily == hop.AddressFamily))}）");
            }
            if (!int.TryParse(metric.Text, out int m) || m is < 0 or > 9999) return Fail(error, "跃点必须是 0 到 9999 之间的整数");
            result = new RouteSpec(prefix.Value, hop, a.Luid, m, persistent.IsChecked == true);
            return true;
        });
        return result;
    }

    public BypassRequest? Bypass(NetworkSnapshot snapshot, string? presetTarget, ulong? tunnelLuid)
    {
        IReadOnlyList<BypassExit> exits = BypassPlanner.Exits(snapshot, IpPrefix.DefaultV4);
        if (exits.Count == 0)
        {
            ShowError("没有可用的出口", "没有已连接、带网关的物理网卡（有线或 Wi-Fi），无法添加绕过路由。");
            return null;
        }

        string tunnelName = tunnelLuid is ulong luid && snapshot.FindAdapter(luid) is { } t ? $"“{t.Name}”" : "隧道";
        var target = new UiTextBox { PlaceholderText = "10.0.1.66 或 10.0.1.0/24", Text = presetTarget ?? "" };
        var exit = new ComboBox
        {
            ItemsSource = exits.Select(e => $"{e.Adapter.Name} · {string.Join("、", e.Adapter.Addresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))}").ToList(),
            SelectedIndex = 0,
        };
        var gateway = new UiTextBox { Text = exits[0].Gateway?.ToString() ?? "" };
        exit.SelectionChanged += (_, _) => gateway.Text = exits[Math.Max(0, exit.SelectedIndex)].Gateway?.ToString() ?? "";
        var persistent = new CheckBox { Content = "持久（重启后保留）。建议先用临时路由试用。", IsChecked = false };
        var error = Text("", ErrorBrush);

        var form = new StackPanel();
        form.Children.Add(Text($"为指定目标添加一条经物理网卡的路由，让它不经过{tunnelName}。不会修改隧道软件自己的路由；撤销时只删除这一条。", muted: true, margin: new Thickness(0, 0, 0, 12)));
        StackPanel fields = Form(("目标", target), ("出口网卡", exit), ("网关（可改成现场网关）", gateway), ("", persistent));
        form.Children.Add(fields);
        form.Children.Add(error);

        BypassRequest? result = null;
        Show("让目标绕过隧道直连", form, "预览", validate: () =>
        {
            if (!IpPrefix.TryParse(target.Text, out IpPrefix? prefix, out string? message)) return Fail(error, message!);
            IPAddress? hop = null;
            if (gateway.Text.Trim().Length > 0 && !IPAddress.TryParse(gateway.Text.Trim(), out hop)) return Fail(error, "网关不是有效的 IP 地址");
            BypassExit chosen = exits[Math.Max(0, exit.SelectedIndex)];
            try
            {
                BypassPlanner.Plan(snapshot, prefix.Value, chosen.Adapter.Luid, hop, persistent.IsChecked == true);
            }
            catch (ArgumentException ex)
            {
                return Fail(error, ex.Message);
            }
            result = new BypassRequest(prefix.Value, chosen.Adapter.Luid, hop, persistent.IsChecked == true);
            return true;
        });
        return result;
    }

    private bool? Show(string title, UIElement body, string? primaryText, bool primaryIsDanger = false, Func<bool>? validate = null)
    {
        var window = new Window
        {
            Title = title,
            Owner = owner,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 720,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        window.SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");
        window.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        if (primaryText is not null)
        {
            var cancel = new UiButton { Content = "取消", Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            cancel.Click += (_, _) => window.DialogResult = false;
            buttons.Children.Add(cancel);
        }
        var primary = new UiButton
        {
            Content = primaryText ?? "确定",
            IsDefault = true,
            Appearance = primaryIsDanger ? Wpf.Ui.Controls.ControlAppearance.Caution : Wpf.Ui.Controls.ControlAppearance.Primary,
        };
        primary.Click += (_, _) =>
        {
            if (validate is null || validate()) window.DialogResult = true;
        };
        buttons.Children.Add(primary);

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new ScrollViewer { Content = body, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(buttons);
        window.Content = root;
        return window.ShowDialog();
    }

    private static StackPanel Form(params (string Label, FrameworkElement Field)[] rows)
    {
        var panel = new StackPanel();
        foreach ((string label, FrameworkElement field) in rows)
        {
            if (label.Length > 0) panel.Children.Add(Text(label, muted: true, margin: new Thickness(0, 6, 0, 2)));
            field.Margin = new Thickness(0, label.Length > 0 ? 0 : 8, 0, 0);
            panel.Children.Add(field);
        }
        return panel;
    }

    private static bool Fail(TextBlock error, string message)
    {
        error.Text = message;
        error.Margin = new Thickness(0, 10, 0, 0);
        return false;
    }

    private static TextBlock Text(string text, Brush? brush = null, bool muted = false, Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
        if (brush is not null) block.Foreground = brush;
        else if (muted) block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
