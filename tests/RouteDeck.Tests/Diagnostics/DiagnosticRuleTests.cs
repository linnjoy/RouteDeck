using System.Net;
using RouteDeck.Core.Changes;
using RouteDeck.Core.Diagnostics;
using RouteDeck.Core.Lookup;
using RouteDeck.Core.Models;
using RouteDeck.Tests.Fakes;

namespace RouteDeck.Tests.Diagnostics;

/// <summary>Modeled on the field setup: wired + Wi-Fi + Clash TUN + Tailscale.</summary>
public class DiagnosticRuleTests
{
    private const ulong Wired = 14, Wifi = 17, Mihomo = 4, Tailscale = 8;
    private readonly FakeNetworkApi _net = new();

    public DiagnosticRuleTests()
    {
        _net.AddAdapter(Wired, "以太网 2", address: "192.168.21.230/24", metric: 35, gateway: "192.168.21.1");
        _net.AddAdapter(Wifi, "WLAN", AdapterKind.Wifi, address: "192.168.8.3/24", metric: 10, gateway: "192.168.8.1");
        _net.AddAdapter(Mihomo, "Mihomo", AdapterKind.Tunnel, address: "198.18.0.1/30", metric: 0, manager: "Mihomo/Clash");
        _net.AddAdapter(Tailscale, "Tailscale", AdapterKind.Tunnel, address: "100.68.50.111/32", metric: 5, manager: "Tailscale");

        _net.AddExisting("0.0.0.0/0", "192.168.21.1", Wired, metric: 256, persistent: true);
        _net.AddExisting("0.0.0.0/0", "192.168.8.1", Wifi, metric: 10);
        _net.AddExisting("100.64.0.0/10", null, Tailscale);
    }

    private void AddClashTun()
    {
        _net.AddExisting("0.0.0.0/1", null, Mihomo);
        _net.AddExisting("128.0.0.0/1", null, Mihomo);
    }

    private IReadOnlyList<DiagnosticFinding> Run(IDiagnosticRule rule) => DiagnosticEngine.Run(_net.GetSnapshot(), [rule]);

    [Fact]
    public void Tunnel_takeover_is_informational_and_offers_no_fix()
    {
        AddClashTun();

        DiagnosticFinding finding = Assert.Single(Run(new TunnelTakeoverRule()));

        Assert.Equal(FindingLevel.Info, finding.Level);
        Assert.Equal("Mihomo/Clash 接管了 IPv4 公网流量", finding.Title);
        Assert.Null(finding.Fix);
        Assert.Equal(Mihomo, finding.TunnelLuid);
        Assert.Equal(2, finding.Routes.Count);
    }

    [Fact]
    public void Takeover_detected_with_split_public_prefixes()
    {
        _net.AddExisting("8.0.0.0/7", null, Mihomo);

        DiagnosticFinding finding = Assert.Single(Run(new TunnelTakeoverRule()));
        Assert.Equal(IpPrefix.Parse("8.0.0.0/7"), Assert.Single(finding.Routes).Destination);
    }

    [Fact]
    public void No_takeover_when_physical_default_wins()
    {
        Assert.Empty(Run(new TunnelTakeoverRule()));
    }

    [Fact]
    public void Tailscale_subnet_route_is_not_a_takeover()
    {
        Assert.DoesNotContain(Run(new TunnelTakeoverRule()), f => f.TunnelLuid == Tailscale);
    }

    [Fact]
    public void Bypass_adds_one_owned_host_route_and_never_touches_the_tunnel()
    {
        AddClashTun();
        NetworkSnapshot snapshot = _net.GetSnapshot();
        IpPrefix target = IpPrefix.Parse("10.0.1.66/32");

        ChangeSet plan = BypassPlanner.Plan(snapshot, target, Wired, IPAddress.Parse("192.168.21.1"));

        RouteOperation op = Assert.Single(plan.Operations);
        Assert.Equal(RouteOperationKind.Add, op.Kind);
        Assert.Equal(Wired, op.Route.InterfaceLuid);
        Assert.Equal(IPAddress.Parse("192.168.21.1"), op.Route.NextHop);

        _net.AddRoute(op.Route);
        Route winner = RouteLookup.Lookup(_net.GetSnapshot(), IPAddress.Parse("10.0.1.66")).Winner!;
        Assert.Equal(Wired, winner.InterfaceLuid);
        Assert.Equal(Mihomo, RouteLookup.Lookup(_net.GetSnapshot(), IPAddress.Parse("8.8.8.8")).Winner!.InterfaceLuid);
    }

    [Fact]
    public void Bypass_uses_adapter_gateway_by_default_and_lists_physical_exits_only()
    {
        NetworkSnapshot snapshot = _net.GetSnapshot();
        IpPrefix target = IpPrefix.Parse("10.0.1.66/32");

        Assert.Equal(IPAddress.Parse("192.168.8.1"), BypassPlanner.Plan(snapshot, target, Wifi).Operations[0].Route.NextHop);
        Assert.Equal([Wired, Wifi], BypassPlanner.Exits(snapshot, target).Select(e => e.Adapter.Luid).Order());
    }

