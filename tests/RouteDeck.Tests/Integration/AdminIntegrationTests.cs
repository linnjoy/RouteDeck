using System.Net;
using System.Security.Principal;
using RouteDeck.Core.Changes;
using RouteDeck.Core.Journal;
using RouteDeck.Core.Models;
using RouteDeck.Core.Native;

namespace RouteDeck.Tests.Integration;

/// <summary>Runs only in an elevated process; skipped otherwise so normal test runs never touch the network.</summary>
public sealed class AdminFactAttribute : FactAttribute
{
    public AdminFactAttribute()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            Skip = "需要管理员权限：在管理员终端中运行 dotnet test --filter Category=Admin";
    }
}

/// <summary>
/// Real route writes, confined to TEST-NET prefixes (198.51.100.0/24, 2001:db8::/32) on the
/// loopback interface. Never touches physical adapters or default routes. Cleans up before and after.
/// </summary>
[Trait("Category", "Admin")]
public sealed class AdminIntegrationTests : IDisposable
{
    private static readonly IpPrefix TestNetV4 = IpPrefix.Parse("198.51.100.0/24");
    private static readonly IpPrefix TestNetV6 = IpPrefix.Parse("2001:db8::/32");
    private readonly WindowsNetworkApi _api = new();
    private readonly ulong _loopback;
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"routedeck-admin-{Guid.NewGuid():N}.db");

    public AdminIntegrationTests()
    {
        _loopback = _api.GetSnapshot().Adapters.Single(a => a.Kind == AdapterKind.Loopback).Luid;
        CleanUp();
    }

    public void Dispose()
    {
        CleanUp();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    private void CleanUp()
    {
        foreach (Route route in _api.GetSnapshot().Routes.Where(IsTestRoute))
        {
            try
            {
                _api.RemoveRoute(route.ToSpec());
            }
            catch (Core.Abstractions.NetworkApiException)
            {
                // Best effort; the assertion in the test reports leftovers.
            }
        }
    }

    private static bool IsTestRoute(Route r) =>
        (r.Destination.Family == TestNetV4.Family && r.Destination.Length >= 24 && TestNetV4.Contains(r.Destination.Address)) ||
        (r.Destination.Family == TestNetV6.Family && r.Destination.Length >= 32 && TestNetV6.Contains(r.Destination.Address));

    private RouteSpec Spec(IpPrefix prefix, int metric = 5, bool persistent = false) => new(prefix, null, _loopback, metric, persistent);

    [AdminFact]
    public void Active_route_add_metric_remove_ipv4()
    {
        RouteSpec spec = Spec(TestNetV4);
        _api.AddRoute(spec);

        Route added = _api.GetSnapshot().FindRoute(spec.Key)!;
        Assert.Equal(5, added.Metric);
        Assert.False(added.InPersistentStore);
        Assert.Equal(spec.Key, _api.GetBestRoute(IPAddress.Parse("198.51.100.10")));

        _api.SetRouteMetric(spec, 9);
        Assert.Equal(9, _api.GetSnapshot().FindRoute(spec.Key)!.Metric);

        _api.RemoveRoute(spec);
        Assert.Null(_api.GetSnapshot().FindRoute(spec.Key));
    }

    [AdminFact]
    public void Active_route_ipv6()
    {
        RouteSpec spec = Spec(TestNetV6);
        _api.AddRoute(spec);
        Assert.NotNull(_api.GetSnapshot().FindRoute(spec.Key));
        _api.RemoveRoute(spec);
        Assert.Null(_api.GetSnapshot().FindRoute(spec.Key));
    }

    [AdminFact]
    public void Persistent_route_round_trip()
    {
        RouteSpec spec = Spec(IpPrefix.Parse("198.51.100.128/25"), persistent: true);
        _api.AddRoute(spec);

        Route added = _api.GetSnapshot().FindRoute(spec.Key)!;
        Assert.True(added.InPersistentStore);
        Assert.True(added.InActiveStore);

        _api.SetRouteMetric(spec, 7);
        Assert.Equal(7, _api.GetSnapshot().FindRoute(spec.Key)!.Metric);

        _api.RemoveRoute(spec with { Metric = 7 });
        Assert.Null(_api.GetSnapshot().FindRoute(spec.Key));
    }

    [AdminFact]
    public void Duplicate_add_reports_chinese_error()
    {
        RouteSpec spec = Spec(TestNetV4);
        _api.AddRoute(spec);
        var ex = Assert.Throws<Core.Abstractions.NetworkApiException>(() => _api.AddRoute(spec));
        Assert.Equal(IpHelper.ERROR_OBJECT_ALREADY_EXISTS, ex.ErrorCode);
        Assert.Contains("路由已存在", ex.Message);
    }

    [AdminFact]
    public void Executor_applies_journals_and_undoes_on_real_stack()
    {
        using var journal = new OperationJournal(_db);
        using var executor = new ChangeExecutor(_api, journal);
        var change = new ChangeSet(ChangeSet.ManualOrigin, "integration", [RouteOperation.Add(Spec(TestNetV4))]);

        ExecuteResult result = executor.Execute(change);
        Assert.True(result.Succeeded);
        Assert.Null(result.Pending);
        Assert.True(result.Verify!.Ok);

        Assert.True(executor.ApplyUndo(result.OperationId).Succeeded);
        Assert.DoesNotContain(_api.GetSnapshot().Routes, IsTestRoute);
    }

    [AdminFact]
    public async Task Change_notifications_fire()
    {
        var fired = new TaskCompletionSource();
        using IDisposable subscription = _api.Subscribe(() => fired.TrySetResult());
        _api.AddRoute(Spec(TestNetV4));

        Task winner = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(fired.Task, winner);
    }
}
