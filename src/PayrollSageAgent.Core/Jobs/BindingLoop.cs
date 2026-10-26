using Microsoft.Extensions.Logging;
using PayrollSageAgent.Core.Bindings;
using PayrollSageAgent.Core.Contract;
using PayrollSageAgent.Core.Platform;

namespace PayrollSageAgent.Core.Jobs;

/// <summary>What the loop tells its host: the platform no longer knows this binding — or answered it.</summary>
public interface IBindingEvents
{
    Task RevokedAsync(BindingConfig binding, string reason, CancellationToken ct);

    /// <summary>A call answered: what the updater waits for after an install (<c>health.json</c>).</summary>
    void PlatformReached()
    {
    }
}

/// <summary>
/// ONE binding's life: claim, run, answer — again, every <c>pollSeconds</c>.
///
/// [HARD] One loop per binding, and a loop never touches another's state: a dead database, a revoked
/// token or a crashing job on one binding leaves the others polling. Its log lines carry the binding's
/// LOCAL name and counts — never a token, a password, a connection string or a row of Sage.
///
/// Waiting goes through <paramref name="delay"/>, so the tests run the loop without real time.
/// </summary>
public sealed partial class BindingLoop(
    BindingConfig binding,
    string token,
    string sqlPassword,
    IPlatformClient platform,
    JobRunner runner,
    IBindingEvents events,
    ILogger logger,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TooOldRetry = TimeSpan.FromHours(1);

    /// <summary>
    /// `agent_outdated`: the platform refuses this BUILD until it is updated — the updater installs the latest
    /// at its next run. The loop keeps calling every 5 minutes, so the screen still sees the agent.
    /// </summary>
    private static readonly TimeSpan OutdatedRetry = TimeSpan.FromMinutes(5);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task RunAsync(CancellationToken ct)
    {
        var poll = TimeSpan.FromSeconds(15);
        var backoff = FirstBackoff;
        var saidOutdated = false;
        LogStarted(binding.Name, binding.ConnectionName);

        while (!ct.IsCancellationRequested)
        {
            var wait = poll;
            var claim = await platform.ClaimAsync(binding.Endpoint, token, ct);

            if (claim.IsOk && claim.Value is { } answer)
            {
                events.PlatformReached();
                saidOutdated = false;
                backoff = FirstBackoff;
                poll = TimeSpan.FromSeconds(Math.Clamp(answer.PollSeconds, 1, 3600));
                wait = poll;
                if (answer.Job is { } job)
                {
                    var stop = await HandleJobAsync(job, ct);
                    if (stop) return;
                }
            }
            else if (claim.IsRefused(ContractNames.TokenInvalid))
            {
                LogRevoked(binding.Name);
                await events.RevokedAsync(binding, ContractNames.TokenInvalid, ct);
                return;
            }
            else if (claim.IsRefused(ContractNames.ProtocolUnsupported))
            {
                LogTooOld(binding.Name, AgentProtocol.Version, claim.Refusal?.MinimumSupported, claim.Refusal?.Protocol);
                wait = TooOldRetry;
            }
            else if (claim.IsRefused(ContractNames.AgentOutdated))
            {
                events.PlatformReached();
                if (!saidOutdated) LogOutdated(binding.Name, AgentProtocol.BuildVersion, claim.Refusal?.MinimumVersion ?? "?");
                saidOutdated = true;
                wait = OutdatedRetry;
            }
            else if (claim.Refusal is { } refusal)
            {
                LogRefused(binding.Name, refusal.Code, claim.Status);
                wait = refusal.RetryAfterSeconds is int seconds ? TimeSpan.FromSeconds(seconds) : poll;
            }
            else
            {
                LogUnreachable(binding.Name, claim.Transient ?? "?", (int)backoff.TotalSeconds);
                wait = Jitter(backoff);
                backoff = backoff * 2 < MaxBackoff ? backoff * 2 : MaxBackoff;
            }

            try
            {
                await _delay(wait, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Runs one job and delivers its outcome. True when the binding must stop.</summary>
    private async Task<bool> HandleJobAsync(AgentJob job, CancellationToken ct)
    {
        LogJob(binding.Name, job.Type, job.Attempt);
        JobOutcome outcome;
        try
        {
            outcome = await runner.RunAsync(job, binding.Sql, sqlPassword, binding.AccountFilter, binding.PartnerFilter, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A bug in the agent: said to the platform in the contract's words, the detail kept here.
            LogJobCrashed(binding.Name, error.GetType().Name, error);
            outcome = new JobOutcome(null, new FailRequest(job.Id, job.Attempt,
                new JobError(ContractNames.Internal, "L’agent a rencontré une erreur imprévue ; le détail est dans son journal."), true));
        }

        // SQL Server's own words stay HERE: in the log file IT sends us, never in what the platform receives.
        if (outcome.LocalDetail is { } detail) LogLocalDetail(binding.Name, outcome.Fail?.Error.Code ?? "", detail);

        // A delivery that fails on the network is tried again within the lease; past it, the platform
        // hands the job out again and the next claim runs it anew.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (outcome.Complete is { } complete)
            {
                var sent = await platform.CompleteAsync(binding.Endpoint, token, complete, ct);
                if (sent.IsOk && sent.Value is { } done)
                {
                    var r = done.Report;
                    var partners = complete.Result.Type == ContractNames.SyncPartnerAccounts;
                    if (partners) LogSyncedPartners(binding.Name, complete.Result.Accounts.Count, r.Created, r.Updated, r.Skipped.Count);
                    else LogSynced(binding.Name, complete.Result.Accounts.Count, r.Created, r.Updated, r.Skipped.Count);
                    if (complete.Result.AccountFilter is { } filter)
                    {
                        LogFiltered(binding.Name, partners ? "partnerFilter" : "accountFilter", AccountFilter.Describe(filter), r.Removed ?? 0, r.KeptInUse?.Count ?? 0);
                    }
                    return false;
                }
                if (sent.IsRefused(ContractNames.TokenInvalid)) return await RevokedAsync(ct);
                if (sent.Refusal is { } refusal)
                {
                    LogDeliveryRefused(binding.Name, refusal.Code);
                    return false;
                }
            }
            else if (outcome.Fail is { } fail)
            {
                var sent = await platform.FailAsync(binding.Endpoint, token, fail, ct);
                if (sent.IsOk)
                {
                    LogFailedReported(binding.Name, fail.Error.Code, fail.Retryable ? "un nouvel essai est possible" : "sans nouvel essai");
                    return false;
                }
                if (sent.IsRefused(ContractNames.TokenInvalid)) return await RevokedAsync(ct);
                if (sent.Refusal is { } refusal)
                {
                    LogDeliveryRefused(binding.Name, refusal.Code);
                    return false;
                }
            }
            else
            {
                return false;
            }

            await _delay(TimeSpan.FromSeconds(5 * attempt), ct);
        }
        LogDeliveryGaveUp(binding.Name);
        return false;
    }

    private async Task<bool> RevokedAsync(CancellationToken ct)
    {
        LogRevoked(binding.Name);
        await events.RevokedAsync(binding, ContractNames.TokenInvalid, ct);
        return true;
    }

    private static TimeSpan Jitter(TimeSpan wait) =>
        wait + TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)Math.Min(wait.TotalMilliseconds / 4, 30_000) + 1));

    [LoggerMessage(Level = LogLevel.Information, Message = "Liaison « {Name} » : à l’écoute de la plateforme pour « {Connection} ».")]
    private partial void LogStarted(string name, string connection);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Liaison « {Name} » : révoquée ou inconnue de la plateforme. Elle est arrêtée ; refaites l’appairage pour la relier.")]
    private partial void LogRevoked(string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "Liaison « {Name} » : cet agent parle le protocole {Mine}, la plateforme sert {Min} à {Max}. Mettez l’agent à jour ; nouvel essai dans une heure.")]
    private partial void LogTooOld(string name, int mine, int? min, int? max);

    [LoggerMessage(Level = LogLevel.Error, Message = "Liaison « {Name} » : la plateforme n’accepte plus la version {Mine} de l’agent (minimum {Minimum}). La mise à jour s’installe à la prochaine vérification (toutes les heures) ; nouvel appel dans 5 minutes.")]
    private partial void LogOutdated(string name, string mine, string minimum);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Liaison « {Name} » : la plateforme refuse ({Code}, {Status}).")]
    private partial void LogRefused(string name, string code, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Liaison « {Name} » : plateforme injoignable ({Why}). Nouvel essai dans {Seconds} s environ.")]
    private partial void LogUnreachable(string name, string why, int seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Liaison « {Name} » : travail {Type}, tentative {Attempt}.")]
    private partial void LogJob(string name, string type, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Liaison « {Name} » : erreur imprévue pendant le travail ({Kind}).")]
    private partial void LogJobCrashed(string name, string kind, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Liaison « {Name} » : {Code} — détail de SQL Server : {Detail}")]
    private partial void LogLocalDetail(string name, string code, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Liaison « {Name} » : plan comptable envoyé — {Sent} comptes ; {Created} créés, {Updated} mis à jour, {Skipped} ignorés par la plateforme.")]
    private partial void LogSynced(string name, int sent, int created, int updated, int skipped);

    [LoggerMessage(Level = LogLevel.Information, Message = "Liaison « {Name} » : comptes tiers envoyés — {Sent} tiers ; {Created} créés, {Updated} mis à jour, {Skipped} ignorés par la plateforme.")]
    private partial void LogSyncedPartners(string name, int sent, int created, int updated, int skipped);

    [LoggerMessage(Level = LogLevel.Information, Message = "Liaison « {Name} » : filtre {Key} « {Filter} » — hors filtre, {Removed} supprimés sur la plateforme, {Kept} gardés car employés.")]
    private partial void LogFiltered(string name, string key, string filter, int removed, int kept);

    // Une phrase, pas un booléen : « réessayable : True » au milieu d'une ligne en français.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Liaison « {Name} » : échec signalé à la plateforme ({Code}) — {Retry}.")]
    private partial void LogFailedReported(string name, string code, string retry);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Liaison « {Name} » : la plateforme n’a pas accepté le résultat ({Code}).")]
    private partial void LogDeliveryRefused(string name, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Liaison « {Name} » : le résultat n’a pas pu être remis ; la plateforme redonnera le travail.")]
    private partial void LogDeliveryGaveUp(string name);
}
