using System.Net;
using System.Text.RegularExpressions;
using RouteDeck.Core.Models;

namespace RouteDeck.Core.Proxy;

/// <summary>Decides, per channel, whether a request to host:port would go through a proxy.</summary>
public static class ProxyEvaluator
{
    public static IReadOnlyList<ProxyDecision> Evaluate(ProxyConfig config, string host, int port)
    {
        string scheme = port is 443 or 8443 ? "https" : "http";
        return
        [
            Owned(config, EvaluateSystem(config, host, scheme)),
            Owned(config, EvaluateEnvironment(config, host, scheme)),
            Owned(config, EvaluateWinHttp(config, host)),
        ];
    }

    public static ProxyDecision EvaluateSystem(ProxyConfig config, string host, string scheme)
    {
        if (!string.IsNullOrWhiteSpace(config.PacUrl))
        {
            return new ProxyDecision(ProxyChannel.System, false, null,
                $"系统代理使用 PAC 脚本（{config.PacUrl}），实际是否走代理由脚本决定，RouteDeck 不执行脚本。")
            { Uncertain = true };
        }
        if (!config.SystemProxyEnabled || string.IsNullOrWhiteSpace(config.SystemProxyServer))
            return new ProxyDecision(ProxyChannel.System, false, null, "未启用系统代理，直接连接。");

        string? endpoint = PickWinInetServer(config.SystemProxyServer, scheme);
        if (endpoint is null)
            return new ProxyDecision(ProxyChannel.System, false, null, $"系统代理没有为 {scheme} 配置服务器，直接连接。");

        if (MatchWinInetBypass(config.SystemProxyBypass, host) is string entry)
            return new ProxyDecision(ProxyChannel.System, false, null, $"目标匹配绕过列表中的“{entry}”，直接连接。");

        return new ProxyDecision(ProxyChannel.System, true, endpoint,
            $"目标不在绕过列表中，会交给系统代理 {endpoint}。");
    }

    public static ProxyDecision EvaluateEnvironment(ProxyConfig config, string host, string scheme)
    {
        string? proxy = Env(config, scheme == "https" ? "HTTPS_PROXY" : "HTTP_PROXY") ?? Env(config, "ALL_PROXY");
        if (proxy is null)
            return new ProxyDecision(ProxyChannel.Environment, false, null, "未设置代理环境变量，直接连接。");

        if (MatchNoProxy(Env(config, "NO_PROXY"), host) is string entry)
            return new ProxyDecision(ProxyChannel.Environment, false, null, $"目标匹配 NO_PROXY 中的“{entry}”，直接连接。");

        string endpoint = StripScheme(proxy);
        return new ProxyDecision(ProxyChannel.Environment, true, endpoint, $"会经环境变量代理 {endpoint}。");
    }

    public static ProxyDecision EvaluateWinHttp(ProxyConfig config, string host)
    {
        if (string.IsNullOrWhiteSpace(config.WinHttpProxy))
            return new ProxyDecision(ProxyChannel.WinHttp, false, null, "WinHTTP 未配置代理，直接连接。");

        string? endpoint = PickWinInetServer(config.WinHttpProxy, "http");
        if (endpoint is null)
            return new ProxyDecision(ProxyChannel.WinHttp, false, null, "WinHTTP 代理没有 http 服务器，直接连接。");
        if (MatchWinInetBypass(config.WinHttpBypass, host) is string entry)
            return new ProxyDecision(ProxyChannel.WinHttp, false, null, $"目标匹配 WinHTTP 绕过列表中的“{entry}”，直接连接。");
        return new ProxyDecision(ProxyChannel.WinHttp, true, endpoint, $"会经 WinHTTP 代理 {endpoint}。");
    }

    /// <summary>"host:port" applies to every scheme; "http=a:1;https=b:2" picks by scheme.</summary>
    internal static string? PickWinInetServer(string server, string scheme)
    {
        string[] parts = server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && !parts[0].Contains('=')) return StripScheme(parts[0]);

        foreach (string part in parts)
        {
            string[] kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2 && kv[0].Equals(scheme, StringComparison.OrdinalIgnoreCase)) return StripScheme(kv[1]);
        }
        return null;
    }

    /// <summary>Returns the bypass entry that matches, or null.</summary>
    internal static string? MatchWinInetBypass(string? bypass, string host)
    {
        if (IPAddress.TryParse(host, out IPAddress? ip) && IPAddress.IsLoopback(ip) ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "本机地址（始终直连）";
        }
        if (string.IsNullOrWhiteSpace(bypass)) return null;
        foreach (string raw in bypass.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals("<local>", StringComparison.OrdinalIgnoreCase))
            {
                // WinINET treats names without a dot as local; IP literals are never <local>.
                if (!host.Contains('.') && !IPAddress.TryParse(host, out _)) return raw;
                continue;
            }
            string entry = StripScheme(raw);
            if (WildcardMatch(entry, host)) return raw;
        }
        return null;
    }

    /// <summary>curl-style NO_PROXY: "*", exact hosts, domain suffixes (".corp" or "corp"), IPs and CIDR.</summary>
    internal static string? MatchNoProxy(string? noProxy, string host)
    {
        if (string.IsNullOrWhiteSpace(noProxy)) return null;
        IPAddress.TryParse(host, out IPAddress? address);

        foreach (string raw in noProxy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "*") return raw;
            string entry = raw.Split(':')[0].TrimStart('*');
            if (address is not null)
            {
                if (IpPrefix.TryParse(raw, out IpPrefix? prefix, out _) && prefix.Value.Contains(address)) return raw;
                continue;
            }
            string domain = entry.TrimStart('.');
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                return raw;
            }
        }
        return null;
    }

    private static bool WildcardMatch(string pattern, string host)
    {
        string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(host, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string StripScheme(string value)
    {
        int index = value.IndexOf("://", StringComparison.Ordinal);
        return (index >= 0 ? value[(index + 3)..] : value).TrimEnd('/');
    }

    private static string? Env(ProxyConfig config, string key) =>
        config.Environment.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static ProxyDecision Owned(ProxyConfig config, ProxyDecision decision) =>
        decision.Endpoint is not null && config.EndpointOwners.TryGetValue(NormalizeEndpoint(decision.Endpoint), out string? owner)
            ? decision with { EndpointOwner = owner }
            : decision;

    /// <summary>"localhost:7897" and "127.0.0.1:7897" both normalize to "127.0.0.1:7897".</summary>
    public static string NormalizeEndpoint(string endpoint)
    {
        string text = StripScheme(endpoint);
        int at = text.LastIndexOf('@');
        if (at >= 0) text = text[(at + 1)..];
        return text.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" + text[9..] : text;
    }
}
