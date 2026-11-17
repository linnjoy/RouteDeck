using System.Net;
using Microsoft.Extensions.Time.Testing;
using RouteDeck.Core.Changes;
using RouteDeck.Core.Journal;
using RouteDeck.Core.Lookup;
using RouteDeck.Core.Models;
using RouteDeck.Tests.Fakes;

namespace RouteDeck.Tests.Changes;

public sealed class ChangeExecutorTests : IDisposable
{
    private const ulong Wired = 14, Wifi = 17, Mihomo = 4;
    private readonly FakeNetworkApi _net = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-18T10:00:00Z"));
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"routedeck-test-{Guid.NewGuid():N}.db");
    private readonly List<IDisposable> _disposables = [];

    public ChangeExecutorTests()
    {
        _net.AddAdapter(Wired, "以太网 2", address: "192.168.21.230/24", metric: 35, gateway: "192.168.21.1");
        _net.AddAdapter(Wifi, "WLAN", AdapterKind.Wifi, address: "192.168.8.3/24", metric: 10, gateway: "192.168.8.1");
        _net.AddAdapter(Mihomo, "Mihomo", AdapterKind.Tunnel, address: "198.18.0.1/30", metric: 0, manager: "Mihomo/Clash");
        _net.AddExisting("0.0.0.0/0", "192.168.21.1", Wired, metric: 256, persistent: true);
        _net.AddExisting("0.0.0.0/0", "192.168.8.1", Wifi, metric: 10);
        _net.AddExisting("0.0.0.0/1", null, Mihomo);
        _net.AddExisting("128.0.0.0/1", null, Mihomo);
    }

    public void Dispose()
    {
        foreach (IDisposable d in Enumerable.Reverse(_disposables)) d.Dispose();
        File.Delete(_dbPath);
    }

    private OperationJournal Journal()
    {
        var journal = new OperationJournal(_dbPath, _time);
        _disposables.Add(journal);
        return journal;
    }

    private (ChangeExecutor Executor, OperationJournal Journal) Create()
    {
        OperationJournal journal = Journal();
        var executor = new ChangeExecutor(_net, journal, _time);
        _disposables.Add(executor);
        return (executor, journal);
    }

    private static RouteSpec Spec(string destination, string? hop, ulong luid, int metric = 1) =>
        new(IpPrefix.Parse(destination), hop is null ? null : IPAddress.Parse(hop), luid, metric, false);

    private static ChangeSet Set(params RouteOperation[] ops) => new(ChangeSet.ManualOrigin, "test", ops);

    private ChangeSet Bypass() =>
        BypassPlanner.Plan(_net.GetSnapshot(), IpPrefix.Parse("10.0.1.66/32"), Wired, IPAddress.Parse("192.168.21.1"));

    private Route WinnerFor(string address) => RouteLookup.Lookup(_net.GetSnapshot(), IPAddress.Parse(address)).Winner!;

    [Fact]
    public void Field_case_bypass_route_then_undo_touches_only_its_own_route()
    {
        var (executor, journal) = Create();
        int routesBefore = _net.Routes.Count;

        ExecuteResult result = executor.Execute(Bypass());

        Assert.True(result.Succeeded);
        Assert.Null(result.Pending);
        Assert.True(result.Verify!.Ok);
        Assert.Equal(Wired, WinnerFor("10.0.1.66").InterfaceLuid);
        Assert.Equal(Mihomo, WinnerFor("8.8.8.8").InterfaceLuid);
        Assert.Contains(IpPrefix.Parse("10.0.1.66/32"), executor.Owned().Keys.Select(k => k.Destination));

        UndoPlan plan = executor.PreviewUndo(result.OperationId);
        Assert.Equal(UndoItemState.WillApply, Assert.Single(plan.Items).State);
        Assert.True(executor.ApplyUndo(result.OperationId).Succeeded);

        Assert.Equal(routesBefore, _net.Routes.Count);
        Assert.Equal(2, _net.Routes.Count(r => r.InterfaceLuid == Mihomo));
        Assert.Empty(executor.Owned());
        Assert.Equal(OperationStatus.Undone, journal.Get(result.OperationId)!.Status);
        Assert.DoesNotContain(_net.Log, l => l.Contains($"if {Mihomo}") && !l.StartsWith("add 10.0.1.66"));
    }

    [Fact]
    public void Failure_rolls_back_completed_steps_and_records_nothing_owned()
    {
        var (executor, journal) = Create();
        _net.FailOnCall.Add(2);

        ExecuteResult result = executor.Execute(Set(
            RouteOperation.Add(Spec("10.1.0.0/16", "192.168.21.1", Wired)),
            RouteOperation.Add(Spec("10.2.0.0/16", "192.168.21.1", Wired))));

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.Apply.FailedStep);
        Assert.DoesNotContain(_net.Routes, r => r.Destination == IpPrefix.Parse("10.1.0.0/16"));
        Assert.Empty(executor.Owned());
        OperationRecord record = journal.Get(result.OperationId)!;
        Assert.Equal(OperationStatus.Failed, record.Status);
        Assert.Equal(0, record.AppliedSteps);
        Assert.Contains("第 2 步", record.Error);
    }

    [Fact]
    public void Rollback_failure_is_reported()
    {
        var (executor, journal) = Create();
        _net.FailOnCall.UnionWith([2, 3]);

        ExecuteResult result = executor.Execute(Set(
            RouteOperation.Add(Spec("10.1.0.0/16", "192.168.21.1", Wired)),
            RouteOperation.Add(Spec("10.2.0.0/16", "192.168.21.1", Wired))));

        Assert.NotNull(result.Apply.RollbackError);
        Assert.Equal(OperationStatus.RollbackFailed, journal.Get(result.OperationId)!.Status);
    }

    [Fact]
    public void Removing_a_route_that_is_already_gone_is_not_an_error()
    {
        var (executor, _) = Create();
        Assert.True(executor.Execute(Set(RouteOperation.Remove(Spec("10.99.0.0/16", null, Wired)))).Succeeded);
    }

    [Fact]
    public void Changing_the_public_exit_needs_confirmation_and_reverts_on_timeout()
    {
        _net.Routes.RemoveAll(r => r.InterfaceLuid == Mihomo);
        var (executor, journal) = Create();
        RouteSpec wifiDefault = _net.Routes.Single(r => r.InterfaceLuid == Wifi).ToSpec();

        ExecuteResult result = executor.Execute(Set(RouteOperation.Remove(wifiDefault)));

        Assert.NotNull(result.Pending);
        Assert.Equal(_time.GetUtcNow() + ChangeExecutor.ConfirmWindow, result.Pending!.Deadline);
        Assert.Equal(Wired, WinnerFor("8.8.8.8").InterfaceLuid);
        Assert.Equal(OperationStatus.PendingConfirm, journal.Get(result.OperationId)!.Status);
        Assert.Throws<ChangeRejectedException>(() => executor.Execute(Bypass()));

        _time.Advance(TimeSpan.FromSeconds(61));

        Assert.Null(executor.Pending);
        Assert.Equal(Wifi, WinnerFor("8.8.8.8").InterfaceLuid);
        Assert.Equal(OperationStatus.Reverted, journal.Get(result.OperationId)!.Status);
    }

    [Fact]
    public void Confirmed_change_stays()
    {
        _net.Routes.RemoveAll(r => r.InterfaceLuid == Mihomo);
        var (executor, journal) = Create();
        RouteSpec wifiDefault = _net.Routes.Single(r => r.InterfaceLuid == Wifi).ToSpec();

        ExecuteResult result = executor.Execute(Set(RouteOperation.Remove(wifiDefault)));
        executor.Confirm(result.OperationId);
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(Wired, WinnerFor("8.8.8.8").InterfaceLuid);
        Assert.Equal(OperationStatus.Applied, journal.Get(result.OperationId)!.Status);
    }

    [Fact]
    public void Adding_a_takeover_route_is_risky_but_a_host_route_is_not()
    {
        NetworkSnapshot snapshot = _net.GetSnapshot();
        Assert.False(LockoutGuard.IsRisky(Bypass(), snapshot));
        Assert.True(LockoutGuard.IsRisky(Set(RouteOperation.Add(Spec("0.0.0.0/1", "192.168.21.1", Wired))), WithoutTunnel(snapshot)));
        Assert.False(LockoutGuard.IsRisky(Set(RouteOperation.SetMetric(Spec("0.0.0.0/0", "192.168.21.1", Wired, 256), 300)), snapshot));
    }

    private static NetworkSnapshot WithoutTunnel(NetworkSnapshot s) => s with { Routes = s.Routes.Where(r => r.InterfaceLuid != Mihomo).ToList() };

    [Fact]
    public void Rules_cannot_make_risky_changes()
    {
        _net.Routes.RemoveAll(r => r.InterfaceLuid == Mihomo);
        var (executor, _) = Create();
        RouteSpec wifiDefault = _net.Routes.Single(r => r.InterfaceLuid == Wifi).ToSpec();

        var ex = Assert.Throws<ChangeRejectedException>(() =>
            executor.Execute(new ChangeSet("rule:公司", "x", [RouteOperation.Remove(wifiDefault)])));
        Assert.Contains("自动规则", ex.Message);
        Assert.Contains(_net.Routes, r => r.InterfaceLuid == Wifi);
    }

    [Fact]
    public void Undo_skips_route_changed_by_someone_else()
    {
        var (executor, _) = Create();
        ExecuteResult result = executor.Execute(Bypass());
        int index = _net.Routes.FindIndex(r => r.Destination == IpPrefix.Parse("10.0.1.66/32"));
        _net.Routes[index] = _net.Routes[index] with { Metric = 50 };

        UndoPlan plan = executor.PreviewUndo(result.OperationId);
        UndoItem item = Assert.Single(plan.Items);
        Assert.Equal(UndoItemState.Conflict, item.State);
        Assert.Contains("50", item.Note);

        executor.ApplyUndo(result.OperationId);
        Assert.Contains(_net.Routes, r => r.Destination == IpPrefix.Parse("10.0.1.66/32"));
    }

    [Fact]
    public void Undo_after_external_removal_is_already_done()
    {
        var (executor, _) = Create();
        ExecuteResult result = executor.Execute(Bypass());
        _net.Routes.RemoveAll(r => r.Destination == IpPrefix.Parse("10.0.1.66/32"));

        Assert.Equal(UndoItemState.AlreadyDone, Assert.Single(executor.PreviewUndo(result.OperationId).Items).State);
    }

    [Fact]
    public void Undoing_removal_of_a_recreated_foreign_route_keeps_the_new_one()
    {
        var (executor, _) = Create();
        RouteSpec tun = _net.Routes.First(r => r.InterfaceLuid == Mihomo).ToSpec();
        ExecuteResult result = executor.Execute(Set(RouteOperation.Remove(tun)));
        _net.AddExisting(tun.Destination.ToString(), null, Mihomo); // Clash rebuilt it

        Assert.Equal(UndoItemState.AlreadyDone, Assert.Single(executor.PreviewUndo(result.OperationId).Items).State);
        Assert.True(executor.ApplyUndo(result.OperationId).Succeeded);
        Assert.Single(_net.Routes, r => r.Key == tun.Key);
    }

    [Fact]
    public void Later_operation_on_same_route_blocks_undo_of_earlier_one()
    {
        var (executor, _) = Create();
        ExecuteResult first = executor.Execute(Bypass());
        RouteSpec added = first.Apply.Applied[0].Route;
        executor.Execute(Set(RouteOperation.SetMetric(added, 7)));

        UndoItem item = Assert.Single(executor.PreviewUndo(first.OperationId).Items);
        Assert.Equal(UndoItemState.Conflict, item.State);
        Assert.Contains("#2", item.Note);
    }

    [Fact]
    public void Crash_mid_change_is_recovered_on_startup_including_in_flight_step()
    {
        ChangeSet set = Set(
            RouteOperation.Add(Spec("10.1.0.0/16", "192.168.21.1", Wired)),
            RouteOperation.Add(Spec("10.2.0.0/16", "192.168.21.1", Wired)),
            RouteOperation.Add(Spec("10.3.0.0/16", "192.168.21.1", Wired)));

        // Simulate a service that journaled step 1, executed step 2, and crashed before journaling it.
        using (var crashed = new OperationJournal(_dbPath, _time))
        {
            long id = crashed.Begin(set);
            _net.AddRoute(set.Operations[0].Route);
            crashed.StepApplied(id, 0, set.Operations[0]);
            _net.AddRoute(set.Operations[1].Route);
        }

        var (executor, journal) = Create();
        IReadOnlyList<string> messages = executor.RecoverOnStartup();

        Assert.Single(messages);
        Assert.DoesNotContain(_net.Routes, r => r.Destination.ToString().StartsWith("10."));
        Assert.Equal(OperationStatus.Reverted, journal.Get(1)!.Status);
        Assert.Empty(journal.Unfinished());
    }

    [Fact]
    public void Pending_change_survives_restart_and_still_reverts_at_original_deadline()
    {
        _net.Routes.RemoveAll(r => r.InterfaceLuid == Mihomo);
        RouteSpec wifiDefault = _net.Routes.Single(r => r.InterfaceLuid == Wifi).ToSpec();
        long id;
        using (var journal = new OperationJournal(_dbPath, _time))
        using (var first = new ChangeExecutor(_net, journal, _time))
        {
            id = first.Execute(Set(RouteOperation.Remove(wifiDefault))).OperationId;
        }

        _time.Advance(TimeSpan.FromSeconds(30));
        var (executor, _) = Create();
        executor.RecoverOnStartup();
        Assert.Equal(id, executor.Pending!.OperationId);
        Assert.Equal(Wired, WinnerFor("8.8.8.8").InterfaceLuid);

        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(Wifi, WinnerFor("8.8.8.8").InterfaceLuid);
    }

    [Fact]
    public void Expired_pending_change_is_reverted_at_startup()
    {
        _net.Routes.RemoveAll(r => r.InterfaceLuid == Mihomo);
        RouteSpec wifiDefault = _net.Routes.Single(r => r.InterfaceLuid == Wifi).ToSpec();
        using (var journal = new OperationJournal(_dbPath, _time))
        using (var first = new ChangeExecutor(_net, journal, _time))
        {
            first.Execute(Set(RouteOperation.Remove(wifiDefault)));
        }

        _time.Advance(TimeSpan.FromMinutes(10));
        var (executor, _) = Create();
        executor.RecoverOnStartup();

        Assert.Null(executor.Pending);
        Assert.Equal(Wifi, WinnerFor("8.8.8.8").InterfaceLuid);
    }

    [Fact]
    public void Journal_persists_operations_across_instances()
    {
        long id;
        using (var journal = new OperationJournal(_dbPath, _time))
        using (var executor = new ChangeExecutor(_net, journal, _time))
        {
            id = executor.Execute(Bypass()).OperationId;
        }

        OperationJournal reopened = Journal();
        OperationRecord record = reopened.Get(id)!;
        Assert.Equal(OperationStatus.Applied, record.Status);
        Assert.Equal(1, record.AppliedSteps);
        Assert.Single(reopened.Owned());
        Assert.Equal(id, reopened.Recent(5)[0].Id);
    }
}
