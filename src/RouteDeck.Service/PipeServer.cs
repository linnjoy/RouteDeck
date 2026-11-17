using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using RouteDeck.Contracts;
using StreamJsonRpc;

namespace RouteDeck.Service;

/// <param name="TrustedDirectory">Clients whose executable lives here may write; defaults to the service's own directory.</param>
internal sealed record PipeServerOptions(string PipeName, bool AllowAnyClient, string? TrustedDirectory = null);

/// <summary>Accepts GUI connections on \\.\pipe\RouteDeck. The ACL admits SYSTEM, Administrators and interactive users.</summary>
internal sealed partial class PipeServer(ServiceCore core, PipeServerOptions options, ILogger<PipeServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        core.Start();
        logger.LogInformation("RouteDeck 服务已启动，管道 {Pipe}", options.PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe(options.PipeName);
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // An undisposed instance would keep accepting clients that nobody serves.
                if (pipe is not null) await pipe.DisposeAsync();
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (pipe is not null) await pipe.DisposeAsync();
                // The service owns crash recovery and confirmation timeouts: never let a pipe error stop it.
                logger.LogError(ex, "创建管道失败，5 秒后重试");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
                continue;
            }
            _ = Task.Run(() => ServeAsync(pipe, stoppingToken), CancellationToken.None);
        }
    }

    public override void Dispose()
    {
        core.Dispose();
        base.Dispose();
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe)
        {
            (bool canWrite, string? reason, string client) = Authorize(pipe);
            logger.LogInformation("客户端已连接：{Client}，可写：{CanWrite}", client, canWrite);

            using JsonRpc rpc = RouteDeckRpc.Create(pipe);
            rpc.AddLocalRpcTarget<IRouteDeckService>(new RouteDeckService(core, canWrite, reason), RouteDeckRpc.TargetOptions);
            IRouteDeckEvents events = rpc.Attach<IRouteDeckEvents>(RouteDeckRpc.ProxyOptions);
            rpc.StartListening();
            core.AddClient(events);
            try
            {
                await rpc.Completion.WaitAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ConnectionLostException or IOException)
            {
                // Client went away or the service is stopping.
            }
            finally
            {
                core.RemoveClient(events);
                logger.LogInformation("客户端已断开：{Client}", client);
            }
        }
    }

    /// <summary>Write access only for the RouteDeck GUI shipped next to this service (or anyone in --allow-any-client dev mode).</summary>
    private (bool CanWrite, string? Reason, string Client) Authorize(NamedPipeServerStream pipe)
    {
        string client = "未知进程";
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid))
                return (options.AllowAnyClient, "无法识别客户端进程", client);

            using Process process = Process.GetProcessById((int)pid);
            string path = process.MainModule?.FileName ?? "";
            client = $"{process.ProcessName} ({pid}) {path}";
            if (options.AllowAnyClient) return (true, null, client);

            string serviceDir = Path.GetFullPath(options.TrustedDirectory ?? AppContext.BaseDirectory);
            bool trusted = path.StartsWith(serviceDir, StringComparison.OrdinalIgnoreCase);
            return trusted
                ? (true, null, client)
                : (false, "只有安装目录中的 RouteDeck 可以修改网络。", client);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "识别客户端失败");
            return (options.AllowAnyClient, "无法识别客户端进程", client);
        }
    }

    private static NamedPipeServerStream CreatePipe(string name)
    {
        var security = new PipeSecurity();
        // The account running the service must be able to create further instances of its own pipe.
        using (WindowsIdentity self = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            name,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
}