    [Theory]
    [InlineData(Mihomo, null, "隧道")]
    [InlineData(Wired, "192.168.8.1", "不在")]
    [InlineData(Wired, "fe80::1", "同为")]
    public void Bypass_rejects_bad_exits(ulong exit, string? gateway, string message)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            BypassPlanner.Plan(_net.GetSnapshot(), IpPrefix.Parse("10.0.1.66/32"), exit, gateway is null ? null : IPAddress.Parse(gateway)));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Duplicate_default_metric_detected_without_fix_for_foreign_routes()
    {
        _net.Routes.RemoveAll(r => r.InterfaceLuid == Wifi);
        _net.AddExisting("0.0.0.0/0", "192.168.8.1", Wifi, metric: 281); // 281 + 10 == 256 + 35

        DiagnosticFinding finding = Assert.Single(Run(new DuplicateDefaultMetricRule()));
        Assert.Equal(FindingLevel.Notice, finding.Level);
        Assert.Contains("291", finding.Detail);
        Assert.Null(finding.Fix);
    }

    [Fact]
    public void Duplicate_default_metric_fix_only_changes_owned_route()
    {
        _net.Routes.RemoveAll(r => r.InterfaceLuid == Wifi);
        _net.AddExisting("0.0.0.0/0", "192.168.8.1", Wifi, metric: 281, manual: true);

        DiagnosticFinding finding = Assert.Single(Run(new DuplicateDefaultMetricRule()));
        RouteOperation op = Assert.Single(finding.Fix!.Operations);
        Assert.Equal(Wifi, op.Route.InterfaceLuid);
        Assert.Equal(291, op.NewMetric);
    }

    [Fact]
    public void Distinct_default_metrics_are_fine()
    {
        Assert.Empty(Run(new DuplicateDefaultMetricRule()));
    }

    [Fact]
    public void Off_subnet_gateway_is_a_notice_without_fix()
    {
        _net.AddExisting("10.0.0.0/8", "192.168.99.1", Wired);

        DiagnosticFinding finding = Assert.Single(Run(new OffSubnetGatewayRule()));
        Assert.Equal(FindingLevel.Notice, finding.Level);
        Assert.Null(finding.Fix);
    }

    [Fact]
    public void Off_subnet_check_skips_tunnels_and_on_link_gateways()
    {
        _net.AddExisting("10.10.0.0/16", "100.100.100.100", Tailscale);
        _net.AddExisting("10.0.0.0/8", "192.168.21.1", Wired);

        Assert.Empty(Run(new OffSubnetGatewayRule()));
    }

    [Fact]
    public void Orphan_persistent_route_can_be_removed()
    {
        _net.Routes.Add(new Route
        {
            Destination = IpPrefix.Parse("10.50.0.0/16"),
            NextHop = IPAddress.Parse("192.168.50.1"),
            InterfaceLuid = 0,
            InterfaceIndex = 31,
            InActiveStore = false,
            InPersistentStore = true,
        });

        DiagnosticFinding finding = Assert.Single(Run(new OrphanPersistentRouteRule()));
        RouteOperation op = Assert.Single(finding.Fix!.Operations);
        Assert.Equal(RouteOperationKind.Remove, op.Kind);
        Assert.Equal(31u, op.Route.InterfaceIndex);
        Assert.True(op.Route.Persistent);
    }

    [Fact]
    public void Persistent_route_on_existing_adapter_is_not_orphan()
    {
        Assert.Empty(Run(new OrphanPersistentRouteRule()));
    }

    [Fact]
    public void Same_destination_names_the_winner_and_ignores_local_routes()
    {
        _net.AddExisting("10.0.0.0/8", "192.168.21.1", Wired, metric: 5);
        _net.AddExisting("10.0.0.0/8", "192.168.8.1", Wifi, metric: 5);
        _net.AddExisting("224.0.0.0/4", null, Wired, protocol: RouteProtocol.Local);
        _net.AddExisting("224.0.0.0/4", null, Wifi, protocol: RouteProtocol.Local);

        DiagnosticFinding finding = Assert.Single(Run(new SameDestinationRule()));
        Assert.Equal(FindingLevel.Info, finding.Level);
        Assert.StartsWith("实际使用“WLAN”（有效跃点 15）", finding.Detail);
    }

    [Fact]
    public void Finding_ids_are_stable_across_snapshots()
    {
        AddClashTun();
        string first = Run(new TunnelTakeoverRule())[0].Id;
        Assert.Equal(first, Run(new TunnelTakeoverRule())[0].Id);
    }

    [Theory]
    [InlineData("Mihomo", "Meta Tunnel", "Mihomo/Clash")]
    [InlineData("Tailscale", "Tailscale Tunnel", "Tailscale")]
    [InlineData("以太网 3", "Wintun Userspace Tunnel", "Wintun 隧道（Clash/sing-box/WireGuard 等）")]
    [InlineData("以太网", "Intel(R) Ethernet Connection I219-V", null)]
    [InlineData("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", null)]
    public void Tunnel_manager_detection(string name, string description, string? expected) =>
        Assert.Equal(expected, TunnelManagerDetector.Detect(name, description));
}
