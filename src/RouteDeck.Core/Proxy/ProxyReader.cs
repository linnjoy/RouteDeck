using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RouteDeck.Core.Proxy;

/// <summary>
/// Reads proxy settings for the current user. Must run in the user's process (the GUI):
/// the service runs as SYSTEM and would see SYSTEM's HKCU and environment instead.
/// </summary>
public static partial class ProxyReader
{
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private static readonly string[] EnvKeys = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY"];

    public static ProxyConfig Read()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
        (string? winHttpProxy, string? winHttpBypass) = ReadWinHttp();

        var config = new ProxyConfig
        {
            SystemProxyEnabled = key?.GetValue("ProxyEnable") is int enabled && enabled != 0,
            SystemProxyServer = key?.GetValue("ProxyServer") as string,
            SystemProxyBypass = key?.GetValue("ProxyOverride") as string,
            PacUrl = key?.GetValue("AutoConfigURL") as string,
            WinHttpProxy = winHttpProxy,
            WinHttpBypass = winHttpBypass,
            Environment = ReadEnvironment(),
        };
        return config with { EndpointOwners = ReadEndpointOwners(config) };
    }

    private static Dictionary<string, string> ReadEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in EnvKeys)
        {
            string? value =
                System.Environment.GetEnvironmentVariable(name) ??
                System.Environment.GetEnvironmentVariable(name.ToLowerInvariant()) ??
                System.Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) ??
                System.Environment.GetEnvironmentVariable(name.ToLowerInvariant(), EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(value)) result[name] = value;
        }
        return result;
    }

    /// <summary>Maps each local proxy endpoint to the name of the process listening on it.</summary>
    private static Dictionary<string, string> ReadEndpointOwners(ProxyConfig config)
    {
        var endpoints = new List<string>();
        foreach (string? raw in new[] { config.SystemProxyServer, config.WinHttpProxy }.Concat(config.Environment.Values))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (string part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                endpoints.Add(ProxyEvaluator.NormalizeEndpoint(part.Contains('=') && !part.Contains("://") ? part.Split('=', 2)[1] : part));
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, int> listeners = ReadLoopbackListeners();
        foreach (string endpoint in endpoints.Distinct())
        {
            int colon = endpoint.LastIndexOf(':');
            if (colon < 0 || !int.TryParse(endpoint[(colon + 1)..], out int port)) continue;
            string host = endpoint[..colon];
            if (!IPAddress.TryParse(host, out IPAddress? address) || !IPAddress.IsLoopback(address)) continue;
            if (!listeners.TryGetValue(port, out int pid)) continue;

            try
            {
                using Process process = Process.GetProcessById(pid);
                result[endpoint] = process.ProcessName;
            }
            catch (ArgumentException)
            {
                // The listener exited between the two calls.
            }
        }
        return result;
    }

    private static (string? Proxy, string? Bypass) ReadWinHttp()
    {
        if (!WinHttpGetDefaultProxyConfiguration(out WINHTTP_PROXY_INFO info)) return (null, null);
        try
        {
            const uint NamedProxy = 3;
            return info.AccessType == NamedProxy
                ? (Marshal.PtrToStringUni(info.Proxy), Marshal.PtrToStringUni(info.ProxyBypass))
                : (null, null);
        }
        finally
        {
            if (info.Proxy != IntPtr.Zero) GlobalFree(info.Proxy);
            if (info.ProxyBypass != IntPtr.Zero) GlobalFree(info.ProxyBypass);
        }
    }

    /// <summary>Listening IPv4 TCP ports on loopback/any → owning PID.</summary>
    private static unsafe Dictionary<int, int> ReadLoopbackListeners()
    {
        const int AF_INET = 2;
        const int TCP_TABLE_OWNER_PID_LISTENER = 3;
        var result = new Dictionary<int, int>();

        uint size = 0;
        GetExtendedTcpTable(null, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        byte[] buffer = new byte[size + 1024];
        fixed (byte* table = buffer)
        {
            size = (uint)buffer.Length;
            if (GetExtendedTcpTable(table, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return result;

            uint count = *(uint*)table;
            var rows = (MIB_TCPROW_OWNER_PID*)(table + 4);
            for (int i = 0; i < count; i++)
            {
                uint local = rows[i].LocalAddr;
                // Only 0.0.0.0 and 127.x listeners can serve a loopback proxy endpoint.
                if (local != 0 && (local & 0xFF) != 127) continue;
                int port = (int)(((rows[i].LocalPort & 0xFF) << 8) | ((rows[i].LocalPort >> 8) & 0xFF));
                result.TryAdd(port, (int)rows[i].OwningPid);
            }
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINHTTP_PROXY_INFO
    {
        public uint AccessType;
        public IntPtr Proxy;
        public IntPtr ProxyBypass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinHttpGetDefaultProxyConfiguration(out WINHTTP_PROXY_INFO proxyInfo);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalFree(IntPtr memory);

    [LibraryImport("iphlpapi.dll")]
    private static unsafe partial uint GetExtendedTcpTable(
        void* table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);
}
