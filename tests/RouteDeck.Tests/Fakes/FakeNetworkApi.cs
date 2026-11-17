using System.Net;
using RouteDeck.Core.Abstractions;
using RouteDeck.Core.Models;

namespace RouteDeck.Tests.Fakes;

/// <summary>In-memory network stack for unit tests.</summary>
internal sealed class FakeNetworkApi : INetworkApi
{
    private readonly List<Action> _subscribers = [];

    public List<NetAdapter> Adapters { get; } = [];

    public List<Route> Routes { get; } = [];

    public HashSet<IPAddress> UnreachableNeighbors { get; } = [];

    /// <summary>Calls (1-based, counted across all write methods) that should throw.</summary>
    public HashSet<int> FailOnCall { get; } = [];

    public int WriteCalls { get; private set; }

    public List<string> Log { get; } = [];

    public NetAdapter AddAdapter(ulong luid, string name, AdapterKind kind = AdapterKind.Ethernet, string address = "10.0.0.10/24", int metric = 25, bool up = true, string? gateway = null, string? manager = null)
    {
        string[] parts = address.Split('/');
        var adapter = new NetAdapter
        {
            Luid = luid,
            Index = (uint)luid,
            Name = name,
            Kind = kind,
            IsUp = up,
            MetricV4 = metric,
            MetricV6 = metric,
            Mac = $"00-00-00-00-00-{luid:X2}",
            Addresses = [new UnicastAddress(IPAddress.Parse(parts[0]), int.Parse(parts[1]))],
            Gateways = gateway is null ? [] : [IPAddress.Parse(gateway)],
            TunnelManager = manager,
            Guid = new Guid((int)luid, 0, 0, new byte[8]),
        };
        Adapters.Add(adapter);
        return adapter;
    }

    public Route AddExisting(string destination, string? nextHop, ulong luid, int metric = 0, bool persistent = false, bool manual = false, RouteProtocol protocol = RouteProtocol.Static)
    {
        var route = new Route
        {
            Destination = IpPrefix.Parse(destination),
            NextHop = nextHop is null ? null : IPAddress.Parse(nextHop),
            InterfaceLuid = luid,
            InterfaceIndex = (uint)luid,
            Metric = metric,
            InPersistentStore = persistent,
            IsManual = manual,
            Protocol = protocol,
        };
        Routes.Add(route);
        return route;
    }

    public NetworkSnapshot GetSnapshot()
    {
        var routes = Routes.Select(r =>
        {
            NetAdapter? adapter = Adapters.FirstOrDefault(a => a.Luid == r.InterfaceLuid);
            return r with { InterfaceMetric = adapter?.MetricFor(r.Destination.Family) ?? 0 };
        }).ToList();
        return new NetworkSnapshot(Adapters.ToList(), routes, DateTimeOffset.UnixEpoch);
    }

    public void AddRoute(RouteSpec route)
    {
        Write($"add {route.Key}");
        if (Routes.Any(r => r.Key == route.Key))
            throw new NetworkApiException(5010, "路由已存在");
        Routes.Add(new Route
        {
            Destination = route.Destination,
            NextHop = route.NextHop,
            InterfaceLuid = route.InterfaceLuid,
            Metric = route.Metric,
            InPersistentStore = route.Persistent,
        });
        Notify();
    }

    public void RemoveRoute(RouteSpec route)
    {
        Write($"remove {route.Key}");
        if (Routes.RemoveAll(r => r.Key == route.Key) == 0)
            throw new NetworkApiException(1168, "找不到该路由");
        Notify();
    }

    public void SetRouteMetric(RouteSpec route, int metric)
    {
        Write($"metric {route.Key} {metric}");
        int index = Routes.FindIndex(r => r.Key == route.Key);
        if (index < 0) throw new NetworkApiException(1168, "找不到该路由");
        Routes[index] = Routes[index] with { Metric = metric };
        Notify();
    }

    public RouteKey? GetBestRoute(IPAddress destination) => null;

    public Task<bool> ProbeNeighborAsync(IPAddress address, ulong interfaceLuid, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult(!UnreachableNeighbors.Contains(address));

    public IDisposable Subscribe(Action onChange)
    {
        _subscribers.Add(onChange);
        return new Unsubscriber(() => _subscribers.Remove(onChange));
    }

    private void Write(string entry)
    {
        WriteCalls++;
        if (FailOnCall.Contains(WriteCalls))
        {
            Log.Add("FAIL " + entry);
            throw new NetworkApiException(87, "模拟失败");
        }
        Log.Add(entry);
    }

    private void Notify()
    {
        foreach (Action subscriber in _subscribers.ToList()) subscriber();
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
