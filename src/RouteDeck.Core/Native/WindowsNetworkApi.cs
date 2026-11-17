using System.Net;
using System.Net.Sockets;
using RouteDeck.Core.Abstractions;
using RouteDeck.Core.Models;
using static RouteDeck.Core.Native.IpHelper;

namespace RouteDeck.Core.Native;

/// <summary>Real network stack: IP Helper for the ActiveStore, CIM for the PersistentStore.</summary>
public sealed unsafe class WindowsNetworkApi : INetworkApi
{
    private readonly PersistentRouteStore _persistent = new();
    private readonly object _subscribersLock = new();
    private readonly List<Action> _subscribers = [];
    private ChangeNotifier? _notifier;

    public NetworkSnapshot GetSnapshot()
    {
        Dictionary<(ulong, ushort), MIB_IPINTERFACE_ROW> interfaces = ReadInterfaces();
        List<NetAdapter> adapters = AdapterReader.Read(interfaces);
        IReadOnlyList<PersistentRoute> persistent = ReadPersistentSafe();
        var persistentKeys = persistent
            .Where(p => p.InterfaceLuid is not null)
            .Select(p => new RouteKey(p.Destination, p.InterfaceLuid!.Value, p.NextHop))
            .ToHashSet();

        var routes = new List<Route>();
        foreach (MIB_IPFORWARD_ROW2 row in ReadForwardTable())
        {
            Route route = ToRoute(row, interfaces);
            routes.Add(route with { InPersistentStore = persistentKeys.Contains(route.Key) });
        }

        var activeKeys = routes.Select(r => r.Key).ToHashSet();
        foreach (PersistentRoute p in persistent)
        {
            var key = new RouteKey(p.Destination, p.InterfaceLuid ?? 0, p.NextHop);
            if (activeKeys.Contains(key)) continue;
            routes.Add(new Route
            {
                Destination = p.Destination,
                NextHop = p.NextHop,
                InterfaceLuid = p.InterfaceLuid ?? 0,
                InterfaceIndex = p.InterfaceIndex,
                Metric = p.Metric,
                InterfaceMetric = InterfaceMetric(interfaces, p.InterfaceLuid ?? 0, p.Destination.Family),
                Protocol = RouteProtocol.Static,
                InActiveStore = false,
                InPersistentStore = true,
            });
        }

        return new NetworkSnapshot(adapters, routes, DateTimeOffset.Now);
    }

    public void AddRoute(RouteSpec route)
    {
        if (route.Persistent) _persistent.Create(route, IndexOf(route));

        MIB_IPFORWARD_ROW2 row;
        InitializeIpForwardEntry(&row);
        row.InterfaceLuid = route.InterfaceLuid;
        row.DestinationPrefix.Prefix = SOCKADDR_INET.From(route.Destination.Address, route.Destination.Family);
        row.DestinationPrefix.PrefixLength = (byte)route.Destination.Length;
        row.NextHop = SOCKADDR_INET.From(route.NextHop, route.Destination.Family);
        row.Metric = (uint)route.Metric;
        row.Protocol = RouteProtocolNetMgmt;

        int result = CreateIpForwardEntry2(&row);
        // Creating in the PersistentStore usually also creates the active route.
        if (route.Persistent && result == ERROR_OBJECT_ALREADY_EXISTS) return;
        if (route.Persistent && result != ERROR_SUCCESS && !IsInterfaceUp(route.InterfaceLuid)) return;
        ThrowIfFailed(result, "添加路由");
    }

    public void RemoveRoute(RouteSpec route)
    {
        bool removedSomething = false;
        if (FindRow(route.Key) is MIB_IPFORWARD_ROW2 row)
        {
            ThrowIfFailed(DeleteIpForwardEntry2(&row), "删除路由");
            removedSomething = true;
        }
        if (route.Persistent)
        {
            try
            {
                _persistent.Delete(route, IndexOf(route));
                removedSomething = true;
            }
            catch (NetworkApiException ex) when (ex.ErrorCode == ERROR_NOT_FOUND && removedSomething)
            {
                // The active route was the only copy left.
            }
        }
        if (!removedSomething) throw Win32ErrorMessages.ToException(ERROR_NOT_FOUND, "删除路由");
    }

    public void SetRouteMetric(RouteSpec route, int metric)
    {
        if (route.Persistent) _persistent.SetMetric(route, IndexOf(route), metric);

        if (FindRow(route.Key) is MIB_IPFORWARD_ROW2 row)
        {
            row.Metric = (uint)metric;
            ThrowIfFailed(SetIpForwardEntry2(&row), "修改跃点");
        }
        else if (!route.Persistent)
        {
            throw Win32ErrorMessages.ToException(ERROR_NOT_FOUND, "修改跃点");
        }
    }

    public RouteKey? GetBestRoute(IPAddress destination)
    {
        if (destination.IsIPv4MappedToIPv6) destination = destination.MapToIPv4();
        SOCKADDR_INET dest = SOCKADDR_INET.From(destination, destination.AddressFamily);
        MIB_IPFORWARD_ROW2 best;
        SOCKADDR_INET source;
        if (GetBestRoute2(null, 0, null, &dest, 0, &best, &source) != ERROR_SUCCESS) return null;
        return ToKey(best);
    }

