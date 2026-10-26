using System.Text;
using PayrollSageAgent.Core;
using PayrollSageAgent.Core.Bindings;
using PayrollSageAgent.Core.Contract;
using PayrollSageAgent.Core.Platform;
using PayrollSageAgent.Core.Sync;
using PayrollSageAgent.Core.Update;
using PayrollSageAgent.Service.Bindings;
using PayrollSageAgent.Service.Update;

namespace PayrollSageAgent.Service.Cli;

/// <summary>
/// The agent's verbs — what the IT person types on the server.
///
///   PayrollSageAgent add-binding --name &lt;nom&gt; --url &lt;adresse&gt; --code XXXX-XXXX-XXXX
///                                --sql-server &lt;serveur&gt; --database &lt;base&gt; (--sql-user &lt;compte&gt; | --windows-auth)
///                                [--trust-server-certificate] [--sql-password-stdin]
///   PayrollSageAgent add-binding --config &lt;fichier.json&gt; [--sql-password-stdin]
///   PayrollSageAgent check-sql   --sql-server … --database … (--sql-user … | --windows-auth) [--trust-server-certificate]
///   PayrollSageAgent check-sql   --name &lt;nom&gt;  |  --config &lt;fichier.json&gt;
///   PayrollSageAgent remove-binding --name &lt;nom&gt;
///   PayrollSageAgent list-bindings
///   PayrollSageAgent update [--now]       (from the runner copy: the scheduled task, Mettre-a-jour-agent.bat)
///
/// [HARD] The SQL password is never a command-line argument (it would sit in the process list and the
/// shell's history): it is typed at a hidden prompt, read from standard input, or — the caller's choice —
/// written in a config file the SERVICE deletes once read. Nothing printed here carries a secret.
///
/// [HARD] Under Windows authentication the check must run as the SERVICE's identity, not the
/// administrator's: the verb then writes its request into <c>pending\</c>, the service carries it out
/// (<see cref="PendingRequests"/>), and the verb prints the service's answer.
/// </summary>
public sealed class AgentCli(BindingStore store, ISecretProtector protector, IPlatformClient platform, ISageChartReader reader, AgentHome home, TextWriter output, TextReader input)
{
    public static readonly string[] Verbs = ["add-binding", "remove-binding", "check-sql", "list-bindings", "apply-config", "set-label", "configure-service", "init-config", "update", "purge", "help", "--help", "-h"];

    private readonly BindingPairing _pairing = new(store, protector, platform, reader);
    private readonly ServiceHandOff _handOff = new(home);

