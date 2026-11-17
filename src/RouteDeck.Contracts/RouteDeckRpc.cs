using RouteDeck.Core.Models;
using RouteDeck.Core.Serialization;
using StreamJsonRpc;

namespace RouteDeck.Contracts;

/// <summary>Shared wire setup so both ends always agree on framing and JSON.</summary>
public static class RouteDeckRpc
{
    public const int ProtocolVersion = 1;

    public const string PipeName = "RouteDeck";

    public static JsonRpc Create(Stream stream)
    {
        var formatter = new SystemTextJsonFormatter { JsonSerializerOptions = RouteDeckJson.Create() };
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, formatter));
        return rpc;
    }

    public static readonly JsonRpcTargetOptions TargetOptions = new()
    {
        MethodNameTransform = CommonMethodNameTransforms.CamelCase,
        UseSingleObjectParameterDeserialization = false,
    };

    public static readonly JsonRpcProxyOptions ProxyOptions = new()
    {
        MethodNameTransform = CommonMethodNameTransforms.CamelCase,
    };
}

/// <summary>Client-side helpers that turn user intent into change sets.</summary>
public static class ChangeSets
{
    public static ChangeSet Add(RouteSpec route) =>
        new(ChangeSet.ManualOrigin, RouteOperation.Add(route).Describe(), [RouteOperation.Add(route)]);

    public static ChangeSet Remove(Route route) =>
        new(ChangeSet.ManualOrigin, RouteOperation.Remove(route.ToSpec()).Describe(), [RouteOperation.Remove(route.ToSpec())]);

    /// <summary>Metric-only edits stay a single SetMetric; anything else is remove + add.</summary>
    public static ChangeSet Edit(Route original, RouteSpec updated)
    {
        RouteSpec before = original.ToSpec();
        if (before.Key == updated.Key && before.Persistent == updated.Persistent)
        {
            RouteOperation op = RouteOperation.SetMetric(before, updated.Metric);
            return new ChangeSet(ChangeSet.ManualOrigin, op.Describe(), [op]);
        }
        return new ChangeSet(
            ChangeSet.ManualOrigin,
            $"编辑 {original.Destination}",
            [RouteOperation.Remove(before), RouteOperation.Add(updated)]);
    }
}