    public Task<bool> ProbeNeighborAsync(IPAddress address, ulong interfaceLuid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // ResolveIpNetEntry2 blocks for up to a few seconds; run it off-thread and stop waiting at the timeout.
        Task<bool> probe = Task.Run(() =>
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte* mac = stackalloc byte[8];
                uint length = 8;
                uint destination = BitConverter.ToUInt32(address.GetAddressBytes());
                return SendARP(destination, 0, mac, &length) == ERROR_SUCCESS && length > 0;
            }

            MIB_IPNET_ROW2 row = default;
            row.Address = SOCKADDR_INET.From(address, address.AddressFamily);
            row.InterfaceLuid = interfaceLuid;
            return ResolveIpNetEntry2(&row, null) == ERROR_SUCCESS;
        }, cancellationToken);

        return probe.WaitAsync(timeout, cancellationToken).ContinueWith(
            t => t.IsCompletedSuccessfully && t.Result,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public IDisposable Subscribe(Action onChange)
    {
        lock (_subscribersLock)
        {
            _subscribers.Add(onChange);
            _notifier ??= new ChangeNotifier(RaiseChanged, TimeSpan.FromMilliseconds(300));
        }
        return new Subscription(this, onChange);
    }

    private void Unsubscribe(Action onChange)
    {
        lock (_subscribersLock)
        {
            _subscribers.Remove(onChange);
            if (_subscribers.Count > 0) return;
            _notifier?.Dispose();
            _notifier = null;
        }
    }

    private void RaiseChanged()
    {
        Action[] subscribers;
        lock (_subscribersLock) subscribers = _subscribers.ToArray();
        foreach (Action subscriber in subscribers) subscriber();
    }

    private static MIB_IPFORWARD_ROW2[] ReadForwardTable()
    {
        ThrowIfFailed(GetIpForwardTable2(AF_UNSPEC, out IntPtr table), "读取路由表");
        return ReadTable<MIB_IPFORWARD_ROW2>(table);
    }

    private static Dictionary<(ulong, ushort), MIB_IPINTERFACE_ROW> ReadInterfaces()
    {
        ThrowIfFailed(GetIpInterfaceTable(AF_UNSPEC, out IntPtr table), "读取网卡");
        return ReadTable<MIB_IPINTERFACE_ROW>(table).ToDictionary(r => (r.InterfaceLuid, r.Family));
    }

    private IReadOnlyList<PersistentRoute> ReadPersistentSafe()
    {
        try
        {
            return _persistent.List();
        }
        catch (Exception ex) when (ex is NetworkApiException or Microsoft.Management.Infrastructure.CimException)
        {
            // The active table is still useful without the persistent tags.
            return [];
        }
    }

    private static MIB_IPFORWARD_ROW2? FindRow(RouteKey key)
    {
        foreach (MIB_IPFORWARD_ROW2 row in ReadForwardTable())
        {
            if (ToKey(row) == key) return row;
        }
        return null;
    }

    private static RouteKey ToKey(MIB_IPFORWARD_ROW2 row)
    {
        IPAddress prefixAddress = row.DestinationPrefix.Prefix.ToAddress() ?? IPAddress.Any;
        var destination = new IpPrefix(prefixAddress, row.DestinationPrefix.PrefixLength);
        return new RouteKey(destination, row.InterfaceLuid, NextHopOf(row));
    }

    private static Route ToRoute(MIB_IPFORWARD_ROW2 row, Dictionary<(ulong, ushort), MIB_IPINTERFACE_ROW> interfaces)
    {
        RouteKey key = ToKey(row);
        return new Route
        {
            Destination = key.Destination,
            NextHop = key.NextHop,
            InterfaceLuid = row.InterfaceLuid,
            InterfaceIndex = row.InterfaceIndex,
            Metric = (int)row.Metric,
            InterfaceMetric = InterfaceMetric(interfaces, row.InterfaceLuid, key.Destination.Family),
            Protocol = row.Protocol switch
            {
                2 => RouteProtocol.Local,
                3 or 10002 or 10006 or 10007 => RouteProtocol.Static,
                4 => RouteProtocol.RouterAdvertisement,
                19 => RouteProtocol.Dhcp,
                _ => RouteProtocol.Other,
            },
            InActiveStore = true,
        };
    }

    private static IPAddress? NextHopOf(MIB_IPFORWARD_ROW2 row)
    {
        IPAddress? hop = row.NextHop.ToAddress();
        if (hop is null || hop.Equals(IPAddress.Any) || hop.Equals(IPAddress.IPv6Any)) return null;
        if (hop.AddressFamily == AddressFamily.InterNetworkV6) hop.ScopeId = 0;
        return hop;
    }

    private static int InterfaceMetric(Dictionary<(ulong, ushort), MIB_IPINTERFACE_ROW> interfaces, ulong luid, AddressFamily family)
    {
        ushort af = family == AddressFamily.InterNetwork ? AF_INET : AF_INET6;
        return interfaces.TryGetValue((luid, af), out MIB_IPINTERFACE_ROW row) ? (int)row.Metric : 0;
    }

    private static uint IndexOf(RouteSpec route)
    {
        if (route.InterfaceLuid == 0 && route.InterfaceIndex != 0) return route.InterfaceIndex;
        foreach (MIB_IPINTERFACE_ROW row in ReadInterfaces().Values)
        {
            if (row.InterfaceLuid == route.InterfaceLuid) return row.InterfaceIndex;
        }
        throw Win32ErrorMessages.ToException(2, "查找网卡");
    }

    private static bool IsInterfaceUp(ulong luid) =>
        ReadInterfaces().Values.Any(r => r.InterfaceLuid == luid && r.Connected != 0);

    private static void ThrowIfFailed(int result, string action) => Win32ErrorMessages.ThrowIfFailed(result, action);

    private sealed class Subscription(WindowsNetworkApi owner, Action onChange) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Unsubscribe(onChange);
        }
    }
}
