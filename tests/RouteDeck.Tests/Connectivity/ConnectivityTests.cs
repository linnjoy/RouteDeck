using System.Net;
using System.Net.Sockets;
using System.Text;
using RouteDeck.Core.Connectivity;
using RouteDeck.Core.Models;
using RouteDeck.Core.Proxy;
using RouteDeck.Tests.Fakes;

namespace RouteDeck.Tests.Connectivity;

public class ConnectivitySummaryTests
{
    private static ProbeStep S(ProbeLayer layer, ProbeStatus status) => new(layer, status, "");

    [Fact]
    public void Gateway_ok_but_port_timeout_blames_service_not_route()
    {
        string summary = ConnectivitySummary.Summarize(
        [
            S(ProbeLayer.Route, ProbeStatus.Ok),
            S(ProbeLayer.Gateway, ProbeStatus.Ok),
            S(ProbeLayer.TcpDirect, ProbeStatus.Timeout),
            S(ProbeLayer.TcpProxy, ProbeStatus.Skipped),
        ], 81);

        Assert.StartsWith("路由正常、网关可达，但 TCP 81 超时", summary);
        Assert.Contains("不建议因此删除路由", summary);
    }

    [Fact]
    public void Direct_ok_proxy_failed_blames_proxy()
    {
        string summary = ConnectivitySummary.Summarize(
        [
            S(ProbeLayer.Route, ProbeStatus.Ok),
            S(ProbeLayer.Gateway, ProbeStatus.Ok),
            S(ProbeLayer.TcpDirect, ProbeStatus.Ok),
            S(ProbeLayer.TcpProxy, ProbeStatus.Failed),
        ], 81);

        Assert.Contains("问题在代理", summary);
    }

    [Fact]
    public void Proxy_ok_direct_failed()
    {
        string summary = ConnectivitySummary.Summarize(
        [
            S(ProbeLayer.Route, ProbeStatus.Ok),
            S(ProbeLayer.Gateway, ProbeStatus.Ok),
            S(ProbeLayer.TcpDirect, ProbeStatus.Timeout),
            S(ProbeLayer.TcpProxy, ProbeStatus.Ok),
        ], 81);

        Assert.Contains("经代理可达", summary);
    }

    [Fact]
    public void Gateway_silent()
    {
        string summary = ConnectivitySummary.Summarize(
        [
            S(ProbeLayer.Route, ProbeStatus.Ok),
            S(ProbeLayer.Gateway, ProbeStatus.Failed),
            S(ProbeLayer.TcpDirect, ProbeStatus.Timeout),
        ], 81);

        Assert.StartsWith("网关没有响应", summary);
    }

    [Fact]
    public void Tunnel_path_failure()
    {
        string summary = ConnectivitySummary.Summarize(
        [
            S(ProbeLayer.Route, ProbeStatus.Ok),
            S(ProbeLayer.Gateway, ProbeStatus.NotApplicable),
            S(ProbeLayer.TcpDirect, ProbeStatus.Failed),
        ], 443);

        Assert.StartsWith("经隧道或点对点接口转发", summary);
    }

    [Fact]
    public void No_route() =>
        Assert.StartsWith("没有可用的路由", ConnectivitySummary.Summarize([S(ProbeLayer.Route, ProbeStatus.Failed), S(ProbeLayer.TcpDirect, ProbeStatus.Failed)], 81));
}

/// <summary>Real sockets on 127.0.0.1 only; nothing leaves the machine.</summary>
public sealed class ConnectivityProbeTests : IDisposable
{
    private readonly FakeNetworkApi _net = new();
    private readonly List<TcpListener> _listeners = [];
    private readonly CancellationTokenSource _cts = new();

    public ConnectivityProbeTests()
    {
        _net.AddAdapter(1, "Loopback Pseudo-Interface 1", AdapterKind.Loopback, address: "127.0.0.1/8", metric: 75);
        _net.AddExisting("127.0.0.0/8", null, 1);
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (TcpListener listener in _listeners) listener.Stop();
    }

    private ConnectivityProbe Probe() => new(_net, TimeSpan.FromSeconds(4));

    private Task<ConnectivityReport> RunAsync(int port, ProxyDecision? proxy = null, bool http = false) =>
        Probe().RunAsync(_net.GetSnapshot(), new ConnectivityRequest("127.0.0.1", IPAddress.Loopback, port, proxy, http));

