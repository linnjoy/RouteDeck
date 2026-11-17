using System.ComponentModel;
using RouteDeck.Core.Abstractions;

namespace RouteDeck.Core.Native;

/// <summary>Turns Win32 error codes into Chinese explanations with a suggested next step.</summary>
public static class Win32ErrorMessages
{
    private static readonly Dictionary<int, string> Messages = new()
    {
        [2] = "找不到指定的网卡，它可能已被移除或禁用。刷新后重新选择网卡。",
        [5] = "没有权限修改路由。确认 RouteDeck 服务正在运行。",
        [50] = "系统不支持这个操作。",
        [87] = "参数无效。检查网段、网关和网卡是否属于同一协议（IPv4/IPv6）。",
        [1168] = "找不到该路由，它可能已被其他程序删除。刷新后再试。",
        [1231] = "网络不可达。检查网关是否在该网卡的子网内。",
        [1232] = "主机不可达。",
        [5010] = "路由已存在。如果只想改跃点，请改用编辑。",
    };

    public static string Describe(int errorCode) =>
        Messages.TryGetValue(errorCode, out string? message)
            ? message
            : $"系统返回错误：{new Win32Exception(errorCode).Message}";

    public static NetworkApiException ToException(int errorCode, string action) =>
        new(errorCode, $"{action}失败：{Describe(errorCode)}");

    public static void ThrowIfFailed(int errorCode, string action)
    {
        if (errorCode != IpHelper.ERROR_SUCCESS) throw ToException(errorCode, action);
    }
}
