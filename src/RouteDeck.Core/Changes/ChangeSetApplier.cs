using RouteDeck.Core.Abstractions;
using RouteDeck.Core.Models;
using RouteDeck.Core.Native;

namespace RouteDeck.Core.Changes;

/// <param name="Applied">Steps that executed (on failure: the ones that were then rolled back).</param>
public sealed record ApplyResult(
    bool Succeeded,
    IReadOnlyList<RouteOperation> Applied,
    int? FailedStep = null,
    string? Error = null,
    int? ErrorCode = null,
    bool RolledBack = false,
    string? RollbackError = null)
{
    public static ApplyResult Empty { get; } = new(true, []);
}

/// <summary>Applies operations in order; on failure, undoes the completed ones in reverse.</summary>
public sealed class ChangeSetApplier(INetworkApi network)
{
    /// <param name="onStepApplied">Called after each successful step with its index (for the write-ahead journal).</param>
    public ApplyResult Apply(ChangeSet changeSet, Action<int, RouteOperation>? onStepApplied = null)
    {
        var applied = new List<RouteOperation>();
        for (int i = 0; i < changeSet.Operations.Count; i++)
        {
            RouteOperation op = changeSet.Operations[i];
            try
            {
                if (Execute(op)) applied.Add(op);
                onStepApplied?.Invoke(i, op);
            }
            catch (NetworkApiException ex)
            {
                string? rollbackError = Rollback(applied);
                return new ApplyResult(false, applied, i, $"第 {i + 1} 步“{op.Describe()}”失败：{ex.Message}", ex.ErrorCode, true, rollbackError);
            }
        }
        return new ApplyResult(true, applied);
    }

    /// <summary>Returns false when the step was a no-op (removing something already gone).</summary>
    private bool Execute(RouteOperation op)
    {
        switch (op.Kind)
        {
            case RouteOperationKind.Add:
                network.AddRoute(op.Route);
                return true;
            case RouteOperationKind.Remove:
                try
                {
                    network.RemoveRoute(op.Route);
                    return true;
                }
                catch (NetworkApiException ex) when (ex.ErrorCode == IpHelper.ERROR_NOT_FOUND)
                {
                    return false;
                }
            case RouteOperationKind.SetMetric:
                network.SetRouteMetric(op.Route, op.NewMetric!.Value);
                return true;
            default:
                throw new InvalidOperationException(op.Kind.ToString());
        }
    }

    private string? Rollback(List<RouteOperation> applied)
    {
        var errors = new List<string>();
        foreach (RouteOperation op in Enumerable.Reverse(applied))
        {
            try
            {
                Execute(op.Inverse());
            }
            catch (NetworkApiException ex)
            {
                errors.Add($"“{op.Inverse().Describe()}”：{ex.Message}");
            }
        }
        return errors.Count == 0 ? null : "回滚未完全成功：" + string.Join("；", errors);
    }
}
