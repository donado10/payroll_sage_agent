using System.Security.Principal;
using PayrollSageAgent.Core.Bindings;
using PayrollSageAgent.Core.Platform;
using PayrollSageAgent.Core.Sync;

namespace PayrollSageAgent.Service.Bindings;

/// <summary>
/// Checking a SQL login and pairing a binding — ONE implementation, run by the verbs at the console and by
/// the service for a request dropped into <c>pending\</c>. What it says goes through <c>say</c>: the console
/// for a verb, the result file and the log for the service. The exit codes are the verbs':
/// 0 done · 2 request incomplete · 3 SQL refused · 4 platform refused · 5 platform unreachable.
///
/// Nothing said here carries a secret: the password is never repeated, the code never printed back.
/// </summary>
public sealed class BindingPairing(BindingStore store, ISecretProtector protector, IPlatformClient platform, ISageChartReader reader)
{
    public async Task<int> CheckAsync(SqlTarget target, string password, Func<string, Task> say, CancellationToken ct)
    {
        var (exit, _) = await CheckLoginAsync(target, password, say, ct);
        if (exit == 0) await say($"Accès accepté : {Who(target)} lit F_COMPTEG dans « {target.Database} ».");
        return exit;
    }

    /// <param name="replacing">A binding of the same name that this pairing REPLACES (a new code in agent.json):
    /// the new one is paired and saved FIRST, then the old one unpaired — a mistyped code leaves the old intact.</param>
    /// <param name="accessChecked">The SQL access was already checked — by the SERVICE, for Windows authentication.</param>
    /// <param name="accountFilter">The comptes généraux it syncs (agent.json › <c>accountFilter</c>); none: all.</param>
    /// <param name="partnerFilter">The comptes tiers it syncs (agent.json › <c>partnerFilter</c>); none: all.</param>
    public async Task<int> AddAsync(string name, Uri url, string code, SqlTarget target, string password, Func<string, Task> say, CancellationToken ct,
        BindingConfig? replacing = null, bool accessChecked = false, IReadOnlyList<string>? accountFilter = null, IReadOnlyList<string>? partnerFilter = null)
    {
        if (!BindingRules.IsValidName(name))
        {
            await say($"Refusé : le nom « {name} » — {BindingRules.NameRule}.");
            return 2;
        }
        if (replacing is null && store.Get(name) is { } existing)
        {
            await say(existing.State == BindingStates.Active
                ? $"Refusé : une liaison « {name} » existe déjà. Retirez-la d’abord : PayrollSageAgent remove-binding --name {name}"
                : $"Refusé : une liaison « {name} » révoquée existe encore. Retirez-la d’abord : PayrollSageAgent remove-binding --name {name}");
            return 2;
        }
        if (url.Scheme == Uri.UriSchemeHttp && !AgentAddress.IsThisMachine(url))
        {
            await say("[!] Adresse en HTTP : le code d’appairage, le jeton et le plan comptable circuleront en clair sur le réseau. Accepté pour l’instant ; HTTPS viendra.");
        }
        if (target.TrustServerCertificate)
        {
            await say("[!] Le certificat du serveur SQL ne sera pas vérifié (trust-server-certificate). Réservé à un serveur de test.");
        }

        // The folder first: a code spent on a binding that cannot be written is a code lost.
        store.Prepare();

        if (!accessChecked)
        {
            var (checkedExit, _) = await CheckLoginAsync(target, password, say, ct);
            if (checkedExit != 0) return checkedExit;
        }

        await say("Appairage avec la plateforme…");
        var paired = await platform.PairAsync(url, code, ct);
        if (paired.Refusal is { } refusal)
        {
            await say($"Refusé par la plateforme : {refusal.Message}");
            return 4;
        }
        if (!paired.IsOk || paired.Value is not { } answer)
        {
            await say($"La plateforme est injoignable ({paired.Transient}). Vérifiez l’adresse et l’accès Internet du serveur, puis recommencez.");
            return 5;
        }

        // The address for every later call is the platform's — unless it points INWARD from where the
        // platform was reached: back at this server, or from a public address to a private one, which at a
        // customer's is one of THEIR machines (AgentAddress). The binding is undone at once, through the
        // address that worked, and the token never leaves for the wrong place.
        if (!Uri.TryCreate(answer.Endpoint, UriKind.Absolute, out var endpoint) || !AgentAddress.MayFollow(url, endpoint))
        {
            await platform.UnpairAsync(url, answer.Token, ct);
            await say($"Refusé : la plateforme, jointe à {url}, demande d’appeler ensuite « {answer.Endpoint} », qui ne la désigne pas depuis ce serveur. Son adresse d’agent est mal réglée (NEXT_PUBLIC_AGENT_URL) ; la liaison a été annulée.");
            return 4;
        }
        store.Save(new BindingConfig
        {
            Name = name,
            Endpoint = endpoint,
            BindingId = answer.BindingId,
            CompanyName = answer.Company.Name,
            ConnectionId = answer.Connection.Id,
            ConnectionName = answer.Connection.Name,
            Sql = target,
            ProtectedToken = protector.Protect(answer.Token),
            ProtectedSqlPassword = target.IsWindows ? "" : protector.Protect(password),
            PairedAt = DateTimeOffset.UtcNow,
            AccountFilter = accountFilter ?? [],
            PartnerFilter = partnerFilter ?? [],
        });
        if (replacing is not null)
        {
            // Pairing the SAME connection already revoked the old token; another one keeps it until told.
            var unpaired = await platform.UnpairAsync(replacing.Endpoint, protector.Unprotect(replacing.ProtectedToken), ct);
            await say(unpaired.IsOk ? "L’ancienne liaison de ce nom a été déliée." : "L’ancienne liaison de ce nom est remplacée.");
        }
        await say($"Liaison « {name} » établie : {answer.Company.Name} — {answer.Connection.Name}.");
        if (accountFilter is { Count: > 0 }) await say($"Comptes généraux synchronisés : {AccountFilter.Describe(accountFilter)}.");
        if (partnerFilter is { Count: > 0 }) await say($"Comptes tiers synchronisés : {AccountFilter.Describe(partnerFilter)}.");
        await say($"Secrets protégés : {protector.Kind}. Le service la prend en charge dans les 10 secondes.");
        return 0;
    }

