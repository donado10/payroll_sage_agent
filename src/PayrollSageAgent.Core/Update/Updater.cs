using System.Globalization;
using System.Security.Cryptography;
using PayrollSageAgent.Core.Bindings;
using PayrollSageAgent.Core.Contract;
using PayrollSageAgent.Core.Platform;

namespace PayrollSageAgent.Core.Update;

/// <summary>A binding the updater checks through, with its token in clear (unprotected for this run only).</summary>
public sealed record UpdateBinding(BindingConfig Binding, string Token);

/// <summary>
/// The agent's self-update — what <c>PayrollSageAgent update</c> runs, every hour from the scheduled task (as
/// SYSTEM) or at once from <c>Mettre-a-jour-agent.bat</c> (platform/protocol.md › « Updates »):
///
///   1. CHECK, through every active binding (each says the agent's settings and its last attempt; the
///      platform records them on that binding) — the newest version any platform offers wins;
///   2. DECIDE (<see cref="Decide"/>): outdated → now; automatic and inside the window → now; a version that
///      failed less than 20 hours ago → not yet; asked by a person → now;
///   3. DOWNLOAD into the update folder and VERIFY the size and the SHA-256 the manifest gave — and, when this
///      agent is SIGNED, an Authenticode signature of the same publisher;
///   4. INSTALL with Windows Installer — the MSI upgrades in place, and Windows Installer itself rolls a failed
///      install back to the version before (RemoveExistingProducts after InstallInitialize);
///   5. WAIT up to 5 minutes for the new service to start AND reach the platform (<see cref="HealthReport"/>);
///      when it does not, ROLL BACK: the new version removed (its bindings kept), the one before reinstalled
///      from <c>previous.msi</c>;
///   6. REPORT the attempt at once, through every binding.
///
/// Exit codes, the verbs': 0 nothing to do or done · 1 installed and rolled back, or refused by Windows
/// Installer · 4 refused (the platform, the file, the signature) · 5 no platform reached.
/// Every OS step goes through <see cref="IUpdateMachine"/>; waiting through <paramref name="delay"/>.
/// </summary>
public sealed class Updater(
    IPlatformClient platform,
    IUpdateMachine machine,
    UpdatePaths paths,
    TimeProvider clock,
    Func<string, Task> say,
    Func<HealthReport?> health,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromHours(20);
    public static readonly TimeSpan HealthPoll = TimeSpan.FromSeconds(5);
    public const int HealthPolls = 60; // 5 minutes

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public enum Plan
    {
        NoRelease,
        UpToDate,
        RebootPending,
        RecentlyFailed,
        Disabled,
        OutsideWindow,
        Install,
    }

    /// <summary>Whether to install now, and the sentence that says why — pure, for the tests and the log.</summary>
    public static (Plan Plan, string Why) Decide(string current, UpdateResponse manifest, UpdateSettings settings, TimeOnly serverTime,
        DateTimeOffset now, bool forced, UpdateAttempt? last)
    {
        if (manifest.Latest is not { } latest) return (Plan.NoRelease, "Aucune version de l’agent n’est publiée sur la plateforme.");
        var v = latest.Version;
        if (!AgentVersion.IsOlder(current, v)) return (Plan.UpToDate, $"À jour : version {current} (la plus récente publiée : {v}).");
        if (last is { Outcome: ContractNames.UpdateRebootPending } pending && AgentVersion.Same(pending.Version, v))
        {
            return (Plan.RebootPending, $"La version {v} est installée ; elle prendra effet au redémarrage de ce serveur.");
        }
        if (!forced && last is { Outcome: ContractNames.UpdateFailed or ContractNames.UpdateRolledBack } failed
            && AgentVersion.Same(failed.Version, v) && now - failed.At < RetryFailedAfter)
        {
            var again = (failed.At + RetryFailedAfter).ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
            return (Plan.RecentlyFailed, $"La version {v} a échoué à sa dernière tentative : nouvel essai après le {again} (ou Mettre-a-jour-agent.bat).");
        }
        if (forced) return (Plan.Install, $"Mise à jour demandée : {current} → {v}.");
        if (manifest.Outdated)
        {
            var minimum = manifest.MinimumVersion ?? v;
            return settings.Auto
                ? (Plan.Install, $"La plateforme n’accepte plus la version {current} (minimum {minimum}) : mise à jour sans attendre la plage horaire.")
                : (Plan.Disabled, $"La plateforme n’accepte plus la version {current} (minimum {minimum}), et les mises à jour automatiques sont désactivées : lancez Mettre-a-jour-agent.bat.");
        }
        if (!settings.Auto) return (Plan.Disabled, $"Version {v} disponible ; mises à jour automatiques désactivées — Mettre-a-jour-agent.bat pour l’installer.");
        return settings.Window.Contains(serverTime)
            ? (Plan.Install, $"Version {v} disponible, dans la plage {settings.Window} : installation.")
            : (Plan.OutsideWindow, $"Version {v} disponible : elle s’installera {settings.Window.Sentence} (heure de ce serveur).");
    }

    public async Task<int> RunAsync(string current, IReadOnlyList<UpdateBinding> bindings, UpdateSettings settings, bool forced, CancellationToken ct)
    {
        var state = UpdateState.Read(paths.State);
        var request = new UpdateRequest(settings.Auto, settings.Window.ToString(), state.Last);

        UpdateBinding? source = null;
        UpdateResponse? manifest = null;
        var outdated = false;
        foreach (var binding in bindings)
        {
            var answer = await platform.CheckUpdateAsync(binding.Binding.Endpoint, binding.Token, request, ct);
            if (answer.IsOk && answer.Value is { } offered)
            {
                outdated |= offered.Outdated;
                if (manifest is null || Newer(offered.Latest, manifest.Latest))
                {
                    manifest = offered;
                    source = binding;
                }
                continue;
            }
            await say(answer.Refusal is { } refusal
                ? $"Liaison « {binding.Binding.Name} » : la plateforme refuse la vérification ({refusal.Code})."
                : $"Liaison « {binding.Binding.Name} » : plateforme injoignable ({answer.Transient}).");
        }
        if (manifest is null || source is null)
        {
            await say("Aucune plateforme n’a répondu : pas de mise à jour cette fois.");
            return 5;
        }
        manifest = manifest with { Outdated = outdated };
        state = state with { CheckedAt = clock.GetUtcNow(), Available = manifest.Latest?.Version };
        state.Write(paths.State);

        var (plan, why) = Decide(current, manifest, settings, TimeOnly.FromDateTime(clock.GetLocalNow().DateTime), clock.GetUtcNow(), forced, state.Last);
        await say(why);
        if (plan != Plan.Install) return 0;

        var exit = await InstallAsync(current, manifest.Latest!, source, bindings.Count > 0, ct);

        // The attempt, told now rather than at the next hour — through every binding, best effort.
        var attempt = UpdateState.Read(paths.State).Last;
        if (attempt is not null && attempt != state.Last)
        {
            var report = request with { Last = attempt };
            foreach (var binding in bindings) await platform.CheckUpdateAsync(binding.Binding.Endpoint, binding.Token, report, ct);
        }
        return exit;
    }

    private async Task<int> InstallAsync(string current, AgentRelease target, UpdateBinding source, bool needsPlatform, CancellationToken ct)
    {
        var v = target.Version;
        Directory.CreateDirectory(paths.Folder);
        var file = paths.Download(v);
        var part = file + ".part";

        await say($"Téléchargement de la version {v} ({(target.Bytes / 1e6).ToString("0.0", CultureInfo.InvariantCulture)} Mo)…");
        PlatformResult<long> got;
        await using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            got = await platform.DownloadUpdateAsync(source.Binding.Endpoint, source.Token, v, stream, ct);
        }
        if (!got.IsOk)
        {
            File.Delete(part);
            await say(got.Refusal is { } refusal
                ? $"Téléchargement refusé par la plateforme ({refusal.Code}) : {refusal.Message}"
                : $"Téléchargement impossible ({got.Transient}) : nouvel essai à la prochaine vérification.");
            return got.Refusal is null ? 5 : 4;
        }

        var (length, sha256) = Measure(part);
        if (length != target.Bytes || !string.Equals(sha256, target.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            await say("Le fichier reçu n’est pas celui que la plateforme annonce (taille ou SHA-256) : rien n’est installé. Nouvel essai à la prochaine vérification.");
            return 4;
        }
        File.Move(part, file, overwrite: true);
        await say($"Fichier vérifié : {length} octets, SHA-256 {sha256[..16]}…");

        // A SIGNED agent installs only what its own publisher signed; an unsigned one has the SHA-256 alone —
        // over HTTP, exactly as trustworthy as the channel (platform/protocol.md › « Transport »).
        if (machine.OwnSigner() is { } signer)
        {
            var (valid, by) = machine.Signature(file);
            if (!target.Signed || !valid || !string.Equals(by, signer, StringComparison.Ordinal))
            {
                File.Delete(file);
                Record(v, ContractNames.UpdateFailed, $"La version {v} n’est pas signée par l’éditeur de cet agent : elle n’a pas été installée.");
                await say($"Refusé : la version {v} n’est pas signée par « {signer} ». Rien n’est installé.");
                return 4;
            }
        }

        if (machine.CannotInstall is { } reason)
        {
            await say($"Version {v} téléchargée et vérifiée ({file}) ; {reason}");
            return 0;
        }

        if (File.Exists(paths.Installed)) File.Copy(paths.Installed, paths.Previous, overwrite: true);
        var since = clock.GetUtcNow();
        await say($"Installation de la version {v} (Windows Installer, journal : {paths.Log($"install-{v}")})…");
        var code = await machine.InstallAsync(file, paths.Log($"install-{v}"), ct);
        switch (code)
        {
            case 0:
                break;
            case 3010 or 1641:
                Record(v, ContractNames.UpdateRebootPending, null);
                await say($"Version {v} installée ; elle prendra effet au redémarrage de ce serveur.");
                return 0;
            case 1618:
                await say("Une autre installation Windows est en cours : nouvel essai à la prochaine vérification.");
                return 0;
            default:
                // Windows Installer put the version before back (its own rollback); make sure its service runs.
                await machine.StartServiceAsync(ct);
                Record(v, ContractNames.UpdateFailed, $"Windows Installer a refusé la version {v} (code {code}) ; l’agent est resté en {current}.");
                await say($"Windows Installer a refusé la version {v} (code {code}) ; l’agent reste en {current}. Le détail : {paths.Log($"install-{v}")}.");
                return 1;
        }

        await say("Installée. L’agent redémarre : attente de sa reconnexion à la plateforme (5 minutes au plus)…");
        if (await HealthyAsync(v, since, needsPlatform, ct))
        {
            Record(v, ContractNames.UpdateUpdated, null);
            File.Delete(file);
            await say($"Mise à jour faite : version {v}.");
            return 0;
        }

        if (!File.Exists(paths.Previous))
        {
            Record(v, ContractNames.UpdateFailed, $"La version {v} ne s’est pas reconnectée à la plateforme, et aucune version d’avant n’est gardée sur ce serveur : réinstallez le MSI.");
            await say($"La version {v} ne s’est pas reconnectée, et aucune version d’avant n’est gardée pour y revenir : réinstallez le MSI.");
            return 1;
        }
        await say($"La version {v} ne s’est pas reconnectée dans les 5 minutes : retour à la version {current}…");
        var removed = await machine.UninstallAsync(file, paths.Log($"retrait-{v}"), ct);
        var restored = await machine.InstallAsync(paths.Previous, paths.Log($"retour-{current}"), ct);
        if (restored is 0 or 3010 or 1641)
        {
            Record(v, ContractNames.UpdateRolledBack, $"La version {v} ne s’est pas reconnectée à la plateforme dans les 5 minutes : l’agent est revenu à la {current}.");
            await say($"Revenu à la version {current}. La {v} sera retentée dans 20 heures (ou Mettre-a-jour-agent.bat).");
            return 1;
        }
        Record(v, ContractNames.UpdateFailed, $"La version {v} ne s’est pas reconnectée, et le retour à la {current} a échoué (codes {removed} et {restored}) : réinstallez le MSI.");
        await say($"Le retour à la version {current} a échoué (retrait : code {removed}, réinstallation : code {restored}) : réinstallez le MSI à la main.");
        return 1;
    }

    /// <summary>The new service is running, says it is the new version, started after the install — and has reached the platform.</summary>
    private async Task<bool> HealthyAsync(string version, DateTimeOffset since, bool needsPlatform, CancellationToken ct)
    {
        for (var poll = 0; poll < HealthPolls; poll++)
        {
            await _delay(HealthPoll, ct);
            if (machine.ServiceRunning() && health() is { } report && report.ShowsHealthy(version, since, needsPlatform)) return true;
        }
        return false;
    }

    private void Record(string version, string outcome, string? message)
    {
        var text = message?.Trim();
        if (text is { Length: > ContractNames.MaxUpdateMessage }) text = text[..(ContractNames.MaxUpdateMessage - 1)].TrimEnd() + "…";
        var state = UpdateState.Read(paths.State);
        (state with { Last = new UpdateAttempt(version, outcome, clock.GetUtcNow(), string.IsNullOrEmpty(text) ? null : text) }).Write(paths.State);
    }

    private static bool Newer(AgentRelease? candidate, AgentRelease? current) =>
        candidate is not null && (current is null || AgentVersion.IsOlder(current.Version, candidate.Version));

    private static (long Length, string Sha256) Measure(string file)
    {
        using var stream = File.OpenRead(file);
        var hash = SHA256.HashData(stream);
        return (stream.Length, Convert.ToHexStringLower(hash));
    }
}
