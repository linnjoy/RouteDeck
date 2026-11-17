using System.Net;
using RouteDeck.Core.Models;
using RouteDeck.Core.Native;

namespace RouteDeck.Tests.Native;

public class NativeLayoutTests
{
    [Fact]
    public void Struct_sizes_match_netioapi_h()
    {
        Assert.Equal(28, IpHelper.SizeOf<IpHelper.SOCKADDR_INET>());
        Assert.Equal(32, IpHelper.SizeOf<IpHelper.IP_ADDRESS_PREFIX>());
        Assert.Equal(104, IpHelper.SizeOf<IpHelper.MIB_IPFORWARD_ROW2>());
        Assert.Equal(168, IpHelper.SizeOf<IpHelper.MIB_IPINTERFACE_ROW>());
        Assert.Equal(88, IpHelper.SizeOf<IpHelper.MIB_IPNET_ROW2>());
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("2001:db8::7")]
    public void Sockaddr_round_trips(string text)
    {
        IPAddress address = IPAddress.Parse(text);
        var sockaddr = IpHelper.SOCKADDR_INET.From(address, address.AddressFamily);
        Assert.Equal(address, sockaddr.ToAddress());
    }
}

/// <summary>Reads the real routing table. Needs no elevation, only Windows.</summary>
[Trait("Category", "Windows")]
public class WindowsSnapshotTests
{
    [Fact]
    public void Snapshot_contains_loopback_routes_and_adapters()
    {
        NetworkSnapshot snapshot = new WindowsNetworkApi().GetSnapshot();

        Assert.NotEmpty(snapshot.Adapters);
        Assert.Contains(snapshot.Routes, r => r.Destination == IpPrefix.Parse("127.0.0.0/8"));
        Assert.All(snapshot.Routes.Where(r => r.InActiveStore), r => Assert.NotEqual(0UL, r.InterfaceLuid));
    }

    [Fact]
    public void Snapshot_agrees_with_best_route_for_loopback()
    {
        var api = new WindowsNetworkApi();
        RouteKey? best = api.GetBestRoute(IPAddress.Parse("127.0.0.1"));

        Assert.NotNull(best);
        Assert.Contains(api.GetSnapshot().Routes, r => r.Key == best);
    }
}
