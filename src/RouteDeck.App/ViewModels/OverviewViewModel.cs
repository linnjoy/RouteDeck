using System.Net.Sockets;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RouteDeck.App.Services;
using RouteDeck.Core.Lookup;
using RouteDeck.Core.Models;

namespace RouteDeck.App.ViewModels;

public sealed record AdapterCard(
    string Name,
    string Kind,
    string Description,
    Brush Color,
    bool IsUp,
    string Addresses,
    string Gateways,
    string Dns,
    string Metrics,
    string? Manager,
    bool CarriesPublicTraffic);

public sealed partial class OverviewViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty]
    private IReadOnlyList<AdapterCard> _adapters = [];

    [ObservableProperty]
    private string _publicExit = "";

    [ObservableProperty]
    private string _findingSummary = "";

    public void Rebuild()
    {
        NetworkSnapshot snapshot = main.Snapshot;
        Route? v4 = RouteLookup.Lookup(snapshot, System.Net.IPAddress.Parse("8.8.8.8")).Winner;
        NetAdapter? exit = v4 is null ? null : snapshot.FindAdapter(v4.InterfaceLuid);
        PublicExit = exit is null
            ? "IPv4 公网：没有可用路由"
            : $"IPv4 公网出口：“{exit.Name}”{(exit.TunnelManager is string m ? $"（{m}）" : "")}，网关 {v4!.NextHop?.ToString() ?? "在链路上"}";

        int notices = main.Findings.Count(f => f.Level != Core.Diagnostics.FindingLevel.Info);
        FindingSummary = main.Findings.Count == 0 ? "没有诊断提示" : $"{main.Findings.Count} 条诊断提示，其中 {notices} 条需要注意";

        Adapters = snapshot.Adapters
            .Where(a => a.Kind != AdapterKind.Loopback)
            .OrderByDescending(a => a.IsUp)
            .ThenBy(a => a.Name, StringComparer.CurrentCulture)
            .Select(a => new AdapterCard(
                a.Name,
                a.Kind switch
                {
                    AdapterKind.Ethernet => "有线",
                    AdapterKind.Wifi => "Wi-Fi",
                    AdapterKind.Tunnel => "隧道",
                    AdapterKind.Ppp => "拨号/PPP",
                    _ => "其他",
                },
                a.Description,
                a.IsUp ? AdapterPalette.For(a) : AdapterPalette.Disconnected,
                a.IsUp,
                Join(a.Addresses.Where(x => !x.Address.IsIPv6LinkLocal).Select(x => x.ToString())),
                Join(a.Gateways.Select(g => g.ToString())),
                Join(a.DnsServers.Select(d => d.ToString())),
                $"IPv4 {a.MetricV4?.ToString() ?? "-"} · IPv6 {a.MetricV6?.ToString() ?? "-"}",
                a.TunnelManager,
                exit?.Luid == a.Luid))
            .ToList();
    }

    private static string Join(IEnumerable<string> items)
    {
        string text = string.Join("、", items);
        return text.Length == 0 ? "无" : text;
    }
}
