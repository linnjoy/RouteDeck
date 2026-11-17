namespace RouteDeck.Core.Connectivity;

/// <summary>Turns layered probe results into one conclusion. Each layer is judged on its own evidence.</summary>
public static class ConnectivitySummary
{
    public static string Summarize(IReadOnlyList<ProbeStep> steps, int port)
    {
        ProbeStep? route = Find(steps, ProbeLayer.Route);
        ProbeStep? gateway = Find(steps, ProbeLayer.Gateway);
        ProbeStep? tcp = Find(steps, ProbeLayer.TcpDirect);
        ProbeStep? proxy = Find(steps, ProbeLayer.TcpProxy);
        ProbeStep? http = Find(steps, ProbeLayer.HttpDirect);

        if (route is { IsFailure: true } && tcp is not { IsOk: true })
            return "没有可用的路由到达目标，先为它添加路由。";

        if (tcp is { IsOk: true })
        {
            if (proxy is { IsFailure: true })
                return $"直连 TCP {port} 正常，经代理失败：问题在代理（规则或代理出口），不在系统路由。";
            if (http is { IsFailure: true })
                return $"TCP {port} 已连通，但 HTTP 没有正常响应：检查目标服务本身。";
            return $"路由与 TCP {port} 均正常" + (http is { IsOk: true } ? $"，{http.Detail}。" : "。");
        }

        if (proxy is { IsOk: true })
            return $"直连 TCP {port} 不通，但经代理可达：代理有通往目标的路径，系统路由没有（或被回程/ACL 阻断）。";

        return gateway switch
        {
            { IsOk: true } =>
                $"路由正常、网关可达，但 TCP {port} {Describe(tcp)}：检查目标服务是否在运行、防火墙/ACL，或目标到本机的回程路由。不建议因此删除路由。",
            { IsFailure: true } =>
                "网关没有响应：检查出口网卡是否选对、网关地址是否正确、网线/Wi-Fi 是否连在正确的网络上。",
            { Status: ProbeStatus.NotApplicable } =>
                $"经隧道或点对点接口转发，TCP {port} {Describe(tcp)}：问题可能在隧道对端、代理规则或目标本身。",
            _ => $"TCP {port} {Describe(tcp)}。",
        };
    }

    private static ProbeStep? Find(IReadOnlyList<ProbeStep> steps, ProbeLayer layer) => steps.FirstOrDefault(s => s.Layer == layer);

    private static string Describe(ProbeStep? tcp) => tcp?.Status == ProbeStatus.Timeout ? "超时" : "被拒绝或失败";
}
