using PayrollSageAgent.Core.Bindings;
using PayrollSageAgent.Core.Contract;
using PayrollSageAgent.Core.Platform;

namespace PayrollSageAgent.Service.Bindings;

/// <summary>
/// Applies <c>agent.json</c> — the bindings the agent SHOULD have — to the bindings it HAS. Run by
/// <c>Redemarrer-agent.bat</c> (as administrator) and by the MSI (as SYSTEM), never by the service: the
/// service only does, through <see cref="ServiceHandOff"/>, the one thing that needs its identity — the SQL
/// check under Windows authentication.
///
/// The file is checked WHOLE first (nothing done if one entry is malformed); then each binding on its own:
///   absent, with a code             → checked, paired, saved
///   linked, with a NEW code          → paired again (the new one saved first, then the old one let go)
///   linked, no code, same settings   → unchanged
///   linked, a password or a setting changed → checked with it, then updated (the service, restarted, rereads it)
///   <c>"remove": true</c>             → unpaired and deleted, and the entry leaves the file
///   absent, no code                  → refused: a code is needed to link it
/// Bindings the agent has that the file does not name are LEFT ALONE, and said. Then the secrets of what
/// worked leave the file (<see cref="AgentConfigFile.WithoutSecrets"/>).
/// </summary>
/// <param name="updateSettingsFile">Where the file's <c>update</c> object is applied (the update folder's
/// <c>settings.json</c>, read by the updater) — absent from the file, the defaults are.</param>
public sealed class ConfigApplier(BindingStore store, ISecretProtector protector, IPlatformClient platform, BindingPairing pairing, ServiceHandOff handOff,
    string? updateSettingsFile = null)
{
    public sealed record Outcome(int Exit, bool RestartNeeded);

    public async Task<Outcome> ApplyAsync(string path, Func<string, Task> say, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            await say($"Refusé : {path} n’existe pas. Le MSI le crée à l’installation ; sinon copiez exemple-plusieurs-liaisons.json sous ce nom.");
            return new Outcome(2, false);
        }
        var text = await File.ReadAllTextAsync(path, ct);
        var (entries, errors) = PendingRequest.ParseFile(text, "agent", config: true);
        if (errors.Count > 0)
        {
            foreach (var error in errors) await say($"Refusé : {error}");
            await say("Rien n’a été fait : corrigez agent.json, puis relancez Redemarrer-agent.bat.");
            return new Outcome(2, false);
        }
        if (entries.Count == 0)
        {
            await say("agent.json ne décrit encore aucune liaison : ajoutez-les dans « bindings » (modèle : exemple-plusieurs-liaisons.json).");
        }
        if (updateSettingsFile is not null) await ApplyUpdateSettingsAsync(text, say);

        var paired = new HashSet<string>(StringComparer.Ordinal);
        var protectedPasswords = new HashSet<string>(StringComparer.Ordinal);
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var exits = new List<int>();
        var restart = false;
        foreach (var entry in entries)
        {
            await say($"— {entry.Name} —");
            var one = await ApplyOneAsync(entry, say, ct);
            exits.Add(one.Exit);
            restart |= one.RestartNeeded;
            if (one.Paired) paired.Add(entry.Name);
            if (one.PasswordProtected) protectedPasswords.Add(entry.Name);
            if (one.Removed) removed.Add(entry.Name);
        }
        foreach (var other in store.List().Where(b => entries.All(e => e.Name != b.Name)))
        {
            await say($"« {other.Name} » : hors d’agent.json — gardée telle quelle.");
        }

        var rewritten = AgentConfigFile.WithoutSecrets(text, paired, protectedPasswords, removed);
        if (rewritten is not null)
        {
            await File.WriteAllTextAsync(path, rewritten, ct);
            await say("agent.json : les codes utilisés et les mots de passe protégés en ont été retirés.");
        }
        AgentHome.RestrictToAdministrators(path);
        if (entries.Count > 1) await say($"{exits.Count(e => e == 0)} liaison(s) sur {exits.Count} en ordre.");
        return new Outcome(exits.FirstOrDefault(e => e != 0), restart);
    }

    /// <summary>The file's « update », already checked with the rest of it; written where the updater reads it.</summary>
    private async Task ApplyUpdateSettingsAsync(string text, Func<string, Task> say)
    {
        using var document = System.Text.Json.JsonDocument.Parse(text, new System.Text.Json.JsonDocumentOptions
        {
            CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        var settings = Core.Update.UpdateSettings.FromConfig(document.RootElement).Settings ?? Core.Update.UpdateSettings.Default;
        try
        {
            settings.Write(updateSettingsFile!);
            await say(document.RootElement.TryGetProperty("update", out _)
                ? settings.Sentence
                : $"{settings.Sentence} (par défaut : agent.json n’a pas de « update »)");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await say($"Les réglages de mise à jour n’ont pas pu être enregistrés ({error.GetType().Name}).");
        }
    }

    private sealed record One(int Exit, bool Paired = false, bool PasswordProtected = false, bool Removed = false, bool RestartNeeded = false);

    private async Task<One> ApplyOneAsync(PendingRequest entry, Func<string, Task> say, CancellationToken ct)
    {
        var existing = store.Get(entry.Name);

        if (entry.Remove)
        {
            if (existing is null)
            {
                await say("Déjà absente : rien à retirer.");
                return new One(0, Removed: true);
            }
            if (existing.State == BindingStates.Active)
            {
                var unpaired = await platform.UnpairAsync(existing.Endpoint, protector.Unprotect(existing.ProtectedToken), ct);
                await say(unpaired.IsOk || unpaired.IsRefused(ContractNames.TokenInvalid)
                    ? "La plateforme a délié cette base."
                    : $"La plateforme n’a pas pu être prévenue ({unpaired.Transient ?? unpaired.Refusal?.Code}) — révoquez la liaison depuis son écran.");
            }
            store.Delete(entry.Name);
            await say("Retirée de l’agent, avec ses secrets.");
            return new One(0, Removed: true);
        }

        var newCode = entry.Code is not null;
        if (existing is null && !newCode)
        {
            await say("Refusé : pas encore reliée, et pas de « code » — générez-en un sur la plateforme (ERPs › Agent Sage).");
            return new One(2);
        }
        if (existing is { State: BindingStates.Revoked } && !newCode)
        {
            await say("Refusé : cette liaison a été révoquée sur la plateforme — un nouveau « code » la relie de nouveau.");
            return new One(2);
        }

        // The password: the one in the file, else the one stored (unchanged binding, still a SQL login).
        var password = entry.Password;
        if (!entry.Sql.IsWindows && password is null)
        {
            if (existing is { } kept && !kept.Sql.IsWindows && kept.ProtectedSqlPassword != "")
            {
                password = protector.Unprotect(kept.ProtectedSqlPassword);
            }
            else
            {
                await say("Refusé : « sql.password » manque — une nouvelle liaison, ou un passage à un compte SQL, en demande un.");
                return new One(2);
            }
        }

        if (newCode)
        {
            if (existing is not null) await say("Nouveau code : la liaison de ce nom sera remplacée si l’appairage réussit.");
            var checkedByService = false;
            if (entry.Sql.IsWindows)
            {
                var access = await handOff.CheckAsync(entry.Sql, say, ct);
                if (access != 0) return new One(access);
                checkedByService = true;
            }
            var exit = await pairing.AddAsync(entry.Name, entry.Url!, entry.Code!, entry.Sql, password ?? "", say, ct, replacing: existing, accessChecked: checkedByService);
            return new One(exit, Paired: exit == 0, PasswordProtected: exit == 0 && entry.Password is not null, RestartNeeded: exit == 0 && existing is not null);
        }

        // Linked, no new code: only what changed.
        var current = existing!;
        var settingsChanged = current.Sql != entry.Sql;
        var passwordChanged = entry.Password is not null;
        if (!settingsChanged && !passwordChanged)
        {
            await say("En ordre — inchangée.");
            return new One(0);
        }
        var checkedExit = entry.Sql.IsWindows
            ? await handOff.CheckAsync(entry.Sql, say, ct)
            : await pairing.CheckAsync(entry.Sql, password!, say, ct);
        if (checkedExit != 0) return new One(checkedExit);
        store.Save(current with
        {
            Sql = entry.Sql,
            ProtectedSqlPassword = entry.Sql.IsWindows ? "" : protector.Protect(password!),
        });
        await say(settingsChanged ? "Mise à jour (connexion SQL modifiée)." : "Mise à jour (nouveau mot de passe, protégé).");
        return new One(0, PasswordProtected: passwordChanged && !entry.Sql.IsWindows, RestartNeeded: true);
    }
}
