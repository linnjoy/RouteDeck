using System.Net;
using RouteDeck.Core.Lookup;
using RouteDeck.Core.Models;

namespace RouteDeck.Core.Changes;

/// <summary>
/// Decides whether a change could cut the machine off: simulate it in memory and see whether
/// the route chosen for public traffic changes. Such changes need a confirmation countdown.
/// </summary>
public static class LockoutGuard
{
    private static readonly IPAddress[] PublicProbes = [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2001:4860:4860::8888")];

    public static bool IsRisky(ChangeSet changeSet, NetworkSnapshot snapshot) => RiskReason(changeSet, snapshot) is not null;

    public static string? RiskReason(ChangeSet changeSet, NetworkSnapshot snapshot)
    {
        NetworkSnapshot after = Simulate(changeSet, snapshot);
        foreach (IPAddress probe in PublicProbes)
        {
            Route? before = RouteLookup.Lookup(snapshot, probe).Winner;
            Route? now = RouteLookup.Lookup(after, probe).Winner;
            if (before?.Key == now?.Key) continue;

            string family = probe.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "IPv4" : "IPv6";
            return now is null
                ? $"这个变更会让 {family} 公网流量失去路由"
                : $"这个变更会改变 {family} 公网流量的出口";
        }
        return null;
    }

    /// <summary>Applies the operations to a copy of the snapshot.</summary>
    public static NetworkSnapshot Simulate(ChangeSet changeSet, NetworkSnapshot snapshot)
    {
        var routes = snapshot.Routes.ToList();
        foreach (RouteOperation op in changeSet.Operations)
        {
            int index = routes.FindIndex(r => r.Key == op.Route.Key);
            switch (op.Kind)
            {
                case RouteOperationKind.Add when index < 0:
                    NetAdapter? adapter = snapshot.FindAdapter(op.Route.InterfaceLuid);
                    routes.Add(new Route
                    {
                        Destination = op.Route.Destination,
                        NextHop = op.Route.NextHop,
                        InterfaceLuid = op.Route.InterfaceLuid,
                        Metric = op.Route.Metric,
                        InterfaceMetric = adapter?.MetricFor(op.Route.Destination.Family) ?? 0,
                        InPersistentStore = op.Route.Persistent,
                        IsManual = true,
                    });
                    break;
                case RouteOperationKind.Remove when index >= 0:
                    routes.RemoveAt(index);
                    break;
                case RouteOperationKind.SetMetric when index >= 0:
                    routes[index] = routes[index] with { Metric = op.NewMetric!.Value };
                    break;
            }
        }
        return snapshot with { Routes = routes };
    }
}
