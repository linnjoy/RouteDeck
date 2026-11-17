using RouteDeck.Core.Abstractions;
using RouteDeck.Core.Journal;
using RouteDeck.Core.Models;

namespace RouteDeck.Core.Changes;

public sealed record PendingChange(long OperationId, string Description, string Reason, DateTimeOffset Deadline);

public sealed record ExecuteResult(long OperationId, ApplyResult Apply, PendingChange? Pending, VerifyResult? Verify)
{
    public bool Succeeded => Apply.Succeeded;
}

public sealed record VerifyResult(IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0;
}

public sealed class ChangeRejectedException(string message) : Exception(message);

/// <summary>
/// The only way the service changes the network. Journals ahead of every step, keeps ownership,
/// enforces the confirmation countdown for risky changes, and restores safely on timeout or after a crash.
/// </summary>
public sealed class ChangeExecutor : IDisposable
{
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(60);

    private readonly INetworkApi _network;
    private readonly OperationJournal _journal;
    private readonly TimeProvider _time;
    private readonly ChangeSetApplier _applier;
    private readonly object _lock = new();
    private ITimer? _deadlineTimer;

    public ChangeExecutor(INetworkApi network, OperationJournal journal, TimeProvider? time = null)
    {
        _network = network;
        _journal = journal;
        _time = time ?? TimeProvider.System;
        _applier = new ChangeSetApplier(network);
    }

    public PendingChange? Pending { get; private set; }

    /// <summary>Raised after any operation is applied, reverted or confirmed (operation id, human-readable message).</summary>
    public event Action<long, string>? Changed;

    /// <summary>
    /// Restores operations that were running or awaiting confirmation when the service stopped.
    /// Call once at startup, before accepting requests.
    /// </summary>
    public IReadOnlyList<string> RecoverOnStartup()
    {
        var messages = new List<string>();
        lock (_lock)
        {
            foreach (OperationRecord record in _journal.Unfinished())
            {
                if (record.Status == OperationStatus.PendingConfirm && record.ConfirmDeadline > _time.GetUtcNow())
                {
                    // Service restarted within the window: keep counting down from the stored deadline.
                    ArmDeadline(new PendingChange(record.Id, record.ChangeSet.Description, "服务重启前未确认", record.ConfirmDeadline!.Value));
                    messages.Add($"操作 #{record.Id} 仍在等待确认");
                    continue;
                }
                messages.Add(RevertLocked(record, includeInFlightStep: record.Status == OperationStatus.Running, "服务启动时发现未完成的操作"));
            }
        }
        return messages;
    }

    /// <summary>Applies a change. Risky changes stay pending until <see cref="Confirm"/> or are restored at the deadline.</summary>
    public ExecuteResult Execute(ChangeSet changeSet)
    {
        lock (_lock)
        {
            if (Pending is not null)
                throw new ChangeRejectedException("还有一个变更在等待确认，请先保留或恢复它。");

            NetworkSnapshot before = _network.GetSnapshot();
            string? risk = LockoutGuard.RiskReason(changeSet, before);
            if (risk is not null && changeSet.Origin.StartsWith("rule:", StringComparison.Ordinal))
                throw new ChangeRejectedException($"自动规则不能执行这个变更：{risk}。");

            DateTimeOffset? deadline = risk is null ? null : _time.GetUtcNow() + ConfirmWindow;
            long id = _journal.Begin(changeSet, deadline);
            ApplyResult result = _applier.Apply(changeSet, (index, op) => _journal.StepApplied(id, index, op));

            if (!result.Succeeded)
            {
                _journal.StepsRolledBack(id, result.Applied);
                _journal.SetStatus(id, result.RollbackError is null ? OperationStatus.Failed : OperationStatus.RollbackFailed,
                    result.RollbackError is null ? result.Error : $"{result.Error}；{result.RollbackError}");
                Changed?.Invoke(id, result.Error!);
                return new ExecuteResult(id, result, null, null);
            }

            VerifyResult verify = Verify(changeSet, _network.GetSnapshot());
            PendingChange? pending = null;
            if (deadline is not null)
            {
                _journal.SetStatus(id, OperationStatus.PendingConfirm);
                pending = new PendingChange(id, changeSet.Description, risk!, deadline.Value);
                ArmDeadline(pending);
            }
            else
            {
                _journal.SetStatus(id, OperationStatus.Applied);
            }
            Changed?.Invoke(id, changeSet.Description);
            return new ExecuteResult(id, result, pending, verify);
        }
    }

    public void Confirm(long operationId)
    {
        lock (_lock)
        {
            if (Pending?.OperationId != operationId) throw new ChangeRejectedException("没有这个待确认的变更，它可能已经超时恢复。");
            DisarmDeadline();
            _journal.SetStatus(operationId, OperationStatus.Applied);
            Changed?.Invoke(operationId, "已保留更改");
        }
    }

