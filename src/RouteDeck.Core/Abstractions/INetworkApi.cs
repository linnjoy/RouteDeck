using System.Net;
using RouteDeck.Core.Models;

namespace RouteDeck.Core.Abstractions;

/// <summary>The only gateway from Core to the operating system's network stack.</summary>
public interface INetworkApi
{
    NetworkSnapshot GetSnapshot();

    /// <summary>Adds to the active store, and to the persistent store when <see cref="RouteSpec.Persistent"/>.</summary>
    void AddRoute(RouteSpec route);

    /// <summary>Removes from the active store, and from the persistent store when <see cref="RouteSpec.Persistent"/>.</summary>
    void RemoveRoute(RouteSpec route);

    void SetRouteMetric(RouteSpec route, int metric);

    /// <summary>The route the OS would pick for <paramref name="destination"/>, or null when unreachable.</summary>
    RouteKey? GetBestRoute(IPAddress destination);

    /// <summary>Resolves a neighbor (ARP/NDP) to tell whether a gateway answers.</summary>
    Task<bool> ProbeNeighborAsync(IPAddress address, ulong interfaceLuid, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Invokes <paramref name="onChange"/> (coalesced) when routes, interfaces or addresses change.</summary>
    IDisposable Subscribe(Action onChange);
}

/// <summary>A failed OS call, carrying the Win32 error code and a Chinese explanation.</summary>
public sealed class NetworkApiException(int errorCode, string message) : Exception(message)
{
    public int ErrorCode { get; } = errorCode;
}
