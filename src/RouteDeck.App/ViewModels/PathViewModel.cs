using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RouteDeck.Core.Connectivity;
using RouteDeck.Core.Lookup;
using RouteDeck.Core.Models;
using RouteDeck.Core.Proxy;

namespace RouteDeck.App.ViewModels;

public sealed record CandidateRow(string Destination, string Adapter, string Gateway, int EffectiveMetric, string Reason, bool IsWinner);

public sealed record ProxyRow(string Channel, string Verdict, string Reason, bool UsesProxy, bool Uncertain);

public sealed record ProbeRow(string Layer, string Status, string Detail, string Elapsed, ProbeStatus Kind);

/// <summary>Explains where traffic to a target goes: system route, proxy channels, and layered reachability.</summary>
public sealed partial class PathViewModel(MainViewModel main) : ObservableObject
{
    private static readonly IpPrefix FakeIpRange = IpPrefix.Parse("198.18.0.0/15");

    [ObservableProperty]
    private string _target = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _routeSummary;

    [ObservableProperty]
    private string? _explanation;

    [ObservableProperty]
    private string? _resolvedNote;

    [ObservableProperty]
    private string? _systemMismatch;

    [ObservableProperty]
    private IReadOnlyList<CandidateRow> _candidates = [];

    [ObservableProperty]
    private IReadOnlyList<ProxyRow> _proxyRows = [];

    [ObservableProperty]
    private string? _proxyConclusion;

    [ObservableProperty]
    private IReadOnlyList<ProbeRow> _probeRows = [];

    [ObservableProperty]
    private string? _probeSummary;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    private IPAddress? _address;
    private string _host = "";
    private int _port;
    private ProxyDecision? _proxyForProbe;

    public bool HasResult => RouteSummary is not null;

    public string PortText => _port > 0 ? _port.ToString() : "";

