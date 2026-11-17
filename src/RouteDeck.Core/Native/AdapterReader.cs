using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RouteDeck.Core.Models;

namespace RouteDeck.Core.Native;

/// <summary>Builds <see cref="NetAdapter"/>s from NetworkInterface plus IP Helper interface metrics.</summary>
internal static class AdapterReader
{
    public static List<NetAdapter> Read(IReadOnlyDictionary<(ulong Luid, ushort Family), IpHelper.MIB_IPINTERFACE_ROW> interfaces)
    {
        var adapters = new List<NetAdapter>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!Guid.TryParse(nic.Id, out Guid guid) || IpHelper.GuidToLuid(guid) is not ulong luid) continue;

            IPInterfaceProperties props = nic.GetIPProperties();
            uint index = TryIndex(() => props.GetIPv4Properties()?.Index) ?? TryIndex(() => props.GetIPv6Properties()?.Index) ?? 0;

            adapters.Add(new NetAdapter
            {
                Luid = luid,
                Index = index,
                Guid = guid,
                Name = nic.Name,
                Description = nic.Description,
                Mac = FormatMac(nic.GetPhysicalAddress()),
                Kind = Classify(nic),
                IsUp = nic.OperationalStatus == OperationalStatus.Up,
                MetricV4 = interfaces.TryGetValue((luid, IpHelper.AF_INET), out var v4) ? (int)v4.Metric : null,
                MetricV6 = interfaces.TryGetValue((luid, IpHelper.AF_INET6), out var v6) ? (int)v6.Metric : null,
                Addresses = props.UnicastAddresses.Select(a => new UnicastAddress(Clean(a.Address), a.PrefixLength)).ToList(),
                Gateways = props.GatewayAddresses.Select(g => Clean(g.Address)).Where(a => !a.Equals(IPAddress.Any)).ToList(),
                DnsServers = props.DnsAddresses.Select(Clean).ToList(),
                DnsSuffix = props.DnsSuffix ?? "",
                TunnelManager = TunnelManagerDetector.Detect(nic.Name, nic.Description),
            });
        }
        return adapters;
    }

    internal static AdapterKind Classify(NetworkInterface nic)
    {
        string description = nic.Description.ToLowerInvariant();
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) return AdapterKind.Loopback;
        if (TunnelManagerDetector.Detect(nic.Name, nic.Description) is not null || description.Contains("vpn"))
            return AdapterKind.Tunnel;

        return nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => AdapterKind.Wifi,
            NetworkInterfaceType.Tunnel => AdapterKind.Tunnel,
            NetworkInterfaceType.Ppp => AdapterKind.Ppp,
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => AdapterKind.Ethernet,
            // IF_TYPE_PROP_VIRTUAL (53) is what Wintun and most userspace VPN drivers report.
            (NetworkInterfaceType)53 => AdapterKind.Tunnel,
            _ => AdapterKind.Other,
        };
    }

    private static string FormatMac(PhysicalAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? "" : string.Join("-", bytes.Select(b => b.ToString("X2")));
    }

    private static IPAddress Clean(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal) address.ScopeId = 0;
        return address;
    }

    private static uint? TryIndex(Func<int?> read)
    {
        try
        {
            return read() is int i ? (uint)i : null;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
