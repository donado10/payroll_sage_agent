using System.Text;
using PayrollSageAgent.Core;
using PayrollSageAgent.Core.Logging;
using PayrollSageAgent.Core.Platform;
using PayrollSageAgent.Sage;
using PayrollSageAgent.Service;
using PayrollSageAgent.Service.Bindings;
using PayrollSageAgent.Service.Cli;
using PayrollSageAgent.Service.Secrets;

// The French of every line — « », ’, é — survives a Windows console only in UTF-8: its default code
// page has no ’ and prints « ? » in its place.
if (OperatingSystem.IsWindows()) Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// A verb (add-binding, check-sql, remove-binding, list-bindings, help — and purge, the MSI's uninstall).
if (AgentCli.Handles(args))
{
    var home = AgentServices.Home();
    using var http = AgentServices.Http();
    // What the verb prints goes to the log file as well — when the agent's folder exists (the installer
    // made it, or an earlier add-binding): a log must never be what creates it, with the wrong rights.
    using var fileLog = Directory.Exists(home.Root) ? new FileLoggerProvider(home.Logs, TimeProvider.System) : null;
    var cliLog = fileLog?.CreateLogger("AgentCli");
    using var tee = cliLog is null ? null : new LoggingWriter(Console.Out, cliLog);
    if (cliLog is not null) CliLog.Command(cliLog, AgentCli.Describe(args));
    try
    {
        var cli = new AgentCli(new BindingStore(home), SecretProtectors.ForThisMachine(home),
            new PlatformClient(http, AgentProtocol.BuildVersion), new SageChartReader(), home, (TextWriter?)tee ?? Console.Out, Console.In);
        var exit = await cli.RunAsync(args, CancellationToken.None);
        if (cliLog is not null) CliLog.Exit(cliLog, exit);
        return exit;
    }
    catch (PlatformNotSupportedException error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}

// No verb: the service. A no-op AddWindowsService off Windows is what lets the same exe run in Docker.
var agentHome = AgentServices.Home();
agentHome.Ensure();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = ServiceIdentity.Name);

// [HARD] Not the default providers. On Windows they include the Event Log, which needs an event SOURCE
// registered by an administrator; the service runs as its own low-privilege account, which cannot
// register one, and a provider that throws takes the log line's caller down with it. The log is the
// files of logs\ (what IT sends us) and the console (Docker, or the exe run by hand).
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddProvider(new FileLoggerProvider(agentHome.Logs, TimeProvider.System));
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

builder.Services.AddAgent();
builder.Services.AddHostedService<AgentSupervisor>();
await builder.Build().RunAsync();
return Environment.ExitCode;

internal static partial class CliLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Commande : PayrollSageAgent {Command}")]
    public static partial void Command(ILogger logger, string command);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fin de la commande, code de sortie {Exit}.")]
    public static partial void Exit(ILogger logger, int exit);
}
