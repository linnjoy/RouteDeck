using System.Net;
using System.Net.Sockets;

namespace RouteDeck.Core.Models;

public enum AdapterKind
{
    Other,
    Ethernet,
    Wifi,
    Tunnel,
    Ppp,
    Loopback,
}

public sealed record UnicastAddress(IPAddress Address, int PrefixLength)
{
    public IpPrefix Subnet => new(Address, PrefixLength);

    public override string ToString() => $"{Address}/{PrefixLength}";
}

public sealed record NetAdapter
{
    public required ulong Luid { get; init; }

    public required uint Index { get; init; }

    /// <summary>Interface GUID: the stable identity used when saving configuration. LUID and index are runtime handles.</summary>
    public Guid Guid { get; init; }

    /// <summary>User-facing connection name, e.g. "以太网" or "WLAN".</summary>
    public required string Name { get; init; }

    public string Description { get; init; } = "";

    /// <summary>MAC as "AA-BB-CC-DD-EE-FF", empty for adapters without one.</summary>
    public string Mac { get; init; } = "";

    public AdapterKind Kind { get; init; }

    public bool IsUp { get; init; }

    /// <summary>Interface metric for IPv4, null when IPv4 is not bound.</summary>
    public int? MetricV4 { get; init; }

    public int? MetricV6 { get; init; }

    public IReadOnlyList<UnicastAddress> Addresses { get; init; } = [];

    public IReadOnlyList<IPAddress> Gateways { get; init; } = [];

    public IReadOnlyList<IPAddress> DnsServers { get; init; } = [];

    public string DnsSuffix { get; init; } = "";

    /// <summary>Software that likely manages this tunnel adapter (e.g. "Mihomo/Clash"), or null.</summary>
    public string? TunnelManager { get; init; }

    public bool IsTunnelLike => Kind is AdapterKind.Tunnel or AdapterKind.Ppp;

    public int? MetricFor(AddressFamily family) =>
        family == AddressFamily.InterNetwork ? MetricV4 : MetricV6;

    /// <summary>True when the address lies inside one of this adapter's on-link subnets.</summary>
    public bool IsOnLink(IPAddress address) => Addresses.Any(a => a.Subnet.Contains(address));
}
