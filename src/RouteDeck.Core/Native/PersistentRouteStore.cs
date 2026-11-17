using System.Net;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using RouteDeck.Core.Abstractions;
using RouteDeck.Core.Models;

namespace RouteDeck.Core.Native;

/// <summary>A route stored in the PersistentStore, as reported by MSFT_NetRoute.</summary>
internal sealed record PersistentRoute(IpPrefix Destination, IPAddress? NextHop, uint InterfaceIndex, ulong? InterfaceLuid, int Metric);

/// <summary>
/// 通过 CIM（root/StandardCimv2 MSFT_NetRoute）读写持久路由。
/// 创建时使用默认同时写入两个存储的行为；查询、修改和删除仍定位 PersistentStore。
/// IP Helper 只能操作 ActiveStore。
/// </summary>
internal sealed class PersistentRouteStore
{
    private const string Namespace = "root/StandardCimv2";
    private const string ClassName = "MSFT_NetRoute";

    public IReadOnlyList<PersistentRoute> List()
    {
        using CimSession session = CimSession.Create(null);
        using CimOperationOptions options = PersistentOptions();
        return session.EnumerateInstances(Namespace, ClassName, options)
            .Select(ToRoute)
            .OfType<PersistentRoute>()
            .ToList();
    }

    public void Create(RouteSpec spec, uint interfaceIndex)
    {
        using CimSession session = CimSession.Create(null);
        // Windows 的 Create 不接受显式 PolicyStore=PersistentStore，会返回参数无效。
        // 省略该参数才会同时写入活动和持久存储，等价于 New-NetRoute 的默认行为。
        var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("DestinationPrefix", spec.Destination.ToString(), CimType.String, CimFlags.In),
            CimMethodParameter.Create("InterfaceIndex", interfaceIndex, CimType.UInt32, CimFlags.In),
            CimMethodParameter.Create("NextHop", NextHopText(spec), CimType.String, CimFlags.In),
            CimMethodParameter.Create("RouteMetric", (ushort)spec.Metric, CimType.UInt16, CimFlags.In),
        };

        Run("写入持久路由", () =>
        {
            CimMethodResult result = session.InvokeMethod(Namespace, ClassName, "Create", parameters);
            uint code = Convert.ToUInt32(result.ReturnValue.Value);
            if (code != 0) throw Win32ErrorMessages.ToException((int)code, "写入持久路由");
        });
    }

    public void Delete(RouteSpec spec, uint interfaceIndex)
    {
        using CimSession session = CimSession.Create(null);
        using CimOperationOptions options = PersistentOptions();
        CimInstance instance = Find(session, options, spec, interfaceIndex)
            ?? throw Win32ErrorMessages.ToException(IpHelper.ERROR_NOT_FOUND, "删除持久路由");
        Run("删除持久路由", () => session.DeleteInstance(Namespace, instance, options));
    }

    public void SetMetric(RouteSpec spec, uint interfaceIndex, int metric)
    {
        using CimSession session = CimSession.Create(null);
        using CimOperationOptions options = PersistentOptions();
        CimInstance instance = Find(session, options, spec, interfaceIndex)
            ?? throw Win32ErrorMessages.ToException(IpHelper.ERROR_NOT_FOUND, "修改持久路由跃点");

        // 使用 InstanceID 定位原路由，只提交本次允许修改的跃点值，避免回传无关的只读属性。
        using var update = new CimInstance(ClassName, Namespace);
        update.CimInstanceProperties.Add(CimProperty.Create("InstanceID",
            instance.CimInstanceProperties["InstanceID"].Value, CimType.String, CimFlags.Key));
        update.CimInstanceProperties.Add(CimProperty.Create("RouteMetric", (ushort)metric, CimType.UInt16, CimFlags.None));

        // PolicyStore 只用于上面的查询；修改调用不接受 PersistentStore 选项。
        // InstanceID 已标识持久存储中的那条实例，不需要再次指定存储。
        Run("修改持久路由跃点", () => session.ModifyInstance(Namespace, update));
    }

    private static CimInstance? Find(CimSession session, CimOperationOptions options, RouteSpec spec, uint interfaceIndex) =>
        session.EnumerateInstances(Namespace, ClassName, options).FirstOrDefault(i =>
            ToRoute(i) is { } r &&
            r.Destination == spec.Destination &&
            r.InterfaceIndex == interfaceIndex &&
            Equals(r.NextHop, spec.NextHop));

    private static CimOperationOptions PersistentOptions()
    {
        var options = new CimOperationOptions();
        options.SetCustomOption("PolicyStore", "PersistentStore", mustComply: false);
        return options;
    }

    private static PersistentRoute? ToRoute(CimInstance instance)
    {
        string? prefixText = instance.CimInstanceProperties["DestinationPrefix"]?.Value as string;
        if (!IpPrefix.TryParse(prefixText, out IpPrefix? prefix, out _)) return null;

        IPAddress? nextHop = null;
        if (instance.CimInstanceProperties["NextHop"]?.Value is string hopText &&
            IPAddress.TryParse(hopText, out IPAddress? hop) &&
            !hop.Equals(IPAddress.Any) && !hop.Equals(IPAddress.IPv6Any))
        {
            nextHop = hop;
        }

        uint index = Convert.ToUInt32(instance.CimInstanceProperties["InterfaceIndex"]?.Value ?? 0u);
        int metric = Convert.ToInt32(instance.CimInstanceProperties["RouteMetric"]?.Value ?? 0);
        return new PersistentRoute(prefix.Value, nextHop, index, IpHelper.IndexToLuid(index), metric);
    }

    private static string NextHopText(RouteSpec spec) =>
        spec.NextHop?.ToString() ?? (spec.Destination.IsIPv4 ? "0.0.0.0" : "::");

    private static void Run(string action, Action call)
    {
        try
        {
            call();
        }
        catch (CimException ex)
        {
            int code = ex.ErrorData?.CimInstanceProperties["error_Code"]?.Value is { } value
                ? Convert.ToInt32(value)
                : (int)ex.NativeErrorCode;
            throw Win32ErrorMessages.ToException(code, action);
        }
    }
}
