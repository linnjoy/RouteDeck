using System.IO.Pipes;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RouteDeck.Contracts;
using RouteDeck.Core.Changes;
using RouteDeck.Core.Journal;
using RouteDeck.Core.Models;
using RouteDeck.Service;
using RouteDeck.Tests.Fakes;
using StreamJsonRpc;

namespace RouteDeck.Tests.Service;

/// <summary>Full RPC round trip over a real named pipe, against the fake network stack.</summary>
public sealed class PipeRoundTripTests : IAsyncLifetime
{
    private const ulong Wired = 14, Mihomo = 4;
    private readonly FakeNetworkApi _net = new();
    private readonly string _pipe = $"RouteDeck-test-{Guid.NewGuid():N}";
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"routedeck-pipe-{Guid.NewGuid():N}.db");
    private readonly Events _events = new();
    private PipeServer? _server;
    private JsonRpc? _rpc;
    private IRouteDeckService _client = null!;

    public async Task InitializeAsync()
    {
        _net.AddAdapter(Wired, "以太网 2", address: "192.168.21.230/24", metric: 35, gateway: "192.168.21.1");
        _net.AddAdapter(Mihomo, "Mihomo", AdapterKind.Tunnel, address: "198.18.0.1/30", metric: 0, manager: "Mihomo/Clash");
        _net.AddExisting("0.0.0.0/0", "192.168.21.1", Wired, metric: 256);
        _net.AddExisting("0.0.0.0/1", null, Mihomo);

        await StartServerAsync(allowAnyClient: true);
    }

    private async Task StartServerAsync(bool allowAnyClient)
    {
        var core = new ServiceCore(_net, new OperationJournal(_db), NullLogger<ServiceCore>.Instance);
        _server = new PipeServer(core, new PipeServerOptions(_pipe, allowAnyClient, Path.Combine(Path.GetTempPath(), "routedeck-trusted-nowhere")), NullLogger<PipeServer>.Instance);
        await _server.StartAsync(CancellationToken.None);

        var stream = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stream.ConnectAsync(5000);
        _rpc = RouteDeckRpc.Create(stream);
        _rpc.AddLocalRpcTarget<IRouteDeckEvents>(_events, RouteDeckRpc.TargetOptions);
        _client = _rpc.Attach<IRouteDeckService>(RouteDeckRpc.ProxyOptions);
        _rpc.StartListening();
    }

    public async Task DisposeAsync()
    {
        _rpc?.Dispose();
        if (_server is not null)
        {
            await _server.StopAsync(CancellationToken.None);
            _server.Dispose();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    [Fact]
    public async Task Info_and_snapshot_round_trip()
    {
        ServiceInfo info = await _client.GetServiceInfoAsync();
        Assert.Equal(RouteDeckRpc.ProtocolVersion, info.ProtocolVersion);
        Assert.True(info.CanWrite);

        NetworkSnapshot snapshot = await _client.GetSnapshotAsync();
        Assert.Equal(2, snapshot.Adapters.Count);
        Route tun = snapshot.Routes.Single(r => r.InterfaceLuid == Mihomo);
        Assert.Equal(IpPrefix.Parse("0.0.0.0/1"), tun.Destination);
        Assert.Null(tun.NextHop);
        Assert.Equal("Mihomo/Clash", snapshot.FindAdapter(Mihomo)!.TunnelManager);
        Assert.Equal(IPAddress.Parse("192.168.21.1"), snapshot.FindAdapter(Wired)!.Gateways[0]);
    }

    [Fact]
    public async Task Bypass_execute_marks_ownership_pushes_events_and_undoes()
    {
        NetworkSnapshot snapshot = await _client.GetSnapshotAsync();
        ChangeSet bypass = BypassPlanner.Plan(snapshot, IpPrefix.Parse("10.0.1.66/32"), Wired, IPAddress.Parse("192.168.21.1"));

        ExecuteResult result = await _client.ExecuteAsync(bypass, confirmForeign: false);

        Assert.True(result.Succeeded);
        Route added = (await _client.GetSnapshotAsync()).Routes.Single(r => r.Destination == IpPrefix.Parse("10.0.1.66/32"));
        Assert.True(added.IsManual);
        Assert.Equal(RouteSource.Manual, added.Source);
        await _events.WaitForOperationAsync(result.OperationId);

        IReadOnlyList<OperationRecord> recent = await _client.GetRecentOperationsAsync(10);
        Assert.Equal(result.OperationId, recent[0].Id);
        Assert.Equal(OperationStatus.Applied, recent[0].Status);

        UndoPlan plan = await _client.PreviewUndoAsync(result.OperationId);
        Assert.Equal(UndoItemState.WillApply, Assert.Single(plan.Items).State);
        Assert.True((await _client.ApplyUndoAsync(result.OperationId)).Succeeded);
        Assert.DoesNotContain((await _client.GetSnapshotAsync()).Routes, r => r.Destination == IpPrefix.Parse("10.0.1.66/32"));
    }

    [Fact]
    public async Task Removing_a_foreign_route_needs_explicit_confirmation()
    {
        Route tun = (await _client.GetSnapshotAsync()).Routes.Single(r => r.InterfaceLuid == Mihomo);

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => _client.ExecuteAsync(ChangeSets.Remove(tun), confirmForeign: false));
        Assert.Contains("不是 RouteDeck 创建的路由", ex.Message);
        Assert.Contains(_net.Routes, r => r.InterfaceLuid == Mihomo);

        Assert.True((await _client.ExecuteAsync(ChangeSets.Remove(tun), confirmForeign: true)).Succeeded);
    }

    [Fact]
    public async Task Clients_cannot_spoof_reserved_origins()
    {
        NetworkSnapshot snapshot = await _client.GetSnapshotAsync();
        ChangeSet bypass = BypassPlanner.Plan(snapshot, IpPrefix.Parse("10.0.1.66/32"), Wired) with { Origin = "recover" };

        ExecuteResult result = await _client.ExecuteAsync(bypass, false);

        Assert.Equal(ChangeSet.ManualOrigin, (await _client.GetRecentOperationsAsync(1))[0].ChangeSet.Origin);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Risky_change_reports_pending_and_can_be_reverted_over_the_pipe()
    {
        _net.Routes.RemoveAll(r => r.InterfaceLuid == Mihomo);
        Route wired = (await _client.GetSnapshotAsync()).Routes.Single();

        ExecuteResult result = await _client.ExecuteAsync(ChangeSets.Remove(wired), confirmForeign: true);

        Assert.NotNull(result.Pending);
        Assert.Equal(result.OperationId, (await _client.GetPendingChangeAsync())!.OperationId);
        string message = await _client.RevertPendingChangeAsync(result.OperationId);
        Assert.Contains("已恢复", message);
        Assert.Single(_net.Routes);
        Assert.Null(await _client.GetPendingChangeAsync());
    }

    [Fact]
    public async Task Several_clients_can_connect_one_after_another()
    {
        for (int i = 0; i < 3; i++)
        {
            await using var stream = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await stream.ConnectAsync(5000);
            using JsonRpc rpc = RouteDeckRpc.Create(stream);
            IRouteDeckService client = rpc.Attach<IRouteDeckService>(RouteDeckRpc.ProxyOptions);
            rpc.StartListening();
            Assert.Equal(RouteDeckRpc.ProtocolVersion, (await client.GetServiceInfoAsync()).ProtocolVersion);
        }
        Assert.True((await _client.GetSnapshotAsync()).Routes.Count > 0);
    }

    [Fact]
    public async Task Untrusted_client_is_read_only()
    {
        // The trusted directory is somewhere testhost.exe is not.
        _rpc!.Dispose();
        await _server!.StopAsync(CancellationToken.None);
        _server.Dispose();
        await StartServerAsync(allowAnyClient: false);

        ServiceInfo info = await _client.GetServiceInfoAsync();
        Assert.False(info.CanWrite);
        Assert.NotEmpty((await _client.GetSnapshotAsync()).Routes);

        NetworkSnapshot snapshot = await _client.GetSnapshotAsync();
        ChangeSet bypass = BypassPlanner.Plan(snapshot, IpPrefix.Parse("10.0.1.66/32"), Wired);
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => _client.ExecuteAsync(bypass, false));
        Assert.Contains("安装目录", ex.Message);
        Assert.DoesNotContain(_net.Routes, r => r.Destination == IpPrefix.Parse("10.0.1.66/32"));
    }

    private sealed class Events : IRouteDeckEvents
    {
        private readonly List<long> _operations = [];
        private readonly SemaphoreSlim _signal = new(0);

        public void OnStateChanged()
        {
        }

        public void OnOperation(long operationId, string message)
        {
            lock (_operations) _operations.Add(operationId);
            _signal.Release();
        }

        public void OnPendingCountdown(PendingChange? pending, int secondsLeft)
        {
        }

        public async Task WaitForOperationAsync(long id)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (_operations)
                {
                    if (_operations.Contains(id)) return;
                }
                await _signal.WaitAsync(TimeSpan.FromMilliseconds(200));
            }
            Assert.Fail($"没有收到操作 #{id} 的推送");
        }
    }
}
