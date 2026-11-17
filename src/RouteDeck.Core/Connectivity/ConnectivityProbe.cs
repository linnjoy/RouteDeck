using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RouteDeck.Core.Abstractions;
using RouteDeck.Core.Lookup;
using RouteDeck.Core.Models;
using RouteDeck.Core.Proxy;

namespace RouteDeck.Core.Connectivity;

public enum ProbeLayer
{
    Route,
    Gateway,
    TcpDirect,
    TcpProxy,
    HttpDirect,
    HttpProxy,
}

public enum ProbeStatus
{
    Ok,
    Failed,
    Timeout,
    NotApplicable,
    Skipped,
}

public sealed record ProbeStep(ProbeLayer Layer, ProbeStatus Status, string Detail, TimeSpan? Elapsed = null)
{
    public string LayerLabel => Layer switch
    {
        ProbeLayer.Route => "路由",
        ProbeLayer.Gateway => "网关",
        ProbeLayer.TcpDirect => "TCP 直连",
        ProbeLayer.TcpProxy => "TCP 经代理",
        ProbeLayer.HttpDirect => "HTTP 直连",
        ProbeLayer.HttpProxy => "HTTP 经代理",
        _ => Layer.ToString(),
    };

    public bool IsOk => Status == ProbeStatus.Ok;

    public bool IsFailure => Status is ProbeStatus.Failed or ProbeStatus.Timeout;
}

/// <param name="Host">What the user typed (name or IP); used for HTTP Host and proxy CONNECT.</param>
/// <param name="Proxy">A proxy decision that uses a proxy, to compare "direct" with "via proxy"; null skips proxy layers.</param>
public sealed record ConnectivityRequest(string Host, IPAddress Address, int Port, ProxyDecision? Proxy = null, bool Http = false);

public sealed record ConnectivityReport(IReadOnlyList<ProbeStep> Steps, string Summary)
{
    public ProbeStep? Step(ProbeLayer layer) => Steps.FirstOrDefault(s => s.Layer == layer);
}

