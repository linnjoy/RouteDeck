namespace RouteDeck.Core.Models;

public sealed record NetworkSnapshot(IReadOnlyList<NetAdapter> Adapters, IReadOnlyList<Route> Routes, DateTimeOffset TakenAt)
{
    public static NetworkSnapshot Empty { get; } = new([], [], DateTimeOffset.MinValue);

    public NetAdapter? FindAdapter(ulong luid) => Adapters.FirstOrDefault(a => a.Luid == luid);

    public Route? FindRoute(RouteKey key) => Routes.FirstOrDefault(r => r.Key == key);
}
