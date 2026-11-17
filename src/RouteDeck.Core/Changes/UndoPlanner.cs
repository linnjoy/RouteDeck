using RouteDeck.Core.Journal;
using RouteDeck.Core.Models;

namespace RouteDeck.Core.Changes;

public enum UndoItemState
{
    /// <summary>Current state matches what RouteDeck wrote; the inverse will be applied.</summary>
    WillApply,
    /// <summary>Already in the target state (e.g. someone else removed the route); nothing to do.</summary>
    AlreadyDone,
    /// <summary>Changed by something else since RouteDeck wrote it; skipped unless the user decides otherwise.</summary>
    Conflict,
}

public sealed record UndoItem(RouteOperation Inverse, UndoItemState State, string Note);

public sealed record UndoPlan(long OperationId, IReadOnlyList<UndoItem> Items)
{
    public ChangeSet ToChangeSet(string origin, string description) =>
        new(origin, description, Items.Where(i => i.State == UndoItemState.WillApply).Select(i => i.Inverse).ToList());

    public bool HasConflicts => Items.Any(i => i.State == UndoItemState.Conflict);
}

/// <summary>
/// Builds a restore diff for one operation. Never restores a snapshot blindly: each step is only
/// reversed when the current value still equals what RouteDeck last wrote.
/// </summary>
public static class UndoPlanner
{
    /// <param name="includeInFlightStep">
    /// For crash recovery: the step after the last recorded one may have executed before the crash.
    /// Its inverse is checked against the current state like any other step.
    /// </param>
    public static UndoPlan Plan(OperationRecord record, NetworkSnapshot snapshot, IReadOnlyList<OperationRecord> laterOperations, bool includeInFlightStep = false)
    {
        int steps = Math.Min(record.ChangeSet.Operations.Count, record.AppliedSteps + (includeInFlightStep ? 1 : 0));
        var touchedLater = laterOperations
            .SelectMany(r => r.AppliedOperations.Select(o => (o.Route.Key, r.Id)))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.First().Id);

        var items = new List<UndoItem>();
        foreach (RouteOperation op in record.ChangeSet.Operations.Take(steps).Reverse())
        {
            RouteOperation inverse = op.Inverse();
            Route? current = snapshot.FindRoute(op.Route.Key);

            if (touchedLater.TryGetValue(op.Route.Key, out long laterId))
            {
                items.Add(new UndoItem(inverse, UndoItemState.Conflict, $"之后的操作 #{laterId} 也修改了它，请先撤销那次操作"));
                continue;
            }

            items.Add(op.Kind switch
            {
                RouteOperationKind.Add => current switch
                {
                    null => new UndoItem(inverse, UndoItemState.AlreadyDone, "路由已不存在，无需删除"),
                    _ when current.Metric != op.Route.Metric =>
                        new UndoItem(inverse, UndoItemState.Conflict, $"跃点已被改成 {current.Metric}（RouteDeck 写入的是 {op.Route.Metric}）"),
                    _ => new UndoItem(inverse, UndoItemState.WillApply, ""),
                },
                RouteOperationKind.Remove => current is null
                    ? new UndoItem(inverse, UndoItemState.WillApply, "")
                    : new UndoItem(inverse, UndoItemState.AlreadyDone, "路由已被重新创建（可能是原来的软件），保持现状"),
                RouteOperationKind.SetMetric => current switch
                {
                    null => new UndoItem(inverse, UndoItemState.Conflict, "路由已不存在，无法恢复跃点"),
                    _ when current.Metric != op.NewMetric =>
                        new UndoItem(inverse, UndoItemState.Conflict, $"跃点已被改成 {current.Metric}（RouteDeck 写入的是 {op.NewMetric}）"),
                    _ => new UndoItem(inverse, UndoItemState.WillApply, ""),
                },
                _ => throw new InvalidOperationException(),
            });
        }
        return new UndoPlan(record.Id, items);
    }
}
