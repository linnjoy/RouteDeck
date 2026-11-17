namespace RouteDeck.Core.Models;

/// <summary>Guesses which program owns a tunnel adapter from its connection name and driver description.</summary>
public static class TunnelManagerDetector
{
    private static readonly (string Keyword, string Manager)[] Known =
    [
        ("mihomo", "Mihomo/Clash"),
        ("clash", "Mihomo/Clash"),
        ("meta tunnel", "Mihomo/Clash"),
        ("sing-box", "sing-box"),
        ("singbox", "sing-box"),
        ("v2ray", "V2Ray/Xray"),
        ("xray", "V2Ray/Xray"),
        ("tailscale", "Tailscale"),
        ("zerotier", "ZeroTier"),
        ("wireguard", "WireGuard"),
        ("openvpn", "OpenVPN"),
        ("tap-windows", "OpenVPN"),
        ("anyconnect", "Cisco AnyConnect"),
        ("cisco", "Cisco AnyConnect"),
        ("forti", "FortiClient"),
        ("globalprotect", "GlobalProtect"),
        ("pangp", "GlobalProtect"),
        ("sonicwall", "SonicWall"),
        ("pulse secure", "Pulse Secure"),
        ("softether", "SoftEther"),
        ("easyconnect", "EasyConnect"),
        ("sangfor", "EasyConnect"),
        // Wintun is a shared driver; the specific program is unknown.
        ("wintun", "Wintun 隧道（Clash/sing-box/WireGuard 等）"),
    ];

    public static string? Detect(string name, string description)
    {
        string text = (name + " " + description).ToLowerInvariant();
        foreach ((string keyword, string manager) in Known)
        {
            if (text.Contains(keyword)) return manager;
        }
        return null;
    }
}
