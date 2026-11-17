using System.Diagnostics;

namespace RouteDeck.Service;

/// <summary>Registers the service with sc.exe. Requires an elevated prompt.</summary>
internal static class ServiceInstaller
{
    public const string ServiceName = "RouteDeck";

    public static int Install()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径");
        // Re-install over an existing registration (upgrade): remove the old one first.
        if (Sc($"query {ServiceName}") == 0) Uninstall();
        int code = Sc($"create {ServiceName} binPath= \"\\\"{exe}\\\"\" start= auto obj= LocalSystem DisplayName= \"RouteDeck 路由管理服务\"");
        if (code != 0) return code;
        Sc($"description {ServiceName} \"RouteDeck 后台服务：执行路由修改、记录操作日志并在需要时自动恢复。\"");
        Sc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000");
        return Sc($"start {ServiceName}");
    }

    public static int Uninstall()
    {
        Sc($"stop {ServiceName}");
        return Sc($"delete {ServiceName}");
    }

    private static int Sc(string arguments)
    {
        using Process process = Process.Start(new ProcessStartInfo("sc.exe", arguments) { UseShellExecute = false })!;
        process.WaitForExit();
        return process.ExitCode;
    }
}
