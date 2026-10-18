using System.Collections.Concurrent;
using PayrollSageAgent.Core.Bindings;
using PayrollSageAgent.Core.Jobs;
using PayrollSageAgent.Core.Platform;
using PayrollSageAgent.Core.Update;
using PayrollSageAgent.Service.Bindings;

namespace PayrollSageAgent.Service;

/// <summary>
/// The service: ONE loop per active binding, started and stopped as the bindings folder changes. When the
/// service stops, it ends the loops and then tells the platform, binding by binding, that it is stopping
/// (<see cref="OfflineNotice"/>, contract step 11).
///
/// It re-reads the folder every 10 s rather than watching it: a file watcher misses events on some
/// network and container mounts, and ten seconds is nothing next to a 15 s poll. A binding added by
/// <c>add-binding</c>, removed by <c>remove-binding</c> or re-paired (a new binding id under the same
/// name) is picked up without a restart, and never disturbs the other bindings.
/// </summary>
internal sealed partial class AgentSupervisor(
    BindingStore store,
    ISecretProtector protector,
    IPlatformClient platform,
    JobRunner runner,
    PendingRequests pending,
    AgentHome home,
    ILoggerFactory loggers,
    ILogger<AgentSupervisor> logger) : BackgroundService, IBindingEvents
{
    private static readonly TimeSpan Rescan = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HealthEvery = TimeSpan.FromSeconds(30);
    private readonly Lock _health = new();
    private HealthReport? _report;
    private DateTimeOffset _healthWritten = DateTimeOffset.MinValue;
    private readonly Dictionary<string, (string BindingId, CancellationTokenSource Stop, Task Loop, RunningBinding Call)> _running = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreadable = new(StringComparer.Ordinal);

    /// <summary>Binding ids the platform revoked while this service ran: no « je m'arrête » for them.</summary>
    private readonly ConcurrentDictionary<string, byte> _revoked = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(Core.AgentProtocol.BuildVersion, Core.AgentProtocol.Version, protector.Kind);
        WriteHealth(new HealthReport(Core.AgentProtocol.BuildVersion, DateTimeOffset.UtcNow, null));
        var saidEmpty = false;
        var failing = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // [HARD] A pass that fails — the bindings folder unreadable for a moment, a file being
                // replaced — is logged once and tried again at the next one. It never ends the service:
                // the other bindings' loops are running, and a service that stops waits for a person.
                try
                {
                    // Requests from pending\ first: a binding they add is started by this same pass.
                    await pending.ProcessAsync(stoppingToken);
                    var active = store.List().Where(b => b.State == BindingStates.Active).ToList();
                    Reconcile(active, stoppingToken);

                    if (active.Count == 0 && !saidEmpty) LogNoBinding();
                    saidEmpty = active.Count == 0;
                    if (failing) LogPassRecovered();
                    failing = false;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    if (!failing) LogPassFailed(error.GetType().Name, error.Message);
                    failing = true;
                }

                await Task.Delay(Rescan, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            LogStopping();
        }
        catch (Exception error)
        {
            // Nothing above should get here. If something does, the exit code says it was a failure, so
            // Windows applies the service's recovery (restart) rather than reading a clean stop.
            LogFatal(error);
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            // Every binding this service ran says « je m'arrête », but a REVOKED one: the platform would refuse it.
            // [!] Not « the loops not yet completed »: the loops share the stopping token, so by the time this
            // block runs they have usually ended already — that filter told the platform nothing (measured in
            // Docker, 2026-10-18). A loop that crashed is still a binding the platform holds: it is told too.
            var live = _running.Values.Where(r => !_revoked.ContainsKey(r.BindingId)).Select(r => r.Call).ToList();
            foreach (var (_, run) in _running) run.Stop.Cancel();
            await Task.WhenAll(_running.Values.Select(r => r.Loop));
            // [HARD] Only now, every loop ended: a claim still in flight landing after this call would put the
            // binding back « En ligne » for a service that is gone (OfflineNotice).
            await OfflineNotice.SendAsync(platform, live, logger);
        }
    }

    private void Reconcile(IReadOnlyList<BindingConfig> active, CancellationToken stoppingToken)
    {
        // Gone, revoked or re-paired: its loop stops.
        foreach (var name in _running.Keys.ToList())
        {
            var current = active.FirstOrDefault(b => b.Name == name);
            var run = _running[name];
            if (current is null || current.BindingId != run.BindingId || run.Loop.IsCompleted)
            {
                run.Stop.Cancel();
                _running.Remove(name);
            }
        }

        foreach (var binding in active)
        {
            if (_running.ContainsKey(binding.Name)) continue;
            string token, password;
            try
            {
                token = protector.Unprotect(binding.ProtectedToken);
                password = binding.Sql.IsWindows ? "" : protector.Unprotect(binding.ProtectedSqlPassword);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (_unreadable.Add(binding.Name)) LogUnreadable(binding.Name, protector.Kind);
                continue;
            }
            _unreadable.Remove(binding.Name);

            var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var loop = new BindingLoop(binding, token, password, platform, runner, this, loggers.CreateLogger<BindingLoop>());
            _running[binding.Name] = (binding.BindingId, stop, RunGuardedAsync(binding.Name, loop, stop.Token),
                new RunningBinding(binding.Name, binding.Endpoint, token));
        }
    }

    /// <summary>A loop that crashes is logged and restarted by the next rescan — never the host.</summary>
    private async Task RunGuardedAsync(string name, BindingLoop loop, CancellationToken ct)
    {
        try
        {
            await loop.RunAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            LogLoopCrashed(name, error.GetType().Name);
        }
    }

    public Task RevokedAsync(BindingConfig binding, string reason, CancellationToken ct)
    {
        _revoked.TryAdd(binding.BindingId, 0);
        store.MarkRevoked(binding.Name, reason);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A binding reached the platform: <c>health.json</c> says so — at once the first time (the updater, after an
    /// install, waits for exactly that), then at most every 30 s, from whichever loop answered.
    /// </summary>
    public void PlatformReached()
    {
        lock (_health)
        {
            var now = DateTimeOffset.UtcNow;
            if (_report is null || (_report.PlatformReachedAt is not null && now - _healthWritten < HealthEvery)) return;
            WriteHealth(_report with { PlatformReachedAt = now });
        }
    }

    /// <summary>Never fatal: the health file serves an update, and a service that cannot write it still works.</summary>
    private void WriteHealth(HealthReport report)
    {
        lock (_health)
        {
            _report = report;
            try
            {
                report.Write(home.HealthFile);
                _healthWritten = DateTimeOffset.UtcNow;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                LogHealthUnwritten(error.GetType().Name);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent Sage {Build} démarré — protocole v{Protocol}, secrets : {Secrets}.")]
    private partial void LogStarted(string build, int protocol, string secrets);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Aucune liaison configurée : l’agent n’a rien à synchroniser. PayrollSageAgent add-binding — pour en relier une.")]
    private partial void LogNoBinding();

    [LoggerMessage(Level = LogLevel.Error, Message = "Liaison « {Name} » : ses secrets ne se lisent pas sur cette machine ({Secrets}). Retirez-la et refaites l’appairage.")]
    private partial void LogUnreadable(string name, string secrets);

    [LoggerMessage(Level = LogLevel.Error, Message = "Liaison « {Name} » : arrêt imprévu ({Kind}) ; elle repart au prochain passage.")]
    private partial void LogLoopCrashed(string name, string kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Arrêt de l’agent demandé.")]
    private partial void LogStopping();

    [LoggerMessage(Level = LogLevel.Error, Message = "Lecture des liaisons impossible ({Kind} : {Detail}) ; nouvel essai toutes les 10 secondes.")]
    private partial void LogPassFailed(string kind, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Les liaisons se lisent de nouveau.")]
    private partial void LogPassRecovered();

    [LoggerMessage(Level = LogLevel.Warning, Message = "health.json n’a pas pu être écrit ({Kind}) : une mise à jour ne pourrait pas vérifier le redémarrage.")]
    private partial void LogHealthUnwritten(string kind);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Arrêt imprévu de l’agent.")]
    private partial void LogFatal(Exception error);
}
