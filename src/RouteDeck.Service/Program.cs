using RouteDeck.Contracts;
using RouteDeck.Core.Abstractions;
using RouteDeck.Core.Journal;
using RouteDeck.Core.Native;
using RouteDeck.Service;

if (args.Contains("--install")) return ServiceInstaller.Install();
if (args.Contains("--uninstall")) return ServiceInstaller.Uninstall();

bool console = args.Contains("--console");
if (console) Console.OutputEncoding = System.Text.Encoding.UTF8;
// Dev only: lets a GUI built elsewhere write. Never honored when running as a service.
bool allowAnyClient = console && args.Contains("--allow-any-client");
string dataDir = GetOption(args, "--data-dir")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RouteDeck");
string pipeName = GetOption(args, "--pipe") ?? RouteDeckRpc.PipeName;

Directory.CreateDirectory(dataDir);
DataDirectory.Secure(dataDir);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = ServiceInstaller.ServiceName);
builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss ");
builder.Logging.AddProvider(new DailyFileLoggerProvider(Path.Combine(dataDir, "logs")));
if (!console) builder.Logging.AddEventLog(o => o.SourceName = ServiceInstaller.ServiceName);

builder.Services.AddSingleton<INetworkApi, WindowsNetworkApi>();
builder.Services.AddSingleton(_ => new OperationJournal(Path.Combine(dataDir, "history.db")));
builder.Services.AddSingleton<ServiceCore>();
builder.Services.AddSingleton(new PipeServerOptions(pipeName, allowAnyClient));
builder.Services.AddHostedService<PipeServer>();

builder.Build().Run();
return 0;

static string? GetOption(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
