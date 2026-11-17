using RouteDeck.Contracts;
using RouteDeck.Core.Abstractions;
using RouteDeck.Core.Changes;
using RouteDeck.Core.Journal;
using RouteDeck.Core.Models;

namespace RouteDeck.Service;

/// <summary>Process-wide state shared by every pipe connection.</summary>
internal sealed class ServiceCore : IDisposable
{
    private readonly ILogger<ServiceCore> _logger;
    private readonly List<IRouteDeckEvents> _clients = [];
    private readonly object _clientsLock = new();
    private IDisposable? _subscription;
    private Timer? _countdown;

    public ServiceCore(INetworkApi network, OperationJournal journal, ILogger<ServiceCore> logger, TimeProvider? time = null)
    {
        Network = network;
        Journal = journal;
        _logger = logger;
        Executor = new ChangeExecutor(network, journal, time);
        Executor.Changed += OnExecutorChanged;
    }

    public INetworkApi Network { get; }

    public OperationJournal Journal { get; }

    public ChangeExecutor Executor { get; }

    public IReadOnlyList<string> StartupMessages { get; private set; } = [];

    /// <summary>Crash recovery first, then start watching the network.</summary>
    public void Start()
    {
        StartupMessages = Executor.RecoverOnStartup();
        foreach (string message in StartupMessages) _logger.LogWarning("{Message}", message);
        _subscription = Network.Subscribe(() => Broadcast(c => c.OnStateChanged()));
        _countdown = new Timer(_ => TickCountdown(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public NetworkSnapshot AnnotatedSnapshot()
    {
        NetworkSnapshot snapshot = Network.GetSnapshot();
        var owned = Executor.Owned();
        return snapshot with
        {
            Routes = snapshot.Routes.Select(r => owned.ContainsKey(r.Key) ? r with { IsManual = true } : r).ToList(),
        };
    }

    public void AddClient(IRouteDeckEvents client)
    {
        lock (_clientsLock) _clients.Add(client);
    }

    public void RemoveClient(IRouteDeckEvents client)
    {
        lock (_clientsLock) _clients.Remove(client);
    }

    public void Dispose()
    {
        _countdown?.Dispose();
        _subscription?.Dispose();
        Executor.Dispose();
        Journal.Dispose();
    }

    private void OnExecutorChanged(long id, string message)
    {
        _logger.LogInformation("操作 #{Id}：{Message}", id, message);
        Broadcast(c => c.OnOperation(id, message));
        Broadcast(c => c.OnStateChanged());
    }

    private void TickCountdown()
    {
        PendingChange? pending = Executor.Pending;
        if (pending is null) return;
        int seconds = Math.Max(0, (int)Math.Ceiling((pending.Deadline - DateTimeOffset.UtcNow).TotalSeconds));
        Broadcast(c => c.OnPendingCountdown(pending, seconds));
    }

    private void Broadcast(Action<IRouteDeckEvents> send)
    {
        IRouteDeckEvents[] clients;
        lock (_clientsLock) clients = _clients.ToArray();
        foreach (IRouteDeckEvents client in clients)
        {
            try
            {
                send(client);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or StreamJsonRpc.ConnectionLostException)
            {
                RemoveClient(client);
            }
        }
    }
}
