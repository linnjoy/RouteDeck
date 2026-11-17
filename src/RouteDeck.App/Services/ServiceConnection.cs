using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using CommunityToolkit.Mvvm.ComponentModel;
using RouteDeck.Contracts;
using RouteDeck.Core.Abstractions;
using RouteDeck.Core.Changes;
using RouteDeck.Core.Native;
using RouteDeck.Core.Models;
using StreamJsonRpc;

namespace RouteDeck.App.Services;

/// <summary>
/// Talks to the RouteDeck service and falls back to an in-process, read-only view of the
/// network when the service is not running. Reconnects every few seconds.
/// </summary>
public sealed partial class ServiceConnection : ObservableObject, IRouteDeckEvents, IDisposable
{
    private readonly WindowsNetworkApi _local = new();
    private readonly CancellationTokenSource _stop = new();
    private JsonRpc? _rpc;
    private IDisposable? _localSubscription;

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    private bool _canWrite;

    [ObservableProperty]
    private string _statusText = "正在连接服务…";

    public IRouteDeckService? Service { get; private set; }

    /// <summary>Read-only OS access for things that need no elevation (best route, neighbor probes).</summary>
    public INetworkApi LocalNetwork => _local;

    public IReadOnlyList<string> StartupMessages { get; private set; } = [];

    public event Action? StateChanged;

    public event Action<long, string>? OperationReported;

    public event Action<PendingChange?, int>? PendingCountdown;

    public void Start()
    {
        _localSubscription = _local.Subscribe(() => { if (!IsOnline) StateChanged?.Invoke(); });
        _ = Task.Run(() => ConnectLoopAsync(_stop.Token));
    }

    public async Task<NetworkSnapshot> GetSnapshotAsync()
    {
        IRouteDeckService? service = Service;
        if (service is not null)
        {
            try
            {
                return await service.GetSnapshotAsync();
            }
            catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException or IOException)
            {
                // Fall through to the local view; the connect loop will notice.
            }
        }
        return await Task.Run(_local.GetSnapshot);
    }

    /// <summary>Starts the Windows service via an elevated sc.exe (shows a UAC prompt).</summary>
    public static bool TryStartService()
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("sc.exe", "start RouteDeck")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return process is not null;
        }
        catch (Win32Exception)
        {
            return false; // User declined the UAC prompt.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _localSubscription?.Dispose();
        _rpc?.Dispose();
    }

    void IRouteDeckEvents.OnStateChanged() => StateChanged?.Invoke();

    void IRouteDeckEvents.OnOperation(long operationId, string message) => OperationReported?.Invoke(operationId, message);

    void IRouteDeckEvents.OnPendingCountdown(PendingChange? pending, int secondsLeft) => PendingCountdown?.Invoke(pending, secondsLeft);

    private async Task ConnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeClientStream(".", RouteDeckRpc.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(1000, cancellationToken);
                JsonRpc rpc = RouteDeckRpc.Create(pipe);
                rpc.AddLocalRpcTarget<IRouteDeckEvents>(this, RouteDeckRpc.TargetOptions);
                IRouteDeckService service = rpc.Attach<IRouteDeckService>(RouteDeckRpc.ProxyOptions);
                rpc.StartListening();

                ServiceInfo info = await service.GetServiceInfoAsync();
                if (info.ProtocolVersion != RouteDeckRpc.ProtocolVersion)
                {
                    rpc.Dispose();
                    SetState(false, false, $"服务版本不匹配（协议 {info.ProtocolVersion}），只读模式");
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                    continue;
                }

                _rpc = rpc;
                Service = service;
                StartupMessages = info.StartupMessages;
                SetState(true, info.CanWrite, info.CanWrite ? "服务已连接" : $"服务已连接（只读：{info.ReadOnlyReason}）");
                StateChanged?.Invoke();
                if (info.StartupMessages.Count > 0)
                {
                    foreach (string message in info.StartupMessages) OperationReported?.Invoke(0, message);
                }

                await rpc.Completion.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or ConnectionLostException or RemoteInvocationException or UnauthorizedAccessException)
            {
                // Service not running or went away.
            }

            Service = null;
            _rpc?.Dispose();
            _rpc = null;
            SetState(false, false, "服务未运行：只读模式，不能修改路由");
            StateChanged?.Invoke();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void SetState(bool online, bool canWrite, string status)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            IsOnline = online;
            CanWrite = canWrite;
            StatusText = status;
        });
    }
}
