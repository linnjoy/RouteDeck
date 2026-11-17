using System.Windows.Media;
using RouteDeck.App.Services;
using RouteDeck.Core.Diagnostics;
using RouteDeck.Core.Models;

namespace RouteDeck.App.ViewModels;

/// <summary>One row of the route table, pre-formatted for display.</summary>
public sealed class RouteRow
{
    public RouteRow(Route route, NetAdapter? adapter, FindingLevel? finding)
    {
        Route = route;
        Adapter = adapter;
        Finding = finding;
        Color = adapter is { IsUp: true } ? AdapterPalette.For(adapter) : AdapterPalette.Disconnected;
        string addresses = adapter is null
            ? ""
            : string.Join("、", adapter.Addresses.Where(a => a.Address.AddressFamily == route.Destination.Family && !a.Address.IsIPv6LinkLocal).Select(a => a.Address));
        string state = adapter is null ? "（网卡不存在）" : adapter.IsUp ? "" : " · 已断开";
        string manager = adapter?.TunnelManager is string m ? $" · {m}" : "";
        GroupName = $"{adapter?.Name ?? $"网卡 {route.InterfaceIndex}"}{(addresses.Length > 0 ? " · " + addresses : "")}{manager}{state}";
    }

    public Route Route { get; }

    public NetAdapter? Adapter { get; }

    public string GroupName { get; }

    public Brush Color { get; }

    public bool AdapterUp => Adapter is { IsUp: true };

    /// <summary>Highest diagnostic level that mentions this route, if any.</summary>
    public FindingLevel? Finding { get; }

    public bool HasWarning => Finding is FindingLevel.Notice or FindingLevel.Problem;

    public bool HasInfoOnly => Finding is FindingLevel.Info;

    public bool IsDefault => Route.Destination.IsDefault;

    public string Destination => Route.Destination.ToString();

    public string Gateway => Route.NextHop?.ToString() ?? "在链路上";

    public int EffectiveMetric => Route.EffectiveMetric;

    public string MetricFormula => $"{Route.Metric} + {Route.InterfaceMetric} = {Route.EffectiveMetric}";

    public string SourceLabel => Route.Source switch
    {
        RouteSource.Manual => "RouteDeck",
        RouteSource.Persistent => "持久",
        _ => Route.Protocol switch
        {
            RouteProtocol.Local => "本机",
            RouteProtocol.Dhcp => "DHCP",
            RouteProtocol.RouterAdvertisement => "路由通告",
            _ => Adapter?.TunnelManager ?? "系统",
        },
    };

    public bool IsOwned => Route.IsManual;

    public string StoreLabel => (Route.InActiveStore, Route.InPersistentStore) switch
    {
        (true, true) => "生效中 · 持久（重启后保留）",
        (true, false) => "生效中 · 临时（重启后消失）",
        (false, true) => "仅持久存储（当前未生效）",
        _ => "",
    };

    public string OwnerNote => Route.IsManual
        ? "由 RouteDeck 创建，可以安全修改和撤销。"
        : Adapter?.TunnelManager is string manager
            ? $"由 {manager} 管理。修改或删除后，{manager} 可能会重新创建它或工作异常。"
            : "不是 RouteDeck 创建的路由，修改前需要确认。";

    /// <summary>Per-interface local, loopback, multicast and broadcast entries that clutter the table.</summary>
    public bool IsHousekeeping =>
        Route.Protocol == RouteProtocol.Local ||
        Housekeeping.Any(h => h.Family == Route.Destination.Family && h.Length <= Route.Destination.Length && h.Contains(Route.Destination.Address));

    public string RouteAddCommand
    {
        get
        {
            IpPrefix d = Route.Destination;
            string gateway = Route.NextHop?.ToString() ?? (d.IsIPv4 ? "0.0.0.0" : "::");
            string persistent = Route.InPersistentStore ? "-p " : "";
            return d.IsIPv4
                ? $"route {persistent}add {d.Address} mask {IpPrefix.LengthToMask(d.Length)} {gateway} metric {Math.Max(1, Route.Metric)} if {Route.InterfaceIndex}"
                : $"route {persistent}add {d} {gateway} metric {Math.Max(1, Route.Metric)} if {Route.InterfaceIndex}";
        }
    }

    public string PowerShellCommand =>
        $"New-NetRoute -DestinationPrefix '{Route.Destination}' -InterfaceIndex {Route.InterfaceIndex} " +
        $"-NextHop '{Route.NextHop?.ToString() ?? (Route.Destination.IsIPv4 ? "0.0.0.0" : "::")}' -RouteMetric {Route.Metric}" +
        (Route.InPersistentStore ? "" : " -PolicyStore ActiveStore");

    private static readonly IpPrefix[] Housekeeping =
    [
        IpPrefix.Parse("127.0.0.0/8"), IpPrefix.Parse("224.0.0.0/4"), IpPrefix.Parse("255.255.255.255/32"),
        IpPrefix.Parse("::1/128"), IpPrefix.Parse("ff00::/8"), IpPrefix.Parse("fe80::/10"),
    ];
}
