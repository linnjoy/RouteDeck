namespace RouteDeck.Core.Models;

public enum RouteOperationKind
{
    Add,
    Remove,
    SetMetric,
}

/// <summary>One step of a change. For SetMetric, <see cref="Route"/> carries the current metric and <see cref="NewMetric"/> the target.</summary>
public sealed record RouteOperation(RouteOperationKind Kind, RouteSpec Route, int? NewMetric = null)
{
    public static RouteOperation Add(RouteSpec route) => new(RouteOperationKind.Add, route);

    public static RouteOperation Remove(RouteSpec route) => new(RouteOperationKind.Remove, route);

    public static RouteOperation SetMetric(RouteSpec route, int metric) => new(RouteOperationKind.SetMetric, route, metric);

    public RouteOperation Inverse() => Kind switch
    {
        RouteOperationKind.Add => Remove(Route),
        RouteOperationKind.Remove => Add(Route),
        RouteOperationKind.SetMetric => SetMetric(Route with { Metric = NewMetric!.Value }, Route.Metric),
        _ => throw new InvalidOperationException(),
    };

    public string Describe() => Kind switch
    {
        RouteOperationKind.Add => $"添加 {Route.Destination} → {Route.NextHop?.ToString() ?? "在链路上"}（跃点 {Route.Metric}{(Route.Persistent ? "，持久" : "")}）",
        RouteOperationKind.Remove => $"删除 {Route.Destination} → {Route.NextHop?.ToString() ?? "在链路上"}",
        RouteOperationKind.SetMetric => $"修改 {Route.Destination} 的跃点：{Route.Metric} → {NewMetric}",
        _ => Kind.ToString(),
    };
}

/// <summary>An ordered list of operations applied as one transaction.</summary>
/// <param name="Origin">"manual", "fix:&lt;rule&gt;", later "profile:&lt;name&gt;" or "rule:&lt;name&gt;".</param>
public sealed record ChangeSet(string Origin, string Description, IReadOnlyList<RouteOperation> Operations)
{
    public const string ManualOrigin = "manual";

    public ChangeSet Inverse() =>
        this with { Description = "撤销：" + Description, Operations = Operations.Reverse().Select(o => o.Inverse()).ToList() };
}
