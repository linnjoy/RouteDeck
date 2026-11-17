using System.Net;
using System.Net.Sockets;
using RouteDeck.Core.Lookup;
using RouteDeck.Core.Models;

namespace RouteDeck.Core.Diagnostics;

public static class DiagnosticEngine
{
    public static IReadOnlyList<IDiagnosticRule> DefaultRules { get; } =
    [
        new TunnelTakeoverRule(),
        new DuplicateDefaultMetricRule(),
        new OffSubnetGatewayRule(),
        new OrphanPersistentRouteRule(),
        new SameDestinationRule(),
    ];

    public static IReadOnlyList<DiagnosticFinding> Run(NetworkSnapshot snapshot, IEnumerable<IDiagnosticRule>? rules = null) =>
        (rules ?? DefaultRules)
            .SelectMany(r => r.Evaluate(snapshot))
            .OrderByDescending(f => f.Level)
            .ToList();

    /// <summary>Active routes on connected adapters, excluding the per-interface local/multicast/broadcast entries.</summary>
    internal static IEnumerable<Route> UsableRoutes(NetworkSnapshot snapshot) =>
        snapshot.Routes.Where(r =>
            r.InActiveStore &&
            r.Protocol != RouteProtocol.Local &&
            !IsHousekeeping(r.Destination) &&
            snapshot.FindAdapter(r.InterfaceLuid) is not { IsUp: false });

    private static readonly IpPrefix[] Housekeeping =
    [
        IpPrefix.Parse("127.0.0.0/8"), IpPrefix.Parse("224.0.0.0/4"), IpPrefix.Parse("255.255.255.255/32"),
        IpPrefix.Parse("::1/128"), IpPrefix.Parse("ff00::/8"), IpPrefix.Parse("fe80::/10"),
    ];

    private static bool IsHousekeeping(IpPrefix prefix) =>
        Housekeeping.Any(h => h.Family == prefix.Family && h.Length <= prefix.Length && h.Contains(prefix.Address));

    internal static string AdapterName(NetworkSnapshot snapshot, ulong luid) =>
        snapshot.FindAdapter(luid)?.Name ?? $"未知网卡（{luid}）";
}

/// <summary>A tunnel adapter carries public traffic. Usually intentional (Clash TUN, VPN full tunnel), so informational only.</summary>
public sealed class TunnelTakeoverRule : IDiagnosticRule
{
    private static readonly (AddressFamily Family, IpPrefix[] Prefixes, IPAddress Probe, string Label)[] Families =
    [
        (AddressFamily.InterNetwork,
            [IpPrefix.Parse("0.0.0.0/0"), IpPrefix.Parse("0.0.0.0/1"), IpPrefix.Parse("128.0.0.0/1")],
            IPAddress.Parse("8.8.8.8"), "IPv4"),
        (AddressFamily.InterNetworkV6,
            [IpPrefix.Parse("::/0"), IpPrefix.Parse("::/1"), IpPrefix.Parse("8000::/1")],
            IPAddress.Parse("2001:4860:4860::8888"), "IPv6"),
    ];

    public string Id => "tunnel-takeover";

    public IEnumerable<DiagnosticFinding> Evaluate(NetworkSnapshot snapshot)
    {
        foreach (var (family, prefixes, probe, label) in Families)
        {
            Route? winner = RouteLookup.Lookup(snapshot, probe).Winner;
            if (winner is null) continue;
            NetAdapter? tunnel = snapshot.FindAdapter(winner.InterfaceLuid);
            if (tunnel is not { IsTunnelLike: true }) continue;

            var routes = snapshot.Routes
                .Where(r => r.InActiveStore && r.InterfaceLuid == tunnel.Luid && prefixes.Contains(r.Destination))
                .Select(r => r.Key)
                .ToList();
            // Some TUN implementations split the public space into many smaller prefixes instead of /1 + /1.
            if (routes.Count == 0) routes.Add(winner.Key);

            string manager = tunnel.TunnelManager ?? "隧道软件";
            yield return new DiagnosticFinding(
                DiagnosticFinding.MakeId(Id, routes),
                Id,
                FindingLevel.Info,
                $"{manager} 接管了 {label} 公网流量",
                $"公网流量经隧道网卡“{tunnel.Name}”转发，这通常是 TUN 模式或全局 VPN 的正常工作方式，RouteDeck 不会修改这些路由。" +
                "如果某个内网或现场地址需要直连，可以为它添加一条绕过路由。",
                routes)
            {
                TunnelLuid = tunnel.Luid,
            };
        }
    }
}

/// <summary>Two adapters' default routes tie on effective metric, so the OS may alternate between them.</summary>
public sealed class DuplicateDefaultMetricRule : IDiagnosticRule
{
    public string Id => "duplicate-default-metric";