    [RelayCommand]
    private async Task LookupAsync()
    {
        Error = null;
        ProbeRows = [];
        ProbeSummary = null;
        if (!TryParseTarget(Target, out _host, out _port, out string? parseError))
        {
            Error = parseError;
            RouteSummary = null;
            return;
        }

        IsBusy = true;
        try
        {
            IPAddress[] addresses = IPAddress.TryParse(_host, out IPAddress? literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(_host);
            _address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            if (_address is null)
            {
                Error = $"无法解析 {_host}";
                RouteSummary = null;
                return;
            }

            ResolvedNote = addresses.Length > 1 || literal is null
                ? $"{_host} 解析为 {string.Join("、", addresses.Select(a => a.ToString()))}，下面按 {_address} 分析。"
                : null;
            if (FakeIpRange.Contains(_address))
                ResolvedNote = (ResolvedNote ?? "") + $" {_address} 是 Clash/Mihomo 的 fake-ip 地址：实际目标和出口由 Clash 规则决定，系统路由只负责把它交给 TUN 网卡。";

            NetworkSnapshot snapshot = main.Snapshot;
            LookupResult result = RouteLookup.Lookup(snapshot, _address) with
            {
                SystemChoice = main.Connection.LocalNetwork.GetBestRoute(_address),
            };
            ShowRoute(snapshot, result);
            ShowProxy();
            if (result.Winner is not null) main.Routes.Highlight(result.Winner.Key);
        }
        catch (SocketException ex)
        {
            Error = $"无法解析 {_host}：{ex.Message}";
            RouteSummary = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ProbeAsync()
    {
        if (_address is null) return;
        if (_port <= 0)
        {
            Error = "连通性测试需要端口，例如 10.0.1.66:81";
            return;
        }
        Error = null;
        IsBusy = true;
        ProbeRows = [];
        ProbeSummary = "正在测试…";
        try
        {
            var probe = new ConnectivityProbe(main.Connection.LocalNetwork);
            var request = new ConnectivityRequest(_host, _address, _port, _proxyForProbe, ConnectivityProbe.LooksLikeHttp(_port));
            ConnectivityReport report = await probe.RunAsync(main.Snapshot, request);
            ProbeRows = report.Steps.Select(s => new ProbeRow(
                s.LayerLabel,
                s.Status switch
                {
                    ProbeStatus.Ok => "通过",
                    ProbeStatus.Failed => "失败",
                    ProbeStatus.Timeout => "超时",
                    ProbeStatus.NotApplicable => "不适用",
                    _ => "跳过",
                },
                s.Detail,
                s.Elapsed is { } e ? $"{e.TotalMilliseconds:0} ms" : "",
                s.Status)).ToList();
            ProbeSummary = report.Summary;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void AddBypass()
    {
        if (_address is null) return;
        int length = _address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        main.Routes.Bypass(new IpPrefix(_address, length).ToString(), null);
    }

    [RelayCommand]
    private void ShowInTable()
    {
        main.SelectedNav = main.NavItems[1];
    }

    private void ShowRoute(NetworkSnapshot snapshot, LookupResult result)
    {
        string Adapter(Route r) => snapshot.FindAdapter(r.InterfaceLuid)?.Name ?? $"网卡 {r.InterfaceIndex}";

        if (result.Winner is Route winner)
        {
            NetAdapter? exit = snapshot.FindAdapter(winner.InterfaceLuid);
            string tunnel = exit?.TunnelManager is string manager ? $"，经 {manager} 隧道" : "";
            RouteSummary = $"{_address} → 路由 {winner.Destination} → “{Adapter(winner)}”，网关 {winner.NextHop?.ToString() ?? "在链路上"}{tunnel}";
        }
        else
        {
            RouteSummary = $"{_address} 没有匹配的路由";
        }
        Explanation = result.Explanation;
        SystemMismatch = result.MismatchWithSystem && result.SystemChoice is RouteKey choice
            ? $"注意：Windows 实际选择的是 {choice.Destination}（{snapshot.FindAdapter(choice.InterfaceLuid)?.Name}），和按路由表计算的结果不同，可能存在策略路由或其他软件的过滤驱动。"
            : null;
        Candidates = result.Candidates.Select(c => new CandidateRow(
            c.Route.Destination.ToString(),
            Adapter(c.Route),
            c.Route.NextHop?.ToString() ?? "在链路上",
            c.Route.EffectiveMetric,
            c.Reason,
            c.IsWinner)).ToList();
    }

    private void ShowProxy()
    {
        ProxyConfig config = ProxyReader.Read();
        IReadOnlyList<ProxyDecision> decisions = ProxyEvaluator.Evaluate(config, _host, _port > 0 ? _port : 80);
        ProxyRows = decisions.Select(d => new ProxyRow(
            d.ChannelLabel,
            d.Uncertain ? "不确定" : d.UsesProxy ? $"经代理 {d.Endpoint}{(d.EndpointOwner is null ? "" : $"（{d.EndpointOwner}）")}" : "直接连接",
            d.Reason,
            d.UsesProxy,
            d.Uncertain)).ToList();

        _proxyForProbe = decisions.FirstOrDefault(d => d.Channel == ProxyChannel.System && d.UsesProxy)
                         ?? decisions.FirstOrDefault(d => d.UsesProxy);

        ProxyDecision system = decisions[0];
        var proxied = decisions.Where(d => d.UsesProxy).ToList();
        ProxyConclusion = proxied.Count switch
        {
            0 when system.Uncertain => "系统代理使用 PAC，浏览器是否走代理取决于脚本内容。",
            0 => "所有程序都直接连接：访问效果只取决于上面的系统路由。",
            _ when !system.UsesProxy =>
                $"浏览器直接连接（走系统路由），但{string.Join("、", proxied.Select(d => d.ChannelLabel))}会经代理。同一个地址在不同程序里可能表现不同。",
            _ => $"浏览器会把请求交给代理 {system.Endpoint}：此时系统路由是否正确不决定这次访问，问题可能在代理规则或绕过列表。",
        };
    }

    /// <summary>Accepts "10.0.1.66", "10.0.1.66:81", "[2001:db8::1]:443", "host:8080", "http://host:81/path".</summary>
    internal static bool TryParseTarget(string text, out string host, out int port, out string? error)
    {
        host = "";
        port = 0;
        error = null;
        text = text.Trim();
        if (text.Length == 0)
        {
            error = "输入 IP、域名或 IP:端口";
            return false;
        }

        if (text.Contains("://") && Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
        {
            host = uri.Host.Trim('[', ']');
            port = uri.Port;
            return true;
        }
        if (IPAddress.TryParse(text, out IPAddress? bare) && !text.StartsWith('['))
        {
            host = bare.ToString();
            return true;
        }

        int colon = text.LastIndexOf(':');
        if (colon > 0 && (text.StartsWith('[') || text.IndexOf(':') == colon))
        {
            string portText = text[(colon + 1)..];
            if (!int.TryParse(portText, out port) || port is < 1 or > 65535)
            {
                error = "端口必须是 1 到 65535 之间的数字";
                return false;
            }
            host = text[..colon].Trim('[', ']');
        }
        else
        {
            host = text;
        }
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            error = "不是有效的地址或域名";
            return false;
        }
        return true;
    }
}
