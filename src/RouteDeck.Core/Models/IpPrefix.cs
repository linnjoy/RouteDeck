using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using RouteDeck.Core.Serialization;

namespace RouteDeck.Core.Models;

/// <summary>A normalized IP network prefix such as 10.0.0.0/8 or ::/0. Host bits are always zero.</summary>
[JsonConverter(typeof(IpPrefixJsonConverter))]
public readonly struct IpPrefix : IEquatable<IpPrefix>
{
    private readonly byte[]? _bytes;

    public IpPrefix(IPAddress address, int length)
    {
        ArgumentNullException.ThrowIfNull(address);
        int max = MaxLength(address.AddressFamily);
        if (length < 0 || length > max)
            throw new ArgumentOutOfRangeException(nameof(length), $"前缀长度必须在 0 到 {max} 之间");

        _bytes = Mask(address.GetAddressBytes(), length);
        Length = length;
    }

    public int Length { get; }

    public IPAddress Address => new(_bytes ?? new byte[4]);

    public AddressFamily Family => (_bytes?.Length ?? 4) == 4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;

    public bool IsIPv4 => Family == AddressFamily.InterNetwork;

    public bool IsDefault => Length == 0;

    public static IpPrefix DefaultV4 { get; } = new(IPAddress.Any, 0);

    public static IpPrefix DefaultV6 { get; } = new(IPAddress.IPv6Any, 0);

    public bool Contains(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != Family) return false;
        byte[] masked = Mask(address.GetAddressBytes(), Length);
        return masked.AsSpan().SequenceEqual(_bytes ?? new byte[4]);
    }

    /// <summary>Parses "a.b.c.d/n", "x::/n" or a bare address (treated as a host prefix).</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out IpPrefix? prefix, out string? error)
    {
        prefix = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "请输入网段，例如 10.0.0.0/8";
            return false;
        }

        string[] parts = text.Trim().Split('/');
        bool shortIPv4 = !parts[0].Contains(':') && parts[0].Split('.').Length != 4;
        if (parts.Length > 2 || shortIPv4 || !IPAddress.TryParse(parts[0], out IPAddress? address))
        {
            error = "不是有效的 IP 网段";
            return false;
        }
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6) address.ScopeId = 0;

        int max = MaxLength(address.AddressFamily);
        int length = max;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out length) || length < 0 || length > max))
        {
            error = $"前缀长度必须在 0 到 {max} 之间";
            return false;
        }

        prefix = new IpPrefix(address, length);
        return true;
    }

    public static IpPrefix Parse(string text) =>
        TryParse(text, out IpPrefix? p, out string? error) ? p.Value : throw new FormatException(error);

    /// <summary>Converts a dotted subnet mask such as 255.255.255.0 to a prefix length, or -1 if not contiguous.</summary>
    public static int MaskToLength(IPAddress mask)
    {
        int length = 0;
        bool zeroSeen = false;
        foreach (byte b in mask.GetAddressBytes())
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                bool set = (b & (1 << bit)) != 0;
                if (set && zeroSeen) return -1;
                if (set) length++; else zeroSeen = true;
            }
        }
        return length;
    }

    public static IPAddress LengthToMask(int length)
    {
        uint mask = length == 0 ? 0 : uint.MaxValue << (32 - length);
        return new IPAddress(new[] { (byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask });
    }

    public override string ToString() => $"{Address}/{Length}";

    public bool Equals(IpPrefix other) =>
        Length == other.Length && (_bytes ?? new byte[4]).AsSpan().SequenceEqual(other._bytes ?? new byte[4]);

    public override bool Equals(object? obj) => obj is IpPrefix other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_bytes ?? new byte[4]);
        hash.Add(Length);
        return hash.ToHashCode();
    }

    public static bool operator ==(IpPrefix left, IpPrefix right) => left.Equals(right);

    public static bool operator !=(IpPrefix left, IpPrefix right) => !left.Equals(right);

    private static int MaxLength(AddressFamily family) => family switch
    {
        AddressFamily.InterNetwork => 32,
        AddressFamily.InterNetworkV6 => 128,
        _ => throw new ArgumentException("只支持 IPv4 和 IPv6"),
    };

    private static byte[] Mask(byte[] bytes, int length)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            int bits = Math.Clamp(length - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bits));
        }
        return bytes;
    }
}