    public IEnumerable<DiagnosticFinding> Evaluate(NetworkSnapshot snapshot)
    {
        var groups = DiagnosticEngine.UsableRoutes(snapshot)
            .Where(r => r.Destination.IsDefault)
            .GroupBy(r => (r.Destination.Family, r.EffectiveMetric))
            .Where(g => g.Select(r => r.InterfaceLuid).Distinct().Count() > 1);

        foreach (var group in groups)
        {
            var routes = group.ToList();
            string names = string.Join("、", routes.Select(r => $"“{DiagnosticEngine.AdapterName(snapshot, r.InterfaceLuid)}”"));
            Route? owned = routes.FirstOrDefault(r => r.IsManual);
            Route? other = owned is null ? null : routes.First(r => r != owned);

            yield return new DiagnosticFinding(
                DiagnosticFinding.MakeId(Id, routes.Select(r => r.Key)),
                Id,
                FindingLevel.Notice,
                "多块网卡的默认路由优先级相同",
                $"{names} 的默认路由有效跃点都是 {group.Key.EffectiveMetric}，系统可能在它们之间切换出口。可以调整网卡跃点来确定优先级。",
                routes.Select(r => r.Key).ToList())
            {
                Fix = owned is null ? null : new ChangeSet(
                    "fix:" + Id,
                    $"让“{DiagnosticEngine.AdapterName(snapshot, other!.InterfaceLuid)}”优先",
                    [RouteOperation.SetMetric(owned.ToSpec(), owned.Metric + 10)]),
                FixLabel = owned is null ? null : "调高 RouteDeck 路由的跃点",
            };
        }
    }
}

/// <summary>A gateway outside every subnet of its adapter cannot be reached directly (not applicable to tunnels/PPP).</summary>
public sealed class OffSubnetGatewayRule : IDiagnosticRule
{
    public string Id => "off-subnet-gateway";

    public IEnumerable<DiagnosticFinding> Evaluate(NetworkSnapshot snapshot)
    {
        foreach (Route route in DiagnosticEngine.UsableRoutes(snapshot))
        {
            if (route.NextHop is not IPAddress hop || hop.IsIPv6LinkLocal) continue;
            NetAdapter? adapter = snapshot.FindAdapter(route.InterfaceLuid);
            if (adapter is null || adapter.IsTunnelLike || adapter.Kind == AdapterKind.Loopback || adapter.Addresses.Count == 0) continue;
            if (adapter.IsOnLink(hop)) continue;

            yield return new DiagnosticFinding(
                DiagnosticFinding.MakeId(Id, [route.Key]),
                Id,
                FindingLevel.Notice,
                $"网关 {hop} 不在“{adapter.Name}”的子网内",
                $"路由 {route.Destination} 的网关 {hop} 不属于“{adapter.Name}”的任何地址段（{string.Join("、", adapter.Addresses.Where(a => a.Address.AddressFamily == hop.AddressFamily))}），" +
                "流量可能发不出去。这只是提示，可以用连通性测试确认。",
                [route.Key]);
        }
    }
}

/// <summary>A persistent route pointing at an adapter that no longer exists. Removing it cannot affect current traffic.</summary>
public sealed class OrphanPersistentRouteRule : IDiagnosticRule
{
    public string Id => "orphan-persistent";

    public IEnumerable<DiagnosticFinding> Evaluate(NetworkSnapshot snapshot)
    {
        foreach (Route route in snapshot.Routes.Where(r => r.InPersistentStore && !r.InActiveStore))
        {
            if (route.InterfaceLuid != 0 && snapshot.FindAdapter(route.InterfaceLuid) is not null) continue;

            yield return new DiagnosticFinding(
                DiagnosticFinding.MakeId(Id, [route.Key]),
                Id,
                FindingLevel.Notice,
                $"持久路由 {route.Destination} 指向已不存在的网卡",
                $"这条持久路由绑定的网卡（索引 {route.InterfaceIndex}）已被移除，它不会生效。删除它不影响当前网络。",
                [route.Key])
            {
                Fix = new ChangeSet("fix:" + Id, $"删除失效的持久路由 {route.Destination}", [RouteOperation.Remove(route.ToSpec())]),
                FixLabel = "删除",
            };
        }
    }
}

/// <summary>The same non-default destination is routed through several adapters; tell the user which one wins.</summary>
public sealed class SameDestinationRule : IDiagnosticRule
{
    public string Id => "same-destination";

    public IEnumerable<DiagnosticFinding> Evaluate(NetworkSnapshot snapshot)
    {
        var groups = DiagnosticEngine.UsableRoutes(snapshot)
            .Where(r => !r.Destination.IsDefault)
            .GroupBy(r => r.Destination)
            .Where(g => g.Select(r => r.InterfaceLuid).Distinct().Count() > 1);

        foreach (var group in groups)
        {
            var routes = group.OrderBy(r => r.EffectiveMetric).ToList();
            Route winner = routes[0];
            yield return new DiagnosticFinding(
                DiagnosticFinding.MakeId(Id, routes.Select(r => r.Key)),
                Id,
                FindingLevel.Info,
                $"{group.Key} 有 {routes.Count} 条路由",
                $"实际使用“{DiagnosticEngine.AdapterName(snapshot, winner.InterfaceLuid)}”（有效跃点 {winner.EffectiveMetric}），" +
                $"其余：{string.Join("、", routes.Skip(1).Select(r => $"“{DiagnosticEngine.AdapterName(snapshot, r.InterfaceLuid)}”（{r.EffectiveMetric}）"))}。",
                routes.Select(r => r.Key).ToList());
        }
    }
}