    /// <summary>User chose "restore now" during the countdown.</summary>
    public string RevertPending(long operationId)
    {
        lock (_lock)
        {
            if (Pending?.OperationId != operationId) throw new ChangeRejectedException("没有这个待确认的变更。");
            OperationRecord record = _journal.Get(operationId)!;
            DisarmDeadline();
            return RevertLocked(record, includeInFlightStep: false, "已按要求恢复");
        }
    }

    public UndoPlan PreviewUndo(long operationId)
    {
        lock (_lock)
        {
            OperationRecord record = _journal.Get(operationId) ?? throw new ChangeRejectedException("找不到这次操作。");
            if (record.UndoneBy is not null) throw new ChangeRejectedException($"这次操作已被 #{record.UndoneBy} 撤销。");
            if (record.Status is not (OperationStatus.Applied or OperationStatus.PendingConfirm))
                throw new ChangeRejectedException("只有已生效的操作才能撤销。");
            return UndoPlanner.Plan(record, _network.GetSnapshot(), _journal.EffectiveAfter(operationId));
        }
    }

    /// <summary>Re-plans against the current state (it may have changed since the preview) and applies the safe items.</summary>
    public ExecuteResult ApplyUndo(long operationId)
    {
        UndoPlan plan = PreviewUndo(operationId);
        OperationRecord record = _journal.Get(operationId)!;
        ChangeSet undo = plan.ToChangeSet(ChangeSet.ManualOrigin, $"撤销 #{operationId}：{record.ChangeSet.Description}");

        lock (_lock)
        {
            if (Pending?.OperationId == operationId) DisarmDeadline();
        }
        ExecuteResult result = Execute(undo);
        if (result.Succeeded) _journal.MarkUndone(operationId, result.OperationId);
        return result;
    }

    public IReadOnlyDictionary<RouteKey, OwnedRoute> Owned() => _journal.Owned();

    public void Dispose() => DisarmDeadline();

    private string RevertLocked(OperationRecord record, bool includeInFlightStep, string reason)
    {
        UndoPlan plan = UndoPlanner.Plan(record, _network.GetSnapshot(), _journal.EffectiveAfter(record.Id), includeInFlightStep);
        ChangeSet restore = plan.ToChangeSet("recover", $"恢复 #{record.Id}：{record.ChangeSet.Description}");

        long restoreId = _journal.Begin(restore, undoOf: record.Id);
        ApplyResult result = _applier.Apply(restore, (index, op) => _journal.StepApplied(restoreId, index, op));
        _journal.SetStatus(restoreId, result.Succeeded ? OperationStatus.Applied : OperationStatus.Failed, result.Error);
        _journal.SetStatus(record.Id, OperationStatus.Reverted, reason);
        _journal.MarkUndone(record.Id, restoreId);

        string skipped = plan.HasConflicts
            ? $"，{plan.Items.Count(i => i.State == UndoItemState.Conflict)} 项因被其他程序改动而保留"
            : "";
        string message = result.Succeeded
            ? $"{reason}：已恢复 #{record.Id}{skipped}"
            : $"{reason}：恢复 #{record.Id} 失败（{result.Error}），请在操作历史中检查";
        Changed?.Invoke(record.Id, message);
        return message;
    }

    private void ArmDeadline(PendingChange pending)
    {
        Pending = pending;
        TimeSpan due = pending.Deadline - _time.GetUtcNow();
        _deadlineTimer?.Dispose();
        _deadlineTimer = _time.CreateTimer(_ => OnDeadline(pending.OperationId), null, due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }

    private void DisarmDeadline()
    {
        _deadlineTimer?.Dispose();
        _deadlineTimer = null;
        Pending = null;
    }

    private void OnDeadline(long operationId)
    {
        lock (_lock)
        {
            if (Pending?.OperationId != operationId) return;
            OperationRecord record = _journal.Get(operationId)!;
            DisarmDeadline();
            RevertLocked(record, includeInFlightStep: false, "60 秒内未确认，已自动恢复");
        }
    }

    private static VerifyResult Verify(ChangeSet changeSet, NetworkSnapshot after)
    {
        var problems = new List<string>();
        foreach (RouteOperation op in changeSet.Operations)
        {
            Route? route = after.FindRoute(op.Route.Key);
            string? problem = op.Kind switch
            {
                RouteOperationKind.Add when route is null => $"“{op.Describe()}”执行后没有在路由表中出现",
                RouteOperationKind.Remove when route is not null && route.InActiveStore => $"“{op.Describe()}”执行后路由仍然存在（可能被其他程序重新创建）",
                RouteOperationKind.SetMetric when route is not null && route.Metric != op.NewMetric => $"“{op.Describe()}”执行后跃点是 {route.Metric}",
                _ => null,
            };
            if (problem is not null) problems.Add(problem);
        }
        return new VerifyResult(problems);
    }
}
