using RouteDeck.Core.Changes;
using RouteDeck.Core.Journal;
using RouteDeck.Core.Models;

namespace RouteDeck.Contracts;

public sealed record ServiceInfo(int ProtocolVersion, string Version, bool CanWrite, string? ReadOnlyReason, IReadOnlyList<string> StartupMessages);

/// <summary>
/// What the SYSTEM service exposes over the named pipe. Deliberately small: everything that only
/// reads (lookup, diagnostics, proxy, connectivity) runs in the unprivileged GUI.
/// </summary>
public interface IRouteDeckService
{
    Task<ServiceInfo> GetServiceInfoAsync();

    /// <summary>Snapshot with <see cref="Route.IsManual"/> set for routes RouteDeck owns.</summary>
    Task<NetworkSnapshot> GetSnapshotAsync();

    /// <param name="confirmForeign">Required when the change removes or modifies routes RouteDeck does not own.</param>
    Task<ExecuteResult> ExecuteAsync(ChangeSet changeSet, bool confirmForeign);

    Task<UndoPlan> PreviewUndoAsync(long operationId);

    Task<ExecuteResult> ApplyUndoAsync(long operationId);

    Task<IReadOnlyList<OperationRecord>> GetRecentOperationsAsync(int count);

    Task<PendingChange?> GetPendingChangeAsync();

    Task ConfirmPendingChangeAsync(long operationId);

    Task<string> RevertPendingChangeAsync(long operationId);
}

/// <summary>Notifications the service pushes to connected GUIs.</summary>
public interface IRouteDeckEvents
{
    void OnStateChanged();

    void OnOperation(long operationId, string message);

    void OnPendingCountdown(PendingChange? pending, int secondsLeft);
}
