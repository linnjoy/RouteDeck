using RouteDeck.Core.Proxy;

namespace RouteDeck.Tests.Proxy;

public class ProxyEvaluatorTests
{
    /// <summary>What Clash Verge typically writes when "system proxy" is on.</summary>
    private static readonly ProxyConfig ClashSystemProxy = new()
    {
        SystemProxyEnabled = true,
        SystemProxyServer = "127.0.0.1:7897",
        SystemProxyBypass = "localhost;127.*;192.168.*;<local>",
        EndpointOwners = new Dictionary<string, string> { ["127.0.0.1:7897"] = "verge-mihomo" },
    };

    [Fact]
    public void Field_address_outside_bypass_goes_to_clash()
    {
        ProxyDecision decision = ProxyEvaluator.Evaluate(ClashSystemProxy, "10.0.1.66", 81)[0];

        Assert.Equal(ProxyChannel.System, decision.Channel);
        Assert.True(decision.UsesProxy);
        Assert.Equal("127.0.0.1:7897", decision.Endpoint);
        Assert.Equal("verge-mihomo", decision.EndpointOwner);
    }

    [Theory]
    [InlineData("192.168.21.5", "192.168.*")]
    [InlineData("localhost", "本机地址（始终直连）")]
    [InlineData("127.0.0.1", "本机地址（始终直连）")]
    [InlineData("nas", "<local>")]
    public void Bypass_entries_match(string host, string entry)
    {
        ProxyDecision decision = ProxyEvaluator.EvaluateSystem(ClashSystemProxy, host, "http");
        Assert.False(decision.UsesProxy);
        Assert.Contains(entry, decision.Reason);
    }

    [Fact]
    public void Local_token_does_not_cover_ip_literals_or_dotted_names()
    {
        var config = ClashSystemProxy with { SystemProxyBypass = "<local>" };
        Assert.True(ProxyEvaluator.EvaluateSystem(config, "10.0.1.66", "http").UsesProxy);
        Assert.True(ProxyEvaluator.EvaluateSystem(config, "intranet.corp", "http").UsesProxy);
    }

    [Fact]
    public void Disabled_system_proxy_is_direct()
    {
        var config = ClashSystemProxy with { SystemProxyEnabled = false };
        Assert.False(ProxyEvaluator.EvaluateSystem(config, "10.0.1.66", "http").UsesProxy);
    }

    [Fact]
    public void Pac_is_reported_as_uncertain()
    {
        var config = ClashSystemProxy with { PacUrl = "http://127.0.0.1:7897/pac" };
        ProxyDecision decision = ProxyEvaluator.EvaluateSystem(config, "10.0.1.66", "http");
        Assert.True(decision.Uncertain);
        Assert.Contains("PAC", decision.Reason);
    }

    [Theory]
    [InlineData("http=10.1.1.1:8080;https=10.1.1.2:8443", "http", "10.1.1.1:8080")]
    [InlineData("http=10.1.1.1:8080;https=10.1.1.2:8443", "https", "10.1.1.2:8443")]
    [InlineData("socks=127.0.0.1:1080", "http", null)]
    [InlineData("http://127.0.0.1:7897", "https", "127.0.0.1:7897")]
    public void Per_scheme_servers(string server, string scheme, string? expected) =>
        Assert.Equal(expected, ProxyEvaluator.PickWinInetServer(server, scheme));

    [Fact]
    public void Environment_proxy_with_no_proxy()
    {
        var config = new ProxyConfig
        {
            Environment = new Dictionary<string, string>
            {
                ["HTTP_PROXY"] = "http://127.0.0.1:7897",
                ["NO_PROXY"] = "localhost,.corp.local,10.0.0.0/8",
            },
        };

        Assert.False(ProxyEvaluator.EvaluateEnvironment(config, "10.0.1.66", "http").UsesProxy);
        Assert.False(ProxyEvaluator.EvaluateEnvironment(config, "git.corp.local", "http").UsesProxy);
        Assert.False(ProxyEvaluator.EvaluateEnvironment(config, "corp.local", "http").UsesProxy);
        ProxyDecision viaProxy = ProxyEvaluator.EvaluateEnvironment(config, "api.anthropic.com", "http");
        Assert.True(viaProxy.UsesProxy);
        Assert.Equal("127.0.0.1:7897", viaProxy.Endpoint);
        Assert.False(ProxyEvaluator.EvaluateEnvironment(config, "api.anthropic.com", "https").UsesProxy);
    }

    [Fact]
    public void All_proxy_is_fallback()
    {
        var config = new ProxyConfig { Environment = new Dictionary<string, string> { ["ALL_PROXY"] = "socks5://127.0.0.1:7897" } };
        Assert.True(ProxyEvaluator.EvaluateEnvironment(config, "10.0.1.66", "https").UsesProxy);
    }

    [Fact]
    public void WinHttp_proxy_respects_its_bypass()
    {
        var config = new ProxyConfig { WinHttpProxy = "10.9.9.9:3128", WinHttpBypass = "10.*" };
        Assert.False(ProxyEvaluator.EvaluateWinHttp(config, "10.0.1.66").UsesProxy);
        Assert.True(ProxyEvaluator.EvaluateWinHttp(config, "example.com").UsesProxy);
    }

    [Fact]
    public void No_proxy_anywhere_is_direct_for_all_channels() =>
        Assert.All(ProxyEvaluator.Evaluate(ProxyConfig.None, "10.0.1.66", 81), d => Assert.False(d.UsesProxy));

    [Theory]
    [InlineData("localhost:7897", "127.0.0.1:7897")]
    [InlineData("http://user:pw@127.0.0.1:7897/", "127.0.0.1:7897")]
    public void Endpoints_normalize(string raw, string expected) =>
        Assert.Equal(expected, ProxyEvaluator.NormalizeEndpoint(raw));
}
