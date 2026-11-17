using System.Net;
using RouteDeck.Core.Models;

namespace RouteDeck.Core.Changes;

/// <summary>A physical adapter the user can send traffic out of, with its default gateway.</summary>
public sealed record BypassExit(NetAdapter Adapter, IPAddress? Gateway);

/// <summary>
/// Plans "let this destination skip the tunnel": one RouteDeck-owned route through a physical adapter.
/// Never touches the tunnel's own routes, so undoing it restores exactly the previous state.
/// </summary>
public static class BypassPlanner
{
    public const int DefaultMetric = 1;

    /// <summary>Connected, non-tunnel adapters that have a gateway (or on-link reachability) for the family.</summary>
    public static IReadOnlyList<BypassExit> Exits(NetworkSnapshot snapshot, IpPrefix destination) =>
        snapshot.Adapters
            .Where(a => a.IsUp && !a.IsTunnelLike && a.Kind != AdapterKind.Loopback)
            .Where(a => a.Addresses.Any(x => x.Address.AddressFamily == destination.Family))
            .Select(a => new BypassExit(a, a.Gateways.FirstOrDefault(g => g.AddressFamily == destination.Family)))
            .Where(e => e.Gateway is not null || e.Adapter.IsOnLink(destination.Address))
            .ToList();

    /// <param name="gateway">Override, e.g. a field gateway; defaults to the adapter's gateway or on-link.</param>
    public static ChangeSet Plan(NetworkSnapshot snapshot, IpPrefix destination, ulong exitLuid, IPAddress? gateway = null, bool persistent = false)
    {
        NetAdapter adapter = snapshot.FindAdapter(exitLuid)
            ?? throw new ArgumentException("找不到选定的出口网卡");
        if (adapter.IsTunnelLike)
            throw new ArgumentException("出口必须是物理网卡，不能是隧道网卡");

        if (gateway is null && !adapter.IsOnLink(destination.Address))
        {
            gateway = adapter.Gateways.FirstOrDefault(g => g.AddressFamily == destination.Family)
                ?? throw new ArgumentException($"“{adapter.Name}”没有 {(destination.IsIPv4 ? "IPv4" : "IPv6")} 网关，请手动填写网关");
        }
        if (gateway is not null && gateway.AddressFamily != destination.Family)
            throw new ArgumentException("网关和目标必须同为 IPv4 或 IPv6");
        if (gateway is not null && !gateway.IsIPv6LinkLocal && !adapter.IsOnLink(gateway))
            throw new ArgumentException($"网关 {gateway} 不在“{adapter.Name}”的子网内");

        var route = new RouteSpec(destination, gateway, adapter.Luid, DefaultMetric, persistent);
        string via = gateway?.ToString() ?? "在链路上";
        return new ChangeSet(
            ChangeSet.ManualOrigin,
            $"让 {destination} 绕过隧道，经“{adapter.Name}”({via}) 直连",
            [RouteOperation.Add(route)]);
    }
}
