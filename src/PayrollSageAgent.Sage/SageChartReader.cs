using Microsoft.Data.SqlClient;
using PayrollSageAgent.Core.Bindings;
using PayrollSageAgent.Core.Contract;
using PayrollSageAgent.Core.Sync;

namespace PayrollSageAgent.Sage;

/// <summary>
/// F_COMPTEG, read for ONE binding — the shape measured on 2026-10-17 (docs/project-book/01-sage/f-compteg.md).
///
/// [HARD] The CODE reads, and is confined: the connection names its ONE database, the session is
/// <c>ApplicationIntent=ReadOnly</c>, the queries are SELECTs on <c>dbo.F_COMPTEG</c> only, and nothing
/// here ever lists the instance's databases — the check below asks a PERMISSION, it does not look. The
/// LOGIN may hold more (any login is accepted since 2026-10-17, <c>sa</c> included): it is reported.
/// Errors are named in the contract's words; SQL Server's own error — number and text — rides along as
/// <see cref="SageReadException.Detail"/> and never leaves this machine (the console, the log file).
/// </summary>
public sealed class SageChartReader : ISageChartReader
{
    private const int ConnectSeconds = 15;
    private const int CommandSeconds = 30;

    // The four columns the contract needs; nothing else of the row is read.
    private const string ChartQuery =
        "SELECT CG_Num, CG_Intitule, CG_Sommeil, CG_Type FROM dbo.F_COMPTEG ORDER BY CG_Num";

    public async Task<IReadOnlyList<SageGlAccount>> ReadChartAsync(SqlTarget target, string password, CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(ConnectionString(target, password));
            await OpenAsync(connection, target, ct);
            await using var command = new SqlCommand(ChartQuery, connection) { CommandTimeout = CommandSeconds };
            await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct);