/// <summary>
/// Read-only, layered reachability check: route → gateway → TCP → HTTP, each shown separately.
/// Never changes configuration. Direct TCP is not bound to a source address so it follows the
/// same routing as any ordinary program.
/// </summary>
public sealed class ConnectivityProbe(INetworkApi network, TimeSpan? timeout = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(4);

    public static bool LooksLikeHttp(int port) => port is 80 or 81 or 443 or 8000 or 8008 or 8080 or 8081 or 8443 or 8888;

    public async Task<ConnectivityReport> RunAsync(NetworkSnapshot snapshot, ConnectivityRequest request, CancellationToken cancellationToken = default)
    {
        var steps = new List<ProbeStep>();

        LookupResult lookup = RouteLookup.Lookup(snapshot, request.Address);
        NetAdapter? exit = lookup.Winner is null ? null : snapshot.FindAdapter(lookup.Winner.InterfaceLuid);
        steps.Add(RouteStep(lookup, exit));
        steps.Add(await GatewayStepAsync(lookup.Winner, exit, request.Address, cancellationToken));

        ProbeStep tcpDirect = await TcpDirectAsync(request, snapshot, cancellationToken);
        steps.Add(tcpDirect);

        IPEndPoint? proxyEndpoint = ParseEndpoint(request.Proxy);
        ProbeStep tcpProxy = proxyEndpoint is null
            ? new ProbeStep(ProbeLayer.TcpProxy, ProbeStatus.Skipped, request.Proxy is null ? "该目标不经代理" : "代理地址不是本机可解析的 IP:端口")
            : await TcpViaProxyAsync(request, proxyEndpoint, cancellationToken);
        steps.Add(tcpProxy);

        if (request.Http)
        {
            steps.Add(tcpDirect.IsOk
                ? await HttpAsync(ProbeLayer.HttpDirect, request, null, cancellationToken)
                : new ProbeStep(ProbeLayer.HttpDirect, ProbeStatus.Skipped, "TCP 直连未成功，跳过"));
            if (proxyEndpoint is not null)
            {
                steps.Add(tcpProxy.IsOk
                    ? await HttpAsync(ProbeLayer.HttpProxy, request, proxyEndpoint, cancellationToken)
                    : new ProbeStep(ProbeLayer.HttpProxy, ProbeStatus.Skipped, "TCP 经代理未成功，跳过"));
            }
        }

        return new ConnectivityReport(steps, ConnectivitySummary.Summarize(steps, request.Port));
    }

    private static ProbeStep RouteStep(LookupResult lookup, NetAdapter? exit)
    {
        if (lookup.Winner is not Route winner)
            return new ProbeStep(ProbeLayer.Route, ProbeStatus.Failed, lookup.Explanation);

        string adapterName = exit?.Name ?? $"网卡 {winner.InterfaceIndex}";
        string via = winner.NextHop?.ToString() ?? "在链路上";
        string detail = $"{winner.Destination} → “{adapterName}”，网关 {via}。{lookup.Explanation}";
        if (exit?.TunnelManager is string manager) detail += $"（经 {manager} 隧道）";
        if (lookup.MismatchWithSystem) detail += " 注意：系统实际选择与计算结果不同。";
        return new ProbeStep(ProbeLayer.Route, ProbeStatus.Ok, detail);
    }

    private async Task<ProbeStep> GatewayStepAsync(Route? winner, NetAdapter? exit, IPAddress target, CancellationToken cancellationToken)
    {
        if (winner is null) return new ProbeStep(ProbeLayer.Gateway, ProbeStatus.Skipped, "没有路由");
        if (exit is { IsTunnelLike: true })
            return new ProbeStep(ProbeLayer.Gateway, ProbeStatus.NotApplicable, "隧道/点对点接口没有普通网关，不做邻居探测");
        if (exit is { Kind: AdapterKind.Loopback })
            return new ProbeStep(ProbeLayer.Gateway, ProbeStatus.NotApplicable, "本机回环");

        IPAddress neighbor = winner.NextHop ?? target;
        string what = winner.NextHop is null ? $"目标 {target}（同一网段）" : $"网关 {neighbor}";
        var watch = Stopwatch.StartNew();
        bool reachable = await network.ProbeNeighborAsync(neighbor, winner.InterfaceLuid, _timeout, cancellationToken);
        return reachable
            ? new ProbeStep(ProbeLayer.Gateway, ProbeStatus.Ok, $"{what} 有 ARP/NDP 响应", watch.Elapsed)
            : new ProbeStep(ProbeLayer.Gateway, ProbeStatus.Failed, $"{what} 没有 ARP/NDP 响应", watch.Elapsed);
    }

    private async Task<ProbeStep> TcpDirectAsync(ConnectivityRequest request, NetworkSnapshot snapshot, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var socket = new Socket(request.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        (ProbeStatus status, string detail) = await ConnectAsync(socket, new IPEndPoint(request.Address, request.Port), cancellationToken);
        if (status != ProbeStatus.Ok) return new ProbeStep(ProbeLayer.TcpDirect, status, detail, watch.Elapsed);

        var local = (IPEndPoint)socket.LocalEndPoint!;
        NetAdapter? adapter = snapshot.Adapters.FirstOrDefault(a => a.Addresses.Any(x => x.Address.Equals(local.Address)));
        string through = adapter is null ? "" : $"，出口“{adapter.Name}”";
        return new ProbeStep(ProbeLayer.TcpDirect, ProbeStatus.Ok, $"已连接 {request.Address}:{request.Port}，本地地址 {local.Address}{through}", watch.Elapsed);
    }

    private async Task<ProbeStep> TcpViaProxyAsync(ConnectivityRequest request, IPEndPoint proxy, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var socket = new Socket(proxy.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        (ProbeStatus status, string detail) = await ConnectAsync(socket, proxy, cancellationToken);
        if (status != ProbeStatus.Ok)
            return new ProbeStep(ProbeLayer.TcpProxy, status, $"连不上代理 {proxy}：{detail}", watch.Elapsed);

        string authority = $"{FormatHost(request.Host)}:{request.Port}";
        byte[] connect = Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout + _timeout);
        try
        {
            await socket.SendAsync(connect, SocketFlags.None, cts.Token);
            string statusLine = await ReadLineAsync(socket, cts.Token);
            string[] parts = statusLine.Split(' ', 3);
            if (parts.Length >= 2 && parts[1] == "200")
                return new ProbeStep(ProbeLayer.TcpProxy, ProbeStatus.Ok, $"代理 {proxy}{Owner(request.Proxy)} 已连通 {authority}", watch.Elapsed);
            return new ProbeStep(ProbeLayer.TcpProxy, ProbeStatus.Failed, $"代理 {proxy}{Owner(request.Proxy)} 拒绝或无法连接目标：{statusLine}", watch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeStep(ProbeLayer.TcpProxy, ProbeStatus.Timeout, $"代理 {proxy} 没有在 {(_timeout + _timeout).TotalSeconds:0} 秒内回应", watch.Elapsed);
        }
        catch (SocketException ex)
        {
            return new ProbeStep(ProbeLayer.TcpProxy, ProbeStatus.Failed, $"代理连接中断：{ex.SocketErrorCode}", watch.Elapsed);
        }
    }

    private async Task<ProbeStep> HttpAsync(ProbeLayer layer, ConnectivityRequest request, IPEndPoint? proxy, CancellationToken cancellationToken)
    {
        bool https = request.Port is 443 or 8443;
        string? certificateIssue = null;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = proxy is not null,
            Proxy = proxy is null ? null : new WebProxy($"http://{proxy}"),
            ConnectTimeout = _timeout,
            AllowAutoRedirect = false,
            SslOptions =
            {
                // Diagnostics only: report certificate problems instead of hiding the HTTP result.
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                {
                    if (errors != System.Net.Security.SslPolicyErrors.None) certificateIssue = errors.ToString();
                    return true;
                },
            },
        };
        using var client = new HttpClient(handler) { Timeout = _timeout + _timeout };
        var uri = new UriBuilder(https ? "https" : "http", request.Host, request.Port).Uri;

        var watch = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            string detail = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            if (response.Headers.Location is Uri location) detail += $"，跳转到 {location}";
            if (certificateIssue is not null) detail += $"（证书问题：{certificateIssue}）";
            return new ProbeStep(layer, ProbeStatus.Ok, detail, watch.Elapsed);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeStep(layer, ProbeStatus.Timeout, "TCP 已连通，但 HTTP 没有在时限内响应", watch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            return new ProbeStep(layer, ProbeStatus.Failed, $"HTTP 失败：{ex.InnerException?.Message ?? ex.Message}", watch.Elapsed);
        }
    }

    private async Task<(ProbeStatus, string)> ConnectAsync(Socket socket, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        try
        {
            await socket.ConnectAsync(endpoint, cts.Token);
            return (ProbeStatus.Ok, "");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (ProbeStatus.Timeout, $"{_timeout.TotalSeconds:0} 秒内没有响应（可能被防火墙丢弃，或回程路由不通）");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return (ProbeStatus.Failed, "连接被拒绝（目标端口没有服务在监听，或被主动拒绝）");
        }
        catch (SocketException ex)
        {
            return (ProbeStatus.Failed, $"连接失败：{ex.SocketErrorCode}");
        }
    }

    private static async Task<string> ReadLineAsync(Socket socket, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var buffer = new byte[1];
        while (line.Length < 512)
        {
            int read = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
            if (read == 0) break;
            char c = (char)buffer[0];
            if (c == '\n') break;
            if (c != '\r') line.Append(c);
        }
        return line.Length == 0 ? "（代理关闭了连接）" : line.ToString();
    }

    private static IPEndPoint? ParseEndpoint(ProxyDecision? decision)
    {
        if (decision is not { UsesProxy: true, Endpoint: string endpoint }) return null;
        string normalized = ProxyEvaluator.NormalizeEndpoint(endpoint);
        return IPEndPoint.TryParse(normalized, out IPEndPoint? parsed) && parsed.Port != 0 ? parsed : null;
    }

    private static string Owner(ProxyDecision? decision) => decision?.EndpointOwner is string owner ? $"（{owner}）" : "";

    private static string FormatHost(string host) =>
        IPAddress.TryParse(host, out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]" : host;
}