    /// <summary>The command as the log records it: every argument, the pairing code masked.</summary>
    public static string Describe(string[] args)
    {
        var shown = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            shown.Add(args[i]);
            if (args[i] == "--code" && i + 1 < args.Length)
            {
                shown.Add("****-****-****");
                i++;
            }
        }
        return string.Join(' ', shown.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));
    }

    public static bool Handles(string[] args) => args.Length > 0 && Verbs.Contains(args[0], StringComparer.Ordinal);

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var verb = args[0];
        try
        {
            var options = Options(args.Skip(1).ToArray());
            return verb switch
            {
                "add-binding" => await AddAsync(options, ct),
                "remove-binding" => await RemoveAsync(options, ct),
                "check-sql" => await CheckSqlAsync(options, ct),
                "list-bindings" => List(),
                "set-label" => await SetLabelAsync(options),
                "configure-service" => await ConfigureServiceAsync(),
                "apply-config" => await ApplyConfigAsync(options, ct),
                "init-config" => await InitConfigAsync(options, ct),
                "update" => await UpdateAsync(options, ct),
                "purge" => await PurgeAsync(options, ct),
                _ => Help(),
            };
        }
        catch (UsageException error)
        {
            await output.WriteLineAsync(error.Message);
            await output.WriteLineAsync("PayrollSageAgent help — pour l’aide.");
            return 2;
        }
        catch (UnauthorizedAccessException)
        {
            // The agent's folder admits SYSTEM, the Administrators and the service only (AgentHome).
            await output.WriteLineAsync($"Accès refusé au dossier de l’agent ({store.Root}). Lancez la commande dans une console ouverte « en tant qu’administrateur ».");
            return 6;
        }
    }

    private Task Say(string line) => output.WriteLineAsync(line);

    private async Task<int> AddAsync(Dictionary<string, string?> o, CancellationToken ct)
    {
        IReadOnlyList<PendingRequest> requests;
        if (o.ContainsKey("config"))
        {
            Only(o, "config", "sql-password-stdin");
            requests = FromConfig(Required(o, "config"));
            if (requests[0].Action != PendingRequest.AddBinding) throw new UsageException("Ce fichier demande « check-sql » : PayrollSageAgent check-sql --config …");
            await WarnPasswordInFileAsync(requests);
        }
        else
        {
            var name = Required(o, "name");
            if (!BindingRules.IsValidName(name)) throw new UsageException($"--name : {BindingRules.NameRule}.");
            var url = BindingRules.Address(Required(o, "url")) ?? throw new UsageException($"--url : {BindingRules.UrlRule}.");
            var code = BindingRules.Code(Required(o, "code")) ?? throw new UsageException($"--code : {BindingRules.CodeRule}.");
            requests = [new PendingRequest(PendingRequest.AddBinding, name, url, code, Target(o), null)];
        }

        return await EachAsync(requests, async request =>
        {
            if (request.Sql.IsWindows)
            {
                // The access is checked AS THE SERVICE; the pairing itself needs no particular identity.
                var access = await _handOff.CheckAsync(request.Sql, Say, ct);
                return access != 0 ? access : await _pairing.AddAsync(request.Name, request.Url!, request.Code!, request.Sql, "", Say, ct, accessChecked: true, accountFilter: request.AccountFilter, partnerFilter: request.PartnerFilter);
            }
            var password = request.Password ?? await ReadPasswordAsync(o.ContainsKey("sql-password-stdin"), request, requests.Count > 1);
            return await _pairing.AddAsync(request.Name, request.Url!, request.Code!, request.Sql, password, Say, ct, accountFilter: request.AccountFilter, partnerFilter: request.PartnerFilter);
        });
    }

    /// <summary>
    /// Several bindings from one file: each on its own, under its name — one refused does not stop the
    /// others. The exit code is the first failure's, or 0 when all succeeded.
    /// </summary>
    private async Task<int> EachAsync(IReadOnlyList<PendingRequest> requests, Func<PendingRequest, Task<int>> run)
    {
        if (requests.Count == 1) return await run(requests[0]);
        var outcomes = new List<int>();
        foreach (var request in requests)
        {
            await Say($"— {request.Name} —");
            outcomes.Add(await run(request));
        }
        await Say($"{outcomes.Count(e => e == 0)} liaison(s) sur {outcomes.Count} faite(s).");
        return outcomes.FirstOrDefault(e => e != 0);
    }

    /// <summary>
    /// The SQL check of <c>add-binding</c>, ALONE: no pairing code, nothing written — what IT runs while
    /// finding the right server name, port or protocol, without a code expiring between tries. With
    /// <c>--name</c>, it checks the login a binding already holds, with its stored password.
    /// </summary>
    private async Task<int> CheckSqlAsync(Dictionary<string, string?> o, CancellationToken ct)
    {
        if (o.ContainsKey("config"))
        {
            Only(o, "config", "sql-password-stdin");
            var requests = FromConfig(Required(o, "config"));
            await WarnPasswordInFileAsync(requests);
            return await EachAsync(requests, request => CheckOneAsync(request.Sql, request.Password, request, requests.Count > 1, o, ct));
        }

        SqlTarget target;
        string? password = null;
        if (o.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name))
        {
            Only(o, "name");
            var binding = store.Get(name.Trim()) ?? throw new UsageException($"Aucune liaison « {name} » sur cet agent. PayrollSageAgent list-bindings — pour les voir.");
            target = binding.Sql;
            if (!target.IsWindows) password = protector.Unprotect(binding.ProtectedSqlPassword);
        }
        else
        {
            target = Target(o);
        }
        return await CheckOneAsync(target, password, null, false, o, ct);
    }

    private async Task<int> CheckOneAsync(SqlTarget target, string? password, PendingRequest? from, bool many, Dictionary<string, string?> o, CancellationToken ct)
    {
        if (target.IsWindows) return await _handOff.CheckAsync(target, Say, ct);
        if (target.TrustServerCertificate)
        {
            await Say("[!] Le certificat du serveur SQL ne sera pas vérifié (trust-server-certificate). Réservé à un serveur de test.");
        }
        password ??= await ReadPasswordAsync(o.ContainsKey("sql-password-stdin"), from, many);
        return await _pairing.CheckAsync(target, password, Say, ct);
    }

    /// <summary>
    /// <c>agent.json</c> applied — what <c>Redemarrer-agent.bat</c> runs, as administrator: the file's bindings
    /// made real (<see cref="ConfigApplier"/>), its secrets taken out, <c>agent.result.txt</c> written beside
    /// it, and with <c>--restart</c> the service started first if it was not (Windows authentication needs it)
    /// and restarted after, so it rereads what changed.
    /// </summary>
    private async Task<int> ApplyConfigAsync(Dictionary<string, string?> o, CancellationToken ct)
    {
        Only(o, "restart");
        var restart = o.ContainsKey("restart");
        if (restart) await ServiceAsync(stopFirst: false);
        var (exit, needsRestart) = await ApplyAndReportAsync(home.ConfigReport, ct);
        if (restart || needsRestart) await ServiceAsync(stopFirst: true);
        return exit;
    }

    /// <summary>
    /// What the MSI runs, as SYSTEM, once the service is started. An <c>agent.json</c> placed NEXT TO the MSI
    /// becomes the agent's config and is applied at once (its answer written there too, then the file next to
    /// the MSI deleted: codes, passwords); otherwise a first install gets an empty one to fill. An existing
    /// config is kept. Never fails the install.
    /// </summary>
    private async Task<int> InitConfigAsync(Dictionary<string, string?> o, CancellationToken ct)
    {
        Only(o, "msi");
        var config = home.ConfigFile;
        var package = o.TryGetValue("msi", out var msi) && !string.IsNullOrWhiteSpace(msi) ? Path.GetFullPath(msi) : null;
        var folder = package is null ? null : Path.GetDirectoryName(package);
        var seed = folder is null ? null : Path.Combine(folder, AgentConfigFile.FileName);
        // An MSI run from the agent's own folder would take agent.json for a config placed next to it — and
        // delete it. The updater runs its MSI from update\, where none is ever placed; this is the guard.
        if (seed is not null && SamePath(seed, config)) seed = null;
        try
        {
            KeepInstalledMsi(package);
            if (seed is not null && File.Exists(seed))
            {
                File.Copy(seed, config, overwrite: true);
                AgentHome.RestrictToAdministrators(config);
                File.Delete(seed);
                await Say($"{AgentConfigFile.FileName} posé à côté du MSI : devenu la configuration de l’agent ({config}).");
                var (_, needsRestart) = await ApplyAndReportAsync(Path.Combine(folder!, AgentConfigFile.ReportName), ct);
                File.Copy(Path.Combine(folder!, AgentConfigFile.ReportName), home.ConfigReport, overwrite: true);
                if (needsRestart) await ServiceAsync(stopFirst: true);
            }
            else if (!File.Exists(config))
            {
                await File.WriteAllTextAsync(config, AgentConfigFile.Template.ReplaceLineEndings("\r\n") + "\r\n", ct);
                AgentHome.RestrictToAdministrators(config);
                await Say($"{config} créé, vide : Configurer-agent.bat pour le remplir, puis Redemarrer-agent.bat.");
            }
            else
            {
                AgentHome.RestrictToAdministrators(config);
                await Say($"{config} existant : gardé tel quel.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await Say($"La configuration n’a pas pu être préparée ({error.GetType().Name} : {error.Message}).");
        }
        return 0;
    }

    /// <summary>
    /// The MSI just installed, kept in the update folder (<c>installed.msi</c>): the version the updater rolls back
    /// TO if the next one fails. Windows Installer's own cache keeps the package without its files, so it cannot
    /// reinstall from it. An MSI read where SYSTEM cannot (a network share) is said, never fatal.
    /// </summary>
    private void KeepInstalledMsi(string? package)
    {
        if (package is null || !File.Exists(package)) return;
        var kept = home.Update.Installed;
        if (SamePath(package, kept)) return;
        try
        {
            Directory.CreateDirectory(home.Update.Folder);
            File.Copy(package, kept, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Le MSI n’a pas pu être gardé pour un retour en arrière ({error.GetType().Name}) : une mise à jour ratée ne pourra pas revenir à cette version.");
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// The agent's self-update (<see cref="Updater"/>) — from the RUNNER copy only: the scheduled task
    /// (<c>--scheduled</c>, every hour, as SYSTEM) and <c>Mettre-a-jour-agent.bat</c> (<c>--now</c>, a person's
    /// gesture: no window, no 20-hour wait). One at a time on the server (<c>update.lock</c>).
    /// </summary>
    private async Task<int> UpdateAsync(Dictionary<string, string?> o, CancellationToken ct)
    {
        Only(o, "now", "scheduled");
        var forced = o.ContainsKey("now");
        IUpdateMachine machine = OperatingSystem.IsWindows() ? new WindowsUpdateMachine() : new DevelopmentUpdateMachine();
        // [HARD] Never from the installed exe: the install replaces it, and a running exe cannot be replaced.
        if (OperatingSystem.IsWindows() && machine.CannotInstall is null
            && SamePath(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), AgentHome.ProgramFolder))
        {
            await Say("La mise à jour se lance avec Mettre-a-jour-agent.bat (ou la tâche planifiée), pas depuis l’exe installé : l’installation doit pouvoir le remplacer.");
            return 2;
        }

        var paths = home.Update;
        Directory.CreateDirectory(paths.Folder);
        FileStream gate;
        try
        {
            gate = new FileStream(paths.Lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            await Say("Une mise à jour est déjà en cours sur ce serveur.");
            return 0;
        }
        await using (gate)
        {
            if (OperatingSystem.IsWindows()) UpdateTask.RemoveOldRunners(home);
            var settings = UpdateSettings.Read(paths.Settings);
            var bindings = new List<UpdateBinding>();
            foreach (var binding in store.List().Where(b => b.State == BindingStates.Active))
            {
                try
                {
                    bindings.Add(new UpdateBinding(binding, protector.Unprotect(binding.ProtectedToken)));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    await Say($"Liaison « {binding.Name} » : ses secrets ne se lisent pas ici ({error.GetType().Name}) — ignorée.");
                }
            }
            if (bindings.Count == 0)
            {
                await Say("Aucune liaison active : l’agent n’a pas de plateforme à interroger pour ses mises à jour.");
                return 0;
            }
            await Say(settings.Sentence);
            using var http = AgentServices.DownloadHttp();
            var updater = new Updater(new PlatformClient(http, AgentProtocol.BuildVersion), machine, paths, TimeProvider.System, Say,
                () => HealthReport.Read(home.HealthFile));
            return await updater.RunAsync(machine.InstalledVersion() ?? AgentProtocol.BuildVersion, bindings, settings, forced, ct);
        }
    }

    /// <summary>Applies agent.json, says every line, and writes them to <paramref name="report"/> (UTF-8 with a BOM, for Notepad).</summary>
    private async Task<(int Exit, bool NeedsRestart)> ApplyAndReportAsync(string report, CancellationToken ct)
    {
        var lines = new List<string>();
        async Task Collect(string line)
        {
            lines.Add(line);
            await Say(line);
        }
        var applier = new ConfigApplier(store, protector, platform, _pairing, _handOff, home.Update.Settings);
        var outcome = await applier.ApplyAsync(home.ConfigFile, Collect, ct);
        var text = new List<string>
        {
            $"IGF Paie — Agent Sage {AgentProtocol.BuildVersion} — {DateTimeOffset.Now:dd/MM/yyyy HH:mm}",
            outcome.Exit == 0 ? "agent.json appliqué : tout est en ordre." : "agent.json appliqué en partie : voir ci-dessous.",
            "",
        };
        text.AddRange(lines);
        await File.WriteAllTextAsync(report, string.Join("\r\n", text) + "\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);
        return (outcome.Exit, outcome.RestartNeeded);
    }

    /// <summary>Starts the service if it is not running; with <paramref name="stopFirst"/>, restarts it. Windows only.</summary>
    private async Task ServiceAsync(bool stopFirst)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var service = new System.ServiceProcess.ServiceController(ServiceIdentity.Name);
            if (stopFirst && service.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            }
            if (service.Status != System.ServiceProcess.ServiceControllerStatus.Running)
            {
                service.Start();
                service.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            }
            if (stopFirst) await Say("Agent redémarré.");
        }
        catch (Exception error) when (error is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            await Say($"Le service n’a pas pu être (re)démarré : {error.Message}");
        }
    }

    private async Task<int> RemoveAsync(Dictionary<string, string?> o, CancellationToken ct)
    {
        var name = Required(o, "name");
        var binding = store.Get(name) ?? throw new UsageException($"Aucune liaison « {name} » sur cet agent. PayrollSageAgent list-bindings — pour les voir.");
        await UnpairAsync(binding, ct);
        store.Delete(name);
        await Say($"Liaison « {name} » retirée de cet agent, avec ses secrets.");
        return 0;
    }

    /// <summary>Best effort: the platform revokes the token; without it the binding still goes from HERE.</summary>
    private async Task UnpairAsync(BindingConfig binding, CancellationToken ct)
    {
        if (binding.State != BindingStates.Active) return;
        var unpaired = await platform.UnpairAsync(binding.Endpoint, protector.Unprotect(binding.ProtectedToken), ct);
        await Say(unpaired.IsOk || unpaired.IsRefused(ContractNames.TokenInvalid)
            ? $"Liaison « {binding.Name} » : la plateforme a délié cet agent."
            : $"Liaison « {binding.Name} » : la plateforme n’a pas pu être prévenue ({unpaired.Transient ?? unpaired.Refusal?.Code}) — révoquez la liaison depuis son écran.");
    }

    /// <summary>
    /// What the MSI runs when the agent is REALLY uninstalled (never on an upgrade): every binding unpaired and
    /// deleted with its secrets, every request left in <c>pending\</c> deleted. The logs stay. It never
    /// fails — an uninstall must not stop because the platform is unreachable.
    /// </summary>
    private async Task<int> PurgeAsync(Dictionary<string, string?> o, CancellationToken ct)
    {
        if (!o.ContainsKey("confirm")) throw new UsageException("purge efface toutes les liaisons et leurs secrets : il demande --confirm. (Le désinstalleur l’appelle ; inutile de le lancer à la main.)");
        foreach (var binding in store.List())
        {
            try
            {
                await UnpairAsync(binding, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await Say($"Liaison « {binding.Name} » : la plateforme n’a pas pu être prévenue ({error.GetType().Name}).");
            }
            store.Delete(binding.Name);
            await Say($"Liaison « {binding.Name} » effacée, avec ses secrets.");
        }
        if (Directory.Exists(home.Pending))
        {
            foreach (var file in Directory.EnumerateFiles(home.Pending)) File.Delete(file);
        }
        // agent.json and its report: not the MSI's files, so without this the program folder could not go —
        // nor without the update folder (its runner copies, the MSIs kept), nor should the task outlive the agent.
        foreach (var file in new[] { home.ConfigFile, home.ConfigReport, home.HealthFile }) if (File.Exists(file)) File.Delete(file);
        if (OperatingSystem.IsWindows()) await UpdateTask.RemoveAsync(home, Say);
        await Say("Agent purgé : liaisons, secrets, demandes, agent.json et mises à jour effacés ; les journaux restent.");
        return 0;
    }

    /// <summary>
    /// The name services.msc shows — « IGF Paie - Agent Sage » by default. One agent serves every SQL Server
    /// instance of its server (each binding names its own), so the label is for telling servers or roles
    /// apart, not instances. The SERVICE NAME stays <c>PayrollSageAgent</c>: its Windows identity, its
    /// folder's rights and the Windows-auth logins all hang on it. Saved where the MSI reads it back
    /// (<c>HKLM\SOFTWARE\IGF\PayrollSageAgent</c> › <c>Label</c>), so an upgrade keeps it.
    /// </summary>
    private async Task<int> SetLabelAsync(Dictionary<string, string?> o)
    {
        Only(o, "label");
        var label = Required(o, "label");
        if (label.Length > 256 || label.Any(char.IsControl)) throw new UsageException("--label : 256 caractères au plus, sans retour à la ligne.");
        if (!OperatingSystem.IsWindows())
        {
            await Say("set-label : sur Windows seulement — c’est le nom que services.msc affiche.");
            return 2;
        }

        var (exit, said) = await ScAsync("config", ServiceIdentity.Name, "DisplayName=", label);
        if (exit == 5) throw new UnauthorizedAccessException();
        if (exit != 0)
        {
            await Say($"Le libellé n’a pas pu être changé (sc.exe, code {exit}) : {said}");
            return exit == 1060 ? 2 : 1;
        }

        using (var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                   .CreateSubKey(@"SOFTWARE\IGF\PayrollSageAgent"))
        {
            key.SetValue("Label", label);
        }
        await Say($"Libellé du service : « {label} » — services.msc l’affiche après un rafraîchissement (F5). Les mises à jour le gardent.");
        return 0;
    }

    /// <summary>
    /// What the MSI runs, as SYSTEM, right after it creates the service: delayed start, the unrestricted
    /// service SID, restart after 1 min, 1 min, 5 min, and « a stop with an error counts as a failure » — and
    /// the updater's scheduled task, on a runner copy of this version (<see cref="UpdateTask"/>).
    /// [!] The MSI first did this with Windows Installer's own tables (MsiServiceConfig,
    /// MsiServiceConfigFailureActions, applied by MsiConfigureServices): on the user's Windows Server 2025
    /// that step failed with error 1939 and rolled the install back (2026-10-17). These are instead the
    /// `sc.exe` commands `install.ps1` ran successfully on that same server. A refusal is said and logged,
    /// and never fails the install: the service runs without them.
    /// </summary>
    private async Task<int> ConfigureServiceAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            await Say("configure-service : sur Windows seulement.");
            return 0;
        }
        string[][] steps =
        [
            ["config", ServiceIdentity.Name, "start=", "delayed-auto"],
            ["sidtype", ServiceIdentity.Name, "unrestricted"],
            ["failure", ServiceIdentity.Name, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/300000"],
            ["failureflag", ServiceIdentity.Name, "1"],
        ];
        foreach (var step in steps)
        {
            var (exit, said) = await ScAsync(step);
            await Say(exit == 0
                ? $"Service : sc.exe {step[0]} — fait."
                : $"Service : sc.exe {string.Join(' ', step)} a échoué (code {exit}) : {said}");
        }
        // The updater: its runner copy of THIS version, and the hourly task on it (UpdateTask).
        await UpdateTask.InstallAsync(home, Say);
        return 0;
    }

    /// <summary>sc.exe with its arguments passed one by one (no quoting to get wrong), and what it said, on one line.</summary>
    private static async Task<(int Exit, string Said)> ScAsync(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("sc.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var said = (await process.StandardOutput.ReadToEndAsync()) + (await process.StandardError.ReadToEndAsync());
        await process.WaitForExitAsync();
        return (process.ExitCode, string.Join(' ', said.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim());
    }

    private int List()
    {
        var bindings = store.List();
        if (bindings.Count == 0)
        {
            output.WriteLine("Aucune liaison sur cet agent.");
            return 0;
        }
        foreach (var b in bindings)
        {
            var state = b.State == BindingStates.Active ? "active" : $"révoquée ({b.StateReason})";
            var who = b.Sql.IsWindows ? "authentification Windows" : $"compte {b.Sql.User}";
            var filter = (b.AccountFilter.Count > 0 ? $" · comptes {AccountFilter.Describe(b.AccountFilter)}" : "")
                + (b.PartnerFilter.Count > 0 ? $" · tiers {AccountFilter.Describe(b.PartnerFilter)}" : "");
            output.WriteLine($"{b.Name} · {state} · base {b.Sql.Database} sur {b.Sql.Server}, {who}{filter} · {b.CompanyName} — {b.ConnectionName} · {b.Endpoint}");
        }
        return 0;
    }

    private int Help()
    {
        output.WriteLine($"Agent Sage — IGF Paie {AgentProtocol.BuildVersion} (protocole {AgentProtocol.Version})");
        output.WriteLine();
        output.WriteLine("  add-binding     relier une base Sage à un ERP de la plateforme, avec le code d’appairage");
        output.WriteLine("                  --name --url --code --sql-server --database (--sql-user | --windows-auth)");
        output.WriteLine("                  [--trust-server-certificate] [--sql-password-stdin]");
        output.WriteLine("                  — ou --config <fichier.json> (le mot de passe absent du fichier est demandé)");
        output.WriteLine("  check-sql       vérifier un accès SQL, sans code ni liaison");
        output.WriteLine("                  --sql-server --database (--sql-user | --windows-auth) — ou --name, ou --config");
        output.WriteLine("  remove-binding  --name — délier une base, et effacer ses secrets");
        output.WriteLine("  list-bindings   les liaisons de cet agent");
        output.WriteLine($"  apply-config    appliquer {home.ConfigFile} [--restart] — ce que fait Redemarrer-agent.bat");
        output.WriteLine("  set-label       --label « … » — le nom que services.msc affiche (Windows, administrateur)");
        output.WriteLine("  update          vérifier et installer la dernière version [--now : sans attendre la plage horaire]");
        output.WriteLine("                  — lancé par la tâche planifiée et par Mettre-a-jour-agent.bat, depuis update\\runner-…");
        output.WriteLine();
        output.WriteLine($"Sans verbe : le service lui-même. Une demande en JSON déposée dans {home.Pending} est traitée par le service.");
        output.WriteLine();
        output.WriteLine("Codes de sortie : 0 fait · 2 commande incomplète · 3 accès SQL refusé · 4 refusé par la plateforme");
        output.WriteLine("                  · 5 plateforme injoignable · 6 lancer en administrateur · 7 le service ne répond pas");
        return 0;
    }

    /// <summary>The SQL target the options describe: a login, or the service's Windows identity.</summary>
    private static SqlTarget Target(Dictionary<string, string?> o)
    {
        var server = Required(o, "sql-server");
        var database = Required(o, "database");
        var trust = o.ContainsKey("trust-server-certificate");
        if (o.ContainsKey("windows-auth"))
        {
            if (o.ContainsKey("sql-user") || o.ContainsKey("sql-password-stdin")) throw new UsageException("--windows-auth : ni --sql-user ni mot de passe — c’est l’identité Windows du service qui se connecte.");
            return new SqlTarget(server, database, "", trust, SqlAuth.Windows);
        }
        return new SqlTarget(server, database, Required(o, "sql-user"), trust);
    }

    /// <summary>The service deletes a request it reads from pending\ — a file given to --config is the user's, and stays.</summary>
    private async Task WarnPasswordInFileAsync(IReadOnlyList<PendingRequest> requests)
    {
        if (requests.Any(r => r.Password is not null))
        {
            await Say("[!] Ce fichier contient un mot de passe en clair, et il reste là où il est : effacez-le après usage (ou retirez-en le mot de passe : il sera demandé à l’invite).");
        }
    }

    /// <summary>The file whole: one malformed binding, and nothing is done — every problem said at once.</summary>
    private static IReadOnlyList<PendingRequest> FromConfig(string path)
    {
        if (!File.Exists(path)) throw new UsageException($"--config : fichier introuvable — {path}");
        var (requests, errors) = PendingRequest.ParseFile(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
        if (errors.Count > 0) throw new UsageException("--config : " + string.Join(Environment.NewLine + "--config : ", errors) + Environment.NewLine + "Rien n’a été fait.");
        return requests;
    }

    /// <summary>
    /// The SQL password at a hidden prompt, or one LINE of standard input — with several bindings, one line
    /// per binding that needs one, in the file's order.
    /// </summary>
    private async Task<string> ReadPasswordAsync(bool fromStdin, PendingRequest? forRequest = null, bool many = false)
    {
        if (fromStdin)
        {
            var line = await input.ReadLineAsync();
            if (string.IsNullOrEmpty(line)) throw new UsageException("--sql-password-stdin : aucun mot de passe sur l’entrée standard" + (forRequest is null ? "." : $" pour « {forRequest.Name} »."));
            return line;
        }
        if (Console.IsInputRedirected) throw new UsageException("Mot de passe SQL : tapez-le à l’invite, ou passez --sql-password-stdin.");
        output.Write(many && forRequest is not null
            ? $"Mot de passe du compte SQL « {forRequest.Sql.User} » ({forRequest.Name}) : "
            : "Mot de passe du compte SQL : ");
        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && password.Length > 0) password.Length--;
            else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
        }
        output.WriteLine();
        return password.ToString();
    }

    private static string Required(Dictionary<string, string?> o, string key) =>
        o.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new UsageException($"--{key} est requis.");

    private static void Only(Dictionary<string, string?> o, params string[] allowed)
    {
        var extra = o.Keys.FirstOrDefault(k => !allowed.Contains(k, StringComparer.Ordinal));
        if (extra is not null) throw new UsageException($"--{extra} ne va pas avec --{allowed[0]} : le fichier dit tout.");
    }

    private static Dictionary<string, string?> Options(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"Argument inattendu : « {args[i]} ».");
            var key = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            options[key] = hasValue ? args[++i] : null;
        }
        return options;
    }

    private sealed class UsageException(string message) : Exception(message);
}
