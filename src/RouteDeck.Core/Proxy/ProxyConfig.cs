namespace RouteDeck.Core.Proxy;

/// <summary>Proxy settings as seen by the current user. Read-only: RouteDeck never changes them.</summary>
public sealed record ProxyConfig
{
    /// <summary>WinINET "Use a proxy server" switch (HKCU Internet Settings ProxyEnable).</summary>
    public bool SystemProxyEnabled { get; init; }

    /// <summary>Raw ProxyServer: "host:port" or "http=host:port;https=host:port;socks=host:port".</summary>
    public string? SystemProxyServer { get; init; }

    /// <summary>Raw ProxyOverride: semicolon separated, supports * wildcards and &lt;local&gt;.</summary>
    public string? SystemProxyBypass { get; init; }

    /// <summary>WinINET AutoConfigURL (PAC). RouteDeck shows it but never executes the script.</summary>
    public string? PacUrl { get; init; }

    /// <summary>WinHTTP named proxy (netsh winhttp), used by many system services.</summary>
    public string? WinHttpProxy { get; init; }

    public string? WinHttpBypass { get; init; }

    /// <summary>HTTP_PROXY, HTTPS_PROXY, ALL_PROXY, NO_PROXY (upper-cased keys), process then user scope.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Owner process of each local proxy endpoint, e.g. "127.0.0.1:7897" → "verge-mihomo".</summary>
    public IReadOnlyDictionary<string, string> EndpointOwners { get; init; } = new Dictionary<string, string>();

    public static ProxyConfig None { get; } = new();
}

/// <summary>Who uses which proxy channel.</summary>
public enum ProxyChannel
{
    /// <summary>WinINET system proxy: Edge, Chrome, most desktop apps.</summary>
    System,

    /// <summary>Environment variables: curl, git, Python, Node, many CLI tools.</summary>
    Environment,

    /// <summary>WinHTTP: Windows services and some updaters.</summary>
    WinHttp,
}

public sealed record ProxyDecision(ProxyChannel Channel, bool UsesProxy, string? Endpoint, string Reason)
{
    /// <summary>True when the result depends on something RouteDeck cannot evaluate (PAC script).</summary>
    public bool Uncertain { get; init; }

    /// <summary>Process listening on <see cref="Endpoint"/> when it is local, e.g. "verge-mihomo".</summary>
    public string? EndpointOwner { get; init; }

    public string ChannelLabel => Channel switch
    {
        ProxyChannel.System => "浏览器与桌面程序（系统代理）",
        ProxyChannel.Environment => "命令行工具（环境变量）",
        ProxyChannel.WinHttp => "系统服务（WinHTTP）",
        _ => Channel.ToString(),
    };
}
