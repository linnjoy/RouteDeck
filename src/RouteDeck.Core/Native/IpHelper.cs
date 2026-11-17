using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RouteDeck.Core.Native;

/// <summary>P/Invoke surface for iphlpapi.dll (netioapi.h). Layouts are for x64.</summary>
internal static unsafe partial class IpHelper
{
    public const ushort AF_UNSPEC = 0;
    public const ushort AF_INET = 2;
    public const ushort AF_INET6 = 23;

    public const int ERROR_SUCCESS = 0;
    public const int ERROR_NOT_FOUND = 1168;
    public const int ERROR_OBJECT_ALREADY_EXISTS = 5010;

    /// <summary>NL_ROUTE_PROTOCOL.MIB_IPPROTO_NETMGMT — a static route added by management tools.</summary>
    public const int RouteProtocolNetMgmt = 3;

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    public struct SOCKADDR_INET
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(2)] public ushort Port;
        [FieldOffset(4)] public fixed byte V4[4];
        [FieldOffset(8)] public fixed byte V6[16];
        [FieldOffset(24)] public uint ScopeId;

        public static SOCKADDR_INET From(IPAddress? address, AddressFamily family)
        {
            var result = new SOCKADDR_INET
            {
                Family = family == AddressFamily.InterNetwork ? AF_INET : AF_INET6,
            };
            if (address is null) return result;

            byte[] bytes = address.GetAddressBytes();
            if (result.Family == AF_INET)
            {
                for (int i = 0; i < 4; i++) result.V4[i] = bytes[i];
            }
            else
            {
                for (int i = 0; i < 16; i++) result.V6[i] = bytes[i];
                result.ScopeId = (uint)address.ScopeId;
            }
            return result;
        }

        public IPAddress? ToAddress()
        {
            fixed (byte* v4 = V4)
            fixed (byte* v6 = V6)
            {
                return Family switch
                {
                    AF_INET => new IPAddress(new ReadOnlySpan<byte>(v4, 4)),
                    AF_INET6 => new IPAddress(new ReadOnlySpan<byte>(v6, 16)),
                    _ => null,
                };
            }
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct IP_ADDRESS_PREFIX
    {
        [FieldOffset(0)] public SOCKADDR_INET Prefix;
        [FieldOffset(28)] public byte PrefixLength;
    }

    [StructLayout(LayoutKind.Explicit, Size = 104)]
    public struct MIB_IPFORWARD_ROW2
    {
        [FieldOffset(0)] public ulong InterfaceLuid;
        [FieldOffset(8)] public uint InterfaceIndex;
        [FieldOffset(12)] public IP_ADDRESS_PREFIX DestinationPrefix;
        [FieldOffset(44)] public SOCKADDR_INET NextHop;
        [FieldOffset(72)] public byte SitePrefixLength;
        [FieldOffset(76)] public uint ValidLifetime;
        [FieldOffset(80)] public uint PreferredLifetime;
        [FieldOffset(84)] public uint Metric;
        [FieldOffset(88)] public int Protocol;
        [FieldOffset(92)] public byte Loopback;
        [FieldOffset(93)] public byte AutoconfigureAddress;
        [FieldOffset(94)] public byte Publish;
        [FieldOffset(95)] public byte Immortal;
        [FieldOffset(96)] public uint Age;
        [FieldOffset(100)] public int Origin;
    }

    [StructLayout(LayoutKind.Explicit, Size = 168)]
    public struct MIB_IPINTERFACE_ROW
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(8)] public ulong InterfaceLuid;
        [FieldOffset(16)] public uint InterfaceIndex;
        [FieldOffset(44)] public byte UseAutomaticMetric;
        [FieldOffset(148)] public uint Metric;
        [FieldOffset(156)] public byte Connected;
    }

    [StructLayout(LayoutKind.Explicit, Size = 88)]
    public struct MIB_IPNET_ROW2
    {
        [FieldOffset(0)] public SOCKADDR_INET Address;
        [FieldOffset(28)] public uint InterfaceIndex;
        [FieldOffset(32)] public ulong InterfaceLuid;
        [FieldOffset(40)] public fixed byte PhysicalAddress[32];
        [FieldOffset(72)] public uint PhysicalAddressLength;
        [FieldOffset(76)] public int State;
    }

    /// <summary>Header of MIB_*_TABLE2: NumEntries followed by rows aligned to 8 bytes.</summary>
    public const int TableHeaderSize = 8;

    [LibraryImport("iphlpapi.dll")]
    public static partial int GetIpForwardTable2(ushort family, out IntPtr table);

    [LibraryImport("iphlpapi.dll")]
    public static partial int GetIpInterfaceTable(ushort family, out IntPtr table);

    [LibraryImport("iphlpapi.dll")]
    public static partial void FreeMibTable(IntPtr memory);

    [LibraryImport("iphlpapi.dll")]
    public static partial void InitializeIpForwardEntry(MIB_IPFORWARD_ROW2* row);

    [LibraryImport("iphlpapi.dll")]
    public static partial int CreateIpForwardEntry2(MIB_IPFORWARD_ROW2* row);

    [LibraryImport("iphlpapi.dll")]
    public static partial int DeleteIpForwardEntry2(MIB_IPFORWARD_ROW2* row);

    [LibraryImport("iphlpapi.dll")]
    public static partial int SetIpForwardEntry2(MIB_IPFORWARD_ROW2* row);

    [LibraryImport("iphlpapi.dll")]
    public static partial int GetBestRoute2(
        ulong* interfaceLuid,
        uint interfaceIndex,
        SOCKADDR_INET* sourceAddress,
        SOCKADDR_INET* destinationAddress,
        uint addressSortOptions,
        MIB_IPFORWARD_ROW2* bestRoute,
        SOCKADDR_INET* bestSourceAddress);

    [LibraryImport("iphlpapi.dll")]
    public static partial int ResolveIpNetEntry2(MIB_IPNET_ROW2* row, SOCKADDR_INET* sourceAddress);

    /// <summary>IPv4 ARP resolution; unlike ResolveIpNetEntry2 it works without elevation.</summary>
    [LibraryImport("iphlpapi.dll")]
    public static partial int SendARP(uint destIp, uint srcIp, byte* macAddress, uint* physicalAddressLength);

    [LibraryImport("iphlpapi.dll")]
    public static partial int ConvertInterfaceGuidToLuid(Guid* interfaceGuid, ulong* interfaceLuid);

    [LibraryImport("iphlpapi.dll")]
    public static partial int ConvertInterfaceIndexToLuid(uint interfaceIndex, ulong* interfaceLuid);

    [LibraryImport("iphlpapi.dll")]
    public static partial int NotifyRouteChange2(
        ushort family,
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, void> callback,
        IntPtr callerContext,
        [MarshalAs(UnmanagedType.U1)] bool initialNotification,
        out IntPtr notificationHandle);

    [LibraryImport("iphlpapi.dll")]
    public static partial int NotifyIpInterfaceChange(
        ushort family,
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, void> callback,
        IntPtr callerContext,
        [MarshalAs(UnmanagedType.U1)] bool initialNotification,
        out IntPtr notificationHandle);

    [LibraryImport("iphlpapi.dll")]
    public static partial int NotifyUnicastIpAddressChange(
        ushort family,
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, void> callback,
        IntPtr callerContext,
        [MarshalAs(UnmanagedType.U1)] bool initialNotification,
        out IntPtr notificationHandle);

    [LibraryImport("iphlpapi.dll")]
    public static partial int CancelMibChangeNotify2(IntPtr notificationHandle);

    /// <summary>Copies every row of a MIB_*_TABLE2 and frees the native table.</summary>
    public static T[] ReadTable<T>(IntPtr table) where T : unmanaged
    {
        try
        {
            uint count = *(uint*)table;
            var rows = new T[count];
            T* first = (T*)(table + TableHeaderSize);
            for (int i = 0; i < count; i++) rows[i] = first[i];
            return rows;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    public static ulong? IndexToLuid(uint index)
    {
        ulong luid;
        return ConvertInterfaceIndexToLuid(index, &luid) == ERROR_SUCCESS ? luid : null;
    }

    public static ulong? GuidToLuid(Guid guid)
    {
        ulong luid;
        return ConvertInterfaceGuidToLuid(&guid, &luid) == ERROR_SUCCESS ? luid : null;
    }

    public static int SizeOf<T>() where T : unmanaged => Unsafe.SizeOf<T>();
}