            var accounts = new List<SageGlAccount>();
            while (await reader.ReadAsync(ct))
            {
                var number = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim();
                var label = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
                var dormant = !reader.IsDBNull(2) && reader.GetInt16(2) != 0;
                var type = reader.IsDBNull(3) ? 0 : reader.GetInt16(3);
                accounts.Add(new SageGlAccount(number, string.IsNullOrEmpty(label) ? null : label, dormant, type));
            }
            return accounts;
        }
        catch (SqlException error)
        {
            throw Name(error, connecting: false, target.IsWindows);
        }
    }

    public async Task<SqlLoginRights> CheckAsync(SqlTarget target, string password, CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(ConnectionString(target, password));
            await OpenAsync(connection, target, ct);
            await using var command = new SqlCommand(
                """
                SELECT
                    HAS_PERMS_BY_NAME('dbo.F_COMPTEG', 'OBJECT', 'SELECT'),
                    HAS_PERMS_BY_NAME('dbo.F_COMPTEG', 'OBJECT', 'INSERT')
                        | HAS_PERMS_BY_NAME('dbo.F_COMPTEG', 'OBJECT', 'UPDATE')
                        | HAS_PERMS_BY_NAME('dbo.F_COMPTEG', 'OBJECT', 'DELETE'),
                    HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW ANY DATABASE')
                """, connection)
            { CommandTimeout = CommandSeconds };
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var canRead = !reader.IsDBNull(0) && reader.GetInt32(0) == 1;
            var canWrite = !reader.IsDBNull(1) && reader.GetInt32(1) != 0;
            var seesAll = !reader.IsDBNull(2) && reader.GetInt32(2) == 1;

            if (!canRead)
            {
                throw new SageReadException(ContractNames.SqlPermissionDenied, false,
                    $"Le compte SQL « {target.User} » ne peut pas lire dbo.F_COMPTEG dans « {target.Database} ». Est-ce bien une base Sage 100 ?");
            }
            return new SqlLoginRights(canWrite, seesAll);
        }
        catch (SqlException error)
        {
            throw Name(error, connecting: false, target.IsWindows);
        }
    }

    /// <summary>
    /// A SQL login with its password, or — <c>auth: windows</c> — the PROCESS's Windows identity and no
    /// credential at all: the service's virtual account on this machine, its machine account towards
    /// another server of the domain. Run by a verb, that would be the administrator's identity, which is
    /// why a Windows-auth check is handed to the service (<c>pending\</c>).
    /// </summary>
    internal static string ConnectionString(SqlTarget target, string password)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = target.Server,
            InitialCatalog = target.Database,
            IntegratedSecurity = target.IsWindows,
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = "PayrollSageAgent",
            ConnectTimeout = ConnectSeconds,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = target.TrustServerCertificate,
            // One binding, one database: never fail over to the login's default database.
            Pooling = true,
        };
        if (!target.IsWindows)
        {
            builder.UserID = target.User;
            builder.Password = password;
        }
        return builder.ConnectionString;
    }


    /// <summary>A failure while CONNECTING is not a failure while reading: a timeout here is the server not answering.</summary>
    private static async Task OpenAsync(SqlConnection connection, SqlTarget target, CancellationToken ct)
    {
        try
        {
            await connection.OpenAsync(ct);
        }
        catch (SqlException error)
        {
            throw Name(error, connecting: true, target.IsWindows);
        }
    }

    /// <summary>
    /// SQL Server's errors in the contract's words, each with a sentence that says what to look at, and
    /// SQL Server's own words kept as the local <see cref="SageReadException.Detail"/>.
    ///
    /// The network failures are recognised by the SNI error the message names (« error: 26 »), because the
    /// NUMBER differs between Windows' native network layer and the managed one used elsewhere; the text
    /// « (provider: SQL Network Interfaces, error: NN - …) » is the same in both. Anything not recognised
    /// keeps the old sentence — with, now, the detail under it.
    /// </summary>
    internal static SageReadException Name(SqlException error, bool connecting, bool windows = false)
    {
        var (code, retryable, sentence) = Classify(error, connecting, windows);
        return new SageReadException(code, retryable, sentence, error, Detail(error));
    }

    private const string WindowsLogins =
        "sur ce même serveur : CREATE LOGIN [NT SERVICE\\PayrollSageAgent] FROM WINDOWS ; vers un serveur SQL distant d’un domaine : CREATE LOGIN [DOMAINE\\SERVEUR$] FROM WINDOWS (le compte machine) — puis un utilisateur dans la base";

    private static (string Code, bool Retryable, string Sentence) Classify(SqlException error, bool connecting, bool windows)
    {
        var text = error.Message;
        if (windows && text.Contains("SSPI", StringComparison.Ordinal))
        {
            return (ContractNames.SqlLoginFailed, false,
                "L’authentification Windows n’a pas pu s’établir avec ce serveur SQL (SSPI). Hors domaine Active Directory, elle ne traverse pas les machines : utilisez un compte SQL. Dans un domaine : donnez le nom DNS complet du serveur, et vérifiez le SPN de SQL Server.");
        }
        if (IsCertificateRefused(error))
        {
            return (ContractNames.SqlUnreachable, false,
                "Le certificat du serveur SQL n’est pas reconnu par ce serveur Windows (auto-signé, ce qui est courant sur un serveur Sage). Reliez avec --trust-server-certificate, ou installez sur SQL Server un certificat reconnu.");
        }
        if (error.Number == -2146893007 || text.Contains("common algorithm", StringComparison.OrdinalIgnoreCase))
        {
            return (ContractNames.SqlUnreachable, false,
                "La connexion chiffrée (TLS) avec SQL Server n’a pas pu se négocier : ce serveur Windows et SQL Server n’ont pas de version de TLS en commun. SQL Server doit accepter TLS 1.2 (mises à jour de SQL Server).");
        }
        switch (error.Number)
        {
            case 18456 or 18452 when windows:
                return (ContractNames.SqlLoginFailed, false, $"Le compte Windows du service n’a pas accès à cette instance SQL — {WindowsLogins}.");
            case 18488:
                return (ContractNames.SqlLoginFailed, false, "Le mot de passe de ce compte SQL doit être changé (« l’utilisateur doit changer le mot de passe à la prochaine connexion ») : redéfinissez-le dans SSMS en décochant cette case, et décochez « Appliquer l’expiration du mot de passe ».");
            case 18487:
                return (ContractNames.SqlLoginFailed, false, "Le mot de passe de ce compte SQL a expiré : redéfinissez-le dans SSMS, et décochez « Appliquer l’expiration du mot de passe » — sinon la synchronisation s’arrêtera de nouveau le jour où il expire.");
            case 233:
                return (ContractNames.SqlLoginFailed, false, windows
                    ? $"SQL Server a fermé la connexion pendant l’ouverture de session : le compte Windows du service n’y a sans doute pas accès — {WindowsLogins}. Le journal de SQL Server dit pourquoi : EXEC xp_readerrorlog 0, 1, N'Login failed';"
                    : "SQL Server a fermé la connexion pendant l’ouverture de session — le plus souvent le compte : mot de passe à changer ou expiré, compte désactivé, ou instance en authentification Windows seule. Le journal de SQL Server dit pourquoi : EXEC xp_readerrorlog 0, 1, N'Login failed';");
            case 18456 or 18452:
                return (ContractNames.SqlLoginFailed, false, "Le compte SQL de cette liaison est refusé par le serveur (mot de passe, compte désactivé, ou authentification SQL Server non activée sur l’instance ?).");
            case 4060:
                return (ContractNames.SqlLoginFailed, false, "La base de cette liaison n’existe pas, ou le compte SQL n’y a pas accès.");
            case 229 or 230 or 262 or 297 or 300:
                return (ContractNames.SqlPermissionDenied, false, "Le compte SQL de cette liaison n’a pas le droit de lire le plan comptable.");
            case 208:
                return (ContractNames.SourceInvalid, false, "F_COMPTEG est introuvable : cette base n’est pas une société Sage 100 ?");
        }
        if (SniError(text) is 26 or 28 or 52)
        {
            return (ContractNames.SqlUnreachable, true,
                "L’instance SQL est introuvable : vérifiez SERVEUR\\INSTANCE (le service Windows s’appelle MSSQL$INSTANCE) et que « SQL Server Browser » est démarré — ou donnez le port : SERVEUR,PORT.");
        }
        if (SniError(text) is 40 or 25 || error.Number is 53 or 2 or 10061 or 1326)
        {
            return (ContractNames.SqlUnreachable, true,
                "SQL Server refuse la connexion réseau : le protocole TCP/IP est-il activé pour cette instance (Gestionnaire de configuration SQL Server) ? Sinon, le pare-feu ou le port.");
        }
        if (error.Number is 11001 or 11004)
        {
            return (ContractNames.SqlUnreachable, true, "Le nom du serveur SQL ne se résout pas depuis ce serveur Windows : vérifiez-le, ou donnez son adresse IP.");
        }
        if (error.Number is -2 or 258 or 10060)
        {
            return connecting
                ? (ContractNames.SqlUnreachable, true, $"SQL Server n’a pas répondu en {ConnectSeconds} s : le serveur, l’instance ou le port est-il joignable depuis ce serveur Windows ?")
                : (ContractNames.SqlTimeout, true, $"La lecture dans Sage a dépassé son délai ({CommandSeconds} s).");
        }
        return (ContractNames.SqlUnreachable, true, "Le serveur SQL ne répond pas.");
    }

    /// <summary>« (provider: …, error: 26 - …) » → 26; null when the message names none.</summary>
    private static int? SniError(string message)
    {
        var at = message.LastIndexOf("error: ", StringComparison.Ordinal);
        if (at < 0) return null;
        var digits = new string(message.AsSpan(at + 7).ToArray().TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>
    /// SQL Server's own error on one line: « n° 2, classe 20, état 0 — A network-related… (provider: Named
    /// Pipes Provider, error: 40 - …) », and the operating system's cause when it adds one. No secret is
    /// ever in it: SQL Server names the login it refused, never its password.
    /// </summary>
    internal static string Detail(SqlException error)
    {
        static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        var detail = $"n° {error.Number}, classe {error.Class}, état {error.State} — {OneLine(error.Message)}";
        if (error.InnerException is { } cause && !error.Message.Contains(cause.Message, StringComparison.Ordinal))
        {
            detail += $" [{cause.GetType().Name} : {OneLine(cause.Message)}]";
        }
        return detail.Length <= 800 ? detail : detail[..800] + "…";
    }

    /// <summary>
    /// The connection is encrypted (<c>Encrypt=Mandatory</c>) and the server's certificate is not one this
    /// machine trusts — the case of most Sage servers, whose SQL Server keeps the self-signed certificate it
    /// was installed with. Said apart because « ne répond pas » would send IT looking at the network. The
    /// numbers are Windows' (SChannel: untrusted root, wrong name); off Windows the cause is an
    /// AuthenticationException — both measured or documented, the second on this repo's Docker run.
    /// </summary>
    private static bool IsCertificateRefused(SqlException error)
    {
        if (error.Number is -2146893019 or -2146762487 or -2146893022) return true;
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
        {
            if (cause is System.Security.Authentication.AuthenticationException) return true;
        }
        return false;
    }
}