    private async Task<(int Exit, SqlLoginRights? Rights)> CheckLoginAsync(SqlTarget target, string password, Func<string, Task> say, CancellationToken ct)
    {
        await say($"Vérification : {Who(target)} sur « {target.Database} » ({target.Server})…");
        SqlLoginRights rights;
        try
        {
            rights = await reader.CheckAsync(target, password, ct);
        }
        catch (SageReadException error)
        {
            await say($"Refusé : {error.Message}");
            if (error.Detail is { } detail) await say($"Détail : {detail}");
            return (3, null);
        }

        // What the login may do beyond reading — said, never refused (the user's decision of 2026-10-17:
        // any login, sa included; the passation comptable will write into Sage).
        if (rights.CanWrite)
        {
            await say("[i] Ce compte peut aussi écrire dans Sage : accepté (la passation comptable en aura besoin). L’agent, aujourd’hui, ne fait que lire.");
        }
        if (rights.SeesAllDatabases)
        {
            await say($"[i] Ce compte voit toutes les bases de l’instance : accepté. L’agent ne lit que « {target.Database} » ; sur un serveur partagé entre plusieurs clients, un compte dédié à chaque base reste préférable (compte-sql-lecture.sql).");
        }
        return (0, rights);
    }

    /// <summary>« le compte SQL « agent » », or the Windows identity this process signs in with.</summary>
    private static string Who(SqlTarget target)
    {
        if (!target.IsWindows) return $"le compte SQL « {target.User} »";
        var identity = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().Name : Environment.UserName;
        return $"l’identité Windows « {identity} » (vers un autre serveur : le compte machine {Environment.MachineName}$)";
    }
}
