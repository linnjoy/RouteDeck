using System.Reflection;
using RouteDeck.Contracts;
using RouteDeck.Core.Abstractions;
using RouteDeck.Core.Changes;
using RouteDeck.Core.Journal;
using RouteDeck.Core.Models;

namespace RouteDeck.Service;

/// <summary>One instance per pipe connection. Untrusted callers get read access only.</summary>
internal sealed class RouteDeckService(ServiceCore core, bool canWrite, string? readOnlyReason) : IRouteDeckService
{
    public Task<ServiceInfo> GetServiceInfoAsync() => Task.FromResult(new ServiceInfo(
        RouteDeckRpc.ProtocolVersion,
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0",
        canWrite,
        readOnlyReason,
        core.StartupMessages));

    public Task<NetworkSnapshot> GetSnapshotAsync() => Task.FromResult(core.AnnotatedSnapshot());

    public Task<ExecuteResult> ExecuteAsync(ChangeSet changeSet, bool confirmForeign) => RunWriteAsync(() =>
    {
        // Clients may only act as the user; "recover" and "rule:" origins are reserved for the service.
        string origin = changeSet.Origin.StartsWith("fix:", StringComparison.Ordinal) ? changeSet.Origin : ChangeSet.ManualOrigin;
        changeSet = changeSet with { Origin = origin };

        if (changeSet.Operations.Count == 0) throw new ChangeRejectedException("没有要执行的操作。");
        if (!confirmForeign)
        {
            var owned = core.Executor.Owned();
            RouteOperation? foreign = changeSet.Operations.FirstOrDefault(op =>
                op.Kind != RouteOperationKind.Add && !owned.ContainsKey(op.Route.Key));
            if (foreign is not null)
                throw new ChangeRejectedException($"“{foreign.Describe()}”涉及不是 RouteDeck 创建的路由，需要明确确认。");
        }
        return core.Executor.Execute(changeSet);
    });

    public Task<UndoPlan> PreviewUndoAsync(long operationId) => Task.FromResult(core.Executor.PreviewUndo(operationId));

    public Task<ExecuteResult> ApplyUndoAsync(long operationId) => RunWriteAsync(() => core.Executor.ApplyUndo(operationId));

    public Task<IReadOnlyList<OperationRecord>> GetRecentOperationsAsync(int count) =>
        Task.FromResult(core.Journal.Recent(Math.Clamp(count, 1, 500)));

    public Task<PendingChange?> GetPendingChangeAsync() => Task.FromResult(core.Executor.Pending);

    public Task ConfirmPendingChangeAsync(long operationId) => RunWriteAsync(() =>
    {
        core.Executor.Confirm(operationId);
        return true;
    });

    public Task<string> RevertPendingChangeAsync(long operationId) => RunWriteAsync(() => core.Executor.RevertPending(operationId));

    private Task<T> RunWriteAsync<T>(Func<T> write)
    {
        if (!canWrite) throw new ChangeRejectedException(readOnlyReason ?? "这个连接没有写权限。");
        try
        {
            return Task.FromResult(write());
        }
        catch (NetworkApiException ex)
        {
            throw new ChangeRejectedException(ex.Message);
        }
    }
}
