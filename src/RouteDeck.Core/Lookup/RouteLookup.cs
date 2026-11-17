using System.Net;
using RouteDeck.Core.Models;

namespace RouteDeck.Core.Lookup;

public sealed record LookupCandidate(Route Route, bool IsWinner, bool IsEligible, string Reason);

public sealed record LookupResult(IPAddress Destination, Route? Winner, IReadOnlyList<LookupCandidate> Candidates, string Explanation)
{
    /// <summary>The route the OS reported via GetBestRoute2, when available.</summary>
    public RouteKey? SystemChoice { get; init; }

    public bool MismatchWithSystem => SystemChoice is not null && Winner is not null && SystemChoice != Winner.Key;
}

/// <summary>Explains which route the OS uses for a destination: longest prefix, then lowest effective metric.</summary>
public static class RouteLookup
{
    public static LookupResult Lookup(NetworkSnapshot snapshot, IPAddress destination)
    {
        if (destination.IsIPv4MappedToIPv6) destination = destination.MapToIPv4();

        var matching = snapshot.Routes.Where(r => r.Destination.Contains(destination)).ToList();
        var eligible = matching.Where(r => IneligibleReason(snapshot, r) is null)
            .OrderByDescending(r => r.Destination.Length)
            .ThenBy(r => r.EffectiveMetric)
            .ToList();

        Route? winner = eligible.FirstOrDefault();
        var candidates = new List<LookupCandidate>();

        foreach (Route route in eligible)
        {
            candidates.Add(route == winner
                ? new LookupCandidate(route, true, true, "实际使用")
                : new LookupCandidate(route, false, true, LoserReason(route, winner!)));
        }
        foreach (Route route in matching.Except(eligible).OrderByDescending(r => r.Destination.Length))
        {
            candidates.Add(new LookupCandidate(route, false, false, IneligibleReason(snapshot, route)!));
        }

        return new LookupResult(destination, winner, candidates, Explain(destination, winner, eligible));
    }

    private static string? IneligibleReason(NetworkSnapshot snapshot, Route route)
    {
        if (!route.InActiveStore) return "仅在持久存储中，当前未生效";
        NetAdapter? adapter = snapshot.FindAdapter(route.InterfaceLuid);
        if (adapter is { IsUp: false }) return $"网卡“{adapter.Name}”已断开";
        return null;
    }

    private static string LoserReason(Route route, Route winner)
    {
        if (route.Destination.Length < winner.Destination.Length)
            return $"/{route.Destination.Length} 不如 /{winner.Destination.Length} 具体";
        if (route.EffectiveMetric > winner.EffectiveMetric)
            return $"有效跃点 {route.EffectiveMetric} 高于 {winner.EffectiveMetric}";
        return "有效跃点相同，系统可能在两者间分担流量";
    }

    private static string Explain(IPAddress destination, Route? winner, List<Route> eligible)
    {
        if (winner is null) return $"没有路由能到达 {destination}，数据包会被丢弃。";

        var sameLength = eligible.Where(r => r != winner && r.Destination.Length == winner.Destination.Length).ToList();
        Route? lessSpecific = eligible.FirstOrDefault(r => r.Destination.Length < winner.Destination.Length);

        if (sameLength.Count > 0)
        {
            Route runnerUp = sameLength[0];
            return runnerUp.EffectiveMetric == winner.EffectiveMetric
                ? $"同为 /{winner.Destination.Length}，有效跃点都是 {winner.EffectiveMetric}，系统可能在两者间分担流量。"
                : $"同为 /{winner.Destination.Length}，有效跃点 {winner.EffectiveMetric} 小于 {runnerUp.EffectiveMetric}。";
        }
        if (lessSpecific is not null)
            return $"/{winner.Destination.Length} 比 /{lessSpecific.Destination.Length} 更具体，优先使用。";
        return "这是唯一匹配的路由。";
    }
}
