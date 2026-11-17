using System.Net;

namespace RouteDeck.Core.Models;

/// <summary>Which protocol created a route, mirroring NL_ROUTE_PROTOCOL for the values users care about.</summary>
public enum RouteProtocol
{
    Other,
    Local,
    Static,
    Dhcp,
    RouterAdvertisement,
}

/// <summary>Where a route came from, as shown in the source tag column.</summary>
public enum RouteSource
{
    System,
    Persistent,
    Manual,
}

/// <summary>Identifies a route: destination + interface + next hop (null means on-link).</summary>
public readonly record struct RouteKey(IpPrefix Destination, ulong InterfaceLuid, IPAddress? NextHop)
{
    public override string ToString() => $"{Destination} via {NextHop?.ToString() ?? "on-link"} if {InterfaceLuid}";
}

/// <summary>What is needed to create a route.</summary>
/// <param name="InterfaceIndex">Fallback for persistent routes whose interface no longer exists (LUID 0).</param>
public sealed record RouteSpec(IpPrefix Destination, IPAddress? NextHop, ulong InterfaceLuid, int Metric, bool Persistent, uint InterfaceIndex = 0)
{
    public RouteKey Key => new(Destination, InterfaceLuid, NextHop);
}

public sealed record Route
{
    public required IpPrefix Destination { get; init; }

    /// <summary>Gateway address, or null for on-link routes.</summary>
    public IPAddress? NextHop { get; init; }

    public required ulong InterfaceLuid { get; init; }

    public uint InterfaceIndex { get; init; }

    public int Metric { get; init; }

    /// <summary>Interface metric at snapshot time; effective metric = route metric + interface metric.</summary>
    public int InterfaceMetric { get; init; }

    public RouteProtocol Protocol { get; init; }

    public bool InActiveStore { get; init; } = true;

    public bool InPersistentStore { get; init; }

    /// <summary>Created through RouteDeck (tracked by the service).</summary>
    public bool IsManual { get; init; }

    public int EffectiveMetric => Metric + InterfaceMetric;

    public RouteKey Key => new(Destination, InterfaceLuid, NextHop);

    public RouteSource Source => IsManual ? RouteSource.Manual : InPersistentStore ? RouteSource.Persistent : RouteSource.System;

    public RouteSpec ToSpec() => new(Destination, NextHop, InterfaceLuid, Metric, InPersistentStore, InterfaceIndex);
}