    /// <summary>Starts a loopback server that answers every connection with <paramref name="respond"/>(request).</summary>
    private int Serve(Func<string, string> respond)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(_cts.Token); }
                catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        NetworkStream stream = client.GetStream();
                        var request = new StringBuilder();
                        var buffer = new byte[1024];
                        while (!request.ToString().Contains("\r\n\r\n"))
                        {
                            int read = await stream.ReadAsync(buffer);
                            if (read == 0) return;
                            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        }
                        byte[] reply = Encoding.ASCII.GetBytes(respond(request.ToString()));
                        await stream.WriteAsync(reply);
                    }
                });
            }
        });
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static ProxyDecision ProxyAt(int port) =>
        new(ProxyChannel.System, true, $"127.0.0.1:{port}", "test") { EndpointOwner = "fake-proxy" };

    [Fact]
    public async Task Open_port_with_http()
    {
        int port = Serve(_ => "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        ConnectivityReport report = await RunAsync(port, http: true);

        Assert.Equal(ProbeStatus.Ok, report.Step(ProbeLayer.Route)!.Status);
        Assert.Equal(ProbeStatus.NotApplicable, report.Step(ProbeLayer.Gateway)!.Status);
        Assert.Equal(ProbeStatus.Ok, report.Step(ProbeLayer.TcpDirect)!.Status);
        Assert.Contains("本地地址 127.0.0.1", report.Step(ProbeLayer.TcpDirect)!.Detail);
        Assert.Equal("HTTP 204 No Content", report.Step(ProbeLayer.HttpDirect)!.Detail);
        Assert.Equal(ProbeStatus.Skipped, report.Step(ProbeLayer.TcpProxy)!.Status);
    }

    [Fact]
    public async Task Closed_port_is_refused()
    {
        ConnectivityReport report = await RunAsync(ClosedPort(), http: true);

        Assert.Equal(ProbeStatus.Failed, report.Step(ProbeLayer.TcpDirect)!.Status);
        Assert.Contains("连接被拒绝", report.Step(ProbeLayer.TcpDirect)!.Detail);
        Assert.Equal(ProbeStatus.Skipped, report.Step(ProbeLayer.HttpDirect)!.Status);
    }

    [Fact]
    public async Task Proxy_connect_success_and_http_via_proxy()
    {
        int target = Serve(_ => "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        int proxy = Serve(request => request.StartsWith("CONNECT")
            ? "HTTP/1.1 200 Connection established\r\n\r\n"
            : "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        ConnectivityReport report = await RunAsync(target, ProxyAt(proxy), http: true);

        Assert.Equal(ProbeStatus.Ok, report.Step(ProbeLayer.TcpProxy)!.Status);
        Assert.Contains("fake-proxy", report.Step(ProbeLayer.TcpProxy)!.Detail);
        Assert.Equal(ProbeStatus.Ok, report.Step(ProbeLayer.HttpProxy)!.Status);
    }

    [Fact]
    public async Task Proxy_refusing_target_is_reported_as_proxy_problem()
    {
        int target = Serve(_ => "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        int proxy = Serve(_ => "HTTP/1.1 502 Bad Gateway\r\n\r\n");

        ConnectivityReport report = await RunAsync(target, ProxyAt(proxy));

        Assert.Equal(ProbeStatus.Failed, report.Step(ProbeLayer.TcpProxy)!.Status);
        Assert.Contains("502", report.Step(ProbeLayer.TcpProxy)!.Detail);
        Assert.Contains("问题在代理", report.Summary);
    }

    [Fact]
    public async Task Ethernet_gateway_probe_uses_network_api()
    {
        _net.AddAdapter(14, "以太网 2", address: "192.168.21.230/24", metric: 35, gateway: "192.168.21.1");
        _net.AddExisting("10.0.1.66/32", "192.168.21.1", 14);
        _net.UnreachableNeighbors.Add(IPAddress.Parse("192.168.21.1"));
        var request = new ConnectivityRequest("10.0.1.66", IPAddress.Parse("10.0.1.66"), ClosedPort());

        // Only the route and gateway layers matter here; TCP goes to the real stack and may fail any way it likes.
        ConnectivityReport report = await new ConnectivityProbe(_net, TimeSpan.FromMilliseconds(300)).RunAsync(_net.GetSnapshot(), request);

        Assert.Contains("“以太网 2”，网关 192.168.21.1", report.Step(ProbeLayer.Route)!.Detail);
        Assert.Equal(ProbeStatus.Failed, report.Step(ProbeLayer.Gateway)!.Status);
    }
}
