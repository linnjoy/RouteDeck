using System.Net;
using RouteDeck.Core.Lookup;
using RouteDeck.Core.Models;
using RouteDeck.Tests.Fakes;

namespace RouteDeck.Tests.Lookup;

public class RouteLookupTests
{
    private readonly FakeNetworkApi _net = new();

    public RouteLookupTests()
    {
        _net.AddAdapter(1, "以太网", address: "10.20.1.15/24", metric: 25);
        _net.AddAdapter(2, "WLAN", AdapterKind.Wifi, address: "192.168.8.3/24", metric: 10);
    }

    private LookupResult Lookup(string address) => RouteLookup.Lookup(_net.GetSnapshot(), IPAddress.Parse(address));

    [Fact]
    public void Longest_prefix_wins()
    {
        _net.AddExisting("0.0.0.0/0", "192.168.8.1", 2);
        _net.AddExisting("10.0.0.0/8", "10.20.1.1", 1, metric: 100);

        LookupResult result = Lookup("10.5.5.5");

        Assert.Equal(IpPrefix.Parse("10.0.0.0/8"), result.Winner!.Destination);
        Assert.Contains("/8 比 /0 更具体", result.Explanation);
        Assert.Equal("/0 不如 /8 具体", result.Candidates.Single(c => !c.IsWinner).Reason);
    }

    [Fact]
    public void Lower_effective_metric_breaks_tie()
    {
        _net.AddExisting("0.0.0.0/0", "10.20.1.1", 1, metric: 0);     // 0 + 25
        _net.AddExisting("0.0.0.0/0", "192.168.8.1", 2, metric: 10);  // 10 + 10

        LookupResult result = Lookup("8.8.8.8");

        Assert.Equal(2UL, result.Winner!.InterfaceLuid);
        Assert.Equal("同为 /0，有效跃点 20 小于 25。", result.Explanation);
        Assert.Equal("有效跃点 25 高于 20", result.Candidates[1].Reason);
    }

    [Fact]
    public void Equal_metrics_are_reported()
    {
        _net.AddExisting("0.0.0.0/0", "10.20.1.1", 1, metric: 0);
        _net.AddExisting("0.0.0.0/0", "192.168.8.1", 2, metric: 15);

        Assert.Contains("分担流量", Lookup("8.8.8.8").Explanation);
    }

    [Fact]
    public void Routes_on_disconnected_adapters_are_skipped()
    {
        _net.AddAdapter(3, "USB 网卡", up: false, address: "172.16.0.2/24", metric: 1);
        _net.AddExisting("0.0.0.0/0", "192.168.8.1", 2);
        _net.AddExisting("172.16.0.0/16", null, 3);

        LookupResult result = Lookup("172.16.9.9");

        Assert.Equal(2UL, result.Winner!.InterfaceLuid);
        LookupCandidate skipped = result.Candidates.Single(c => !c.IsEligible);
        Assert.Equal("网卡“USB 网卡”已断开", skipped.Reason);
    }

    [Fact]
    public void Persistent_only_routes_are_not_active()
    {
        _net.Routes.Add(new Route
        {
            Destination = IpPrefix.Parse("10.9.0.0/16"),
            InterfaceLuid = 1,
            InActiveStore = false,
            InPersistentStore = true,
        });

        LookupResult result = Lookup("10.9.1.1");

        Assert.Null(result.Winner);
        Assert.Contains("没有路由", result.Explanation);
        Assert.Equal("仅在持久存储中，当前未生效", result.Candidates.Single().Reason);
    }

    [Fact]
    public void Ipv6_lookup_ignores_ipv4_routes()
    {
        _net.AddExisting("0.0.0.0/0", "192.168.8.1", 2);
        _net.AddExisting("::/0", "fe80::1", 2);
        _net.AddExisting("2001:db8::/32", null, 1);

        LookupResult result = Lookup("2001:db8::99");

        Assert.Equal(IpPrefix.Parse("2001:db8::/32"), result.Winner!.Destination);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Single_match_is_explained()
    {
        _net.AddExisting("0.0.0.0/0", "192.168.8.1", 2);
        Assert.Equal("这是唯一匹配的路由。", Lookup("1.1.1.1").Explanation);
    }

    [Fact]
    public void Mismatch_with_system_is_flagged()
    {
        _net.AddExisting("0.0.0.0/0", "192.168.8.1", 2);
        LookupResult result = Lookup("1.1.1.1");

        Assert.False((result with { SystemChoice = result.Winner!.Key }).MismatchWithSystem);
        Assert.True((result with { SystemChoice = new RouteKey(IpPrefix.DefaultV4, 1, null) }).MismatchWithSystem);
    }
}
