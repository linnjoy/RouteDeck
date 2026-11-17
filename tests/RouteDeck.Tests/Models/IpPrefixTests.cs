using System.Net;
using System.Text.Json;
using RouteDeck.Core.Models;
using RouteDeck.Core.Serialization;

namespace RouteDeck.Tests.Models;

public class IpPrefixTests
{
    [Theory]
    [InlineData("10.1.2.3/8", "10.0.0.0/8")]
    [InlineData("192.168.1.77/24", "192.168.1.0/24")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("8.8.8.8", "8.8.8.8/32")]
    [InlineData("2001:db8::1/32", "2001:db8::/32")]
    [InlineData("::/0", "::/0")]
    [InlineData("10.0.0.0/9", "10.0.0.0/9")]
    [InlineData("10.255.0.0/9", "10.128.0.0/9")]
    public void Parse_normalizes_host_bits(string input, string expected) =>
        Assert.Equal(expected, IpPrefix.Parse(input).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("::/129")]
    [InlineData("10.0.0/8")]
    [InlineData("abc")]
    [InlineData("1.2.3.4/8/9")]
    public void TryParse_rejects_invalid(string input)
    {
        Assert.False(IpPrefix.TryParse(input, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("10.0.0.0/8", "10.200.1.1", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    [InlineData("0.0.0.0/1", "127.255.255.255", true)]
    [InlineData("0.0.0.0/1", "128.0.0.0", false)]
    [InlineData("192.168.1.5/32", "192.168.1.5", true)]
    [InlineData("192.168.1.5/32", "192.168.1.6", false)]
    [InlineData("2001:db8::/32", "2001:db8:1::5", true)]
    [InlineData("2001:db8::/32", "2001:db9::5", false)]
    [InlineData("0.0.0.0/0", "::1", false)]
    [InlineData("::/0", "1.1.1.1", false)]
    [InlineData("10.0.0.0/8", "::ffff:10.1.1.1", true)]
    public void Contains_matches_prefix(string prefix, string address, bool expected) =>
        Assert.Equal(expected, IpPrefix.Parse(prefix).Contains(IPAddress.Parse(address)));

    [Fact]
    public void Equality_ignores_original_host_bits()
    {
        Assert.Equal(IpPrefix.Parse("10.1.1.1/8"), IpPrefix.Parse("10.9.9.9/8"));
        Assert.NotEqual(IpPrefix.Parse("10.0.0.0/8"), IpPrefix.Parse("10.0.0.0/9"));
        Assert.Equal(IpPrefix.Parse("10.1.1.1/8").GetHashCode(), IpPrefix.Parse("10.9.9.9/8").GetHashCode());
    }

    [Fact]
    public void Default_struct_is_ipv4_any()
    {
        IpPrefix prefix = default;
        Assert.Equal(IpPrefix.DefaultV4, prefix);
        Assert.True(prefix.IsDefault);
    }

    [Theory]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.0.0.0", 8)]
    [InlineData("0.0.0.0", 0)]
    [InlineData("255.255.255.255", 32)]
    [InlineData("255.0.255.0", -1)]
    public void MaskToLength_converts(string mask, int expected)
    {
        Assert.Equal(expected, IpPrefix.MaskToLength(IPAddress.Parse(mask)));
        if (expected >= 0) Assert.Equal(mask, IpPrefix.LengthToMask(expected).ToString());
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var spec = new RouteSpec(IpPrefix.Parse("10.0.0.0/8"), IPAddress.Parse("10.20.1.1"), 42, 5, true);
        string json = JsonSerializer.Serialize(spec, RouteDeckJson.Options);
        Assert.Contains("\"10.0.0.0/8\"", json);
        Assert.Equal(spec, JsonSerializer.Deserialize<RouteSpec>(json, RouteDeckJson.Options));
    }

    [Fact]
    public void Inverse_of_change_set_reverses_order()
    {
        var a = new RouteSpec(IpPrefix.Parse("10.0.0.0/8"), null, 1, 5, false);
        var b = new RouteSpec(IpPrefix.Parse("10.1.0.0/16"), null, 1, 5, false);
        var set = new ChangeSet("manual", "test", [RouteOperation.Add(a), RouteOperation.SetMetric(b, 9)]);

        ChangeSet inverse = set.Inverse();

        Assert.Equal(RouteOperation.SetMetric(b with { Metric = 9 }, 5), inverse.Operations[0]);
        Assert.Equal(RouteOperation.Remove(a), inverse.Operations[1]);
    }
}
