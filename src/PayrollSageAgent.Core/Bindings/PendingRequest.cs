using System.Text.Json;

namespace PayrollSageAgent.Core.Bindings;

/// <summary>
/// A request the SERVICE carries out: a JSON file dropped into the agent's <c>pending\</c> folder — by IT
/// (Notepad), by <c>add-binding --config</c> or a verb handing over to the service, later by the platform's
/// download. The service reads it within 10 seconds, DELETES it at once (it may hold pairing codes and SQL
/// passwords in clear), does what it asks, and writes <c>&lt;fichier&gt;.result.json</c> beside it.
///
/// ONE binding — the whole file is the binding:
/// <code>
/// {
///   "action": "add-binding",                       // or "check-sql"; add-binding when absent
///   "name": "compta21",                            // the file's name when absent
///   "url": "http://srv.exemple.sn:53231/api/agent", // add-binding only
///   "code": "ABCD-EFGH-JKLM",                      // add-binding only
///   "sql": {
///     "server": "SERVEUR\\INSTANCE",               // a JSON backslash is written TWICE
///     "database": "COMPTA21",
///     "auth": "sql",                               // or "windows": the service's own Windows identity
///     "user": "agent_paie", "password": "…",       // sql only; no password = asked at the console (--config)
///     "trustServerCertificate": true
///   }
/// }
/// </code>
///
/// SEVERAL bindings — a hosting company's server, one database per client: what they share at the top,
/// each binding below with its OWN name, code and database, overriding any shared value:
/// <code>
/// {
///   "url": "http://srv.exemple.sn:53231/api/agent",
///   "sql": { "server": "SERVEUR\\INSTANCE", "trustServerCertificate": true },
///   "bindings": [
///     { "name": "client-a", "code": "AAAA-…", "sql": { "database": "CLIENT_A", "user": "agent_a", "password": "…" } },
///     { "name": "client-b", "code": "BBBB-…", "sql": { "database": "CLIENT_B", "auth": "windows" } }
///   ]
/// }
/// </code>
///
/// [HARD] The FILE is checked whole first: one malformed binding, and nothing is done — the answer lists
/// every problem at once, so one correction is enough. Then each binding succeeds or fails ON ITS OWN: a
/// code can expire, a login be refused, for one client only. Unknown keys are refused: « pasword » must not
/// pass silently. Why the SERVICE: under Windows authentication the check must run as the identity that
/// will read Sage — the service's — not as the administrator typing a command.
/// </summary>
public sealed record PendingRequest(string Action, string Name, Uri? Url, string? Code, SqlTarget Sql, string? Password, bool Remove = false)
{
    /// <summary>What a password reads in <c>agent.json</c> once the agent has protected it (<see cref="AgentConfigFile"/>).</summary>
    public const string ProtectedMarker = "(protégé)";

    public const string AddBinding = "add-binding";
    public const string CheckSql = "check-sql";

    private static readonly HashSet<string> SingleKeys = ["action", "name", "url", "code", "sql"];
    private static readonly HashSet<string> SharedKeys = ["action", "url", "sql", "bindings"];
    private static readonly HashSet<string> ConfigSharedKeys = ["action", "url", "sql", "bindings", "update"];
    private static readonly HashSet<string> EntryKeys = ["name", "url", "code", "sql"];
    private static readonly HashSet<string> ConfigEntryKeys = ["name", "url", "code", "sql", "remove"];
    private static readonly HashSet<string> SqlKeys = ["server", "database", "auth", "user", "password", "trustServerCertificate"];

    /// <summary>
    /// Every binding the file describes — one, or the <c>bindings</c> list with the shared values applied —
    /// or every reason it cannot be carried out, in French. Never both.
    /// </summary>
    /// <param name="config">
    /// <c>agent.json</c>, the agent's CONFIG — the desired state rather than a one-shot request: a binding
    /// already linked has no code (« "" » once used), a password the agent protected reads
    /// <see cref="ProtectedMarker"/> (both mean « keep what is stored »), <c>"remove": true</c> unlinks one,
    /// and an empty <c>bindings</c> list is a valid file.
    /// </param>
    public static (IReadOnlyList<PendingRequest> Requests, IReadOnlyList<string> Errors) ParseFile(string json, string fileStem, bool config = false)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException error)
        {
            var line = error.LineNumber is { } n ? $" (ligne {n + 1})" : "";
            return ([], [$"le fichier n’est pas du JSON valide{line}. Dans un fichier JSON, une barre oblique inverse s’écrit deux fois : \"SERVEUR\\\\INSTANCE\"."]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ([], ["le fichier doit contenir un objet JSON { … }."]);

            var action = Text(root, "action") ?? AddBinding;
            if (action != AddBinding && action != CheckSql) return ([], [$"« action » vaut « {AddBinding} » ou « {CheckSql} », pas « {action} »."]);

            if (!root.TryGetProperty("bindings", out var list))
            {
                if (Unknown(root, SingleKeys) is { } top) return ([], [$"clé inconnue « {top} » (attendues : {string.Join(", ", SingleKeys)}, ou « bindings » pour plusieurs liaisons)."]);
                var (single, error) = Build(action, Text(root, "name") ?? fileStem, Text(root, "url"), Text(root, "code"), SqlValues(root, null, out var sqlError), config);
                return single is not null && sqlError is null ? ([single], []) : ([], [sqlError ?? error!]);
            }

            var sharedKeys = config ? ConfigSharedKeys : SharedKeys;
            if (Unknown(root, sharedKeys) is { } shared)
            {
                return ([], [$"clé « {shared} » : avec « bindings », le haut du fichier ne porte que ce que les liaisons partagent ({string.Join(", ", sharedKeys)}) ; le nom et le code vont dans chaque liaison."]);
            }
            if (list.ValueKind != JsonValueKind.Array || (list.GetArrayLength() == 0 && !config)) return ([], ["« bindings » est une liste d’au moins une liaison : [ { … }, { … } ]."]);
            var entryKeys = config ? ConfigEntryKeys : EntryKeys;

            var requests = new List<PendingRequest>();
            var errors = new List<string>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            // agent.json's « update » — how the agent updates itself — is checked with the rest of the file.
            if (config && Update.UpdateSettings.FromConfig(root).Error is { } updateError) errors.Add(updateError);
            var index = 0;
            foreach (var entry in list.EnumerateArray())
            {
                index++;
                var label = $"liaison {index}";
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"{label} : un objet {{ … }} est attendu.");
                    continue;
                }
                var name = Text(entry, "name");
                if (name is not null) label += $" (« {name} »)";
                if (Unknown(entry, entryKeys) is { } key)
                {
                    errors.Add($"{label} : clé inconnue « {key} » (attendues : {string.Join(", ", entryKeys)}).");
                    continue;
                }
                if (name is null)
                {
                    errors.Add($"{label} : « name » est requis pour chaque liaison.");
                    continue;
                }
                if (config && entry.TryGetProperty("remove", out var remove) && remove.ValueKind == JsonValueKind.True)
                {
                    // Unlinking needs nothing but the name.
                    if (!BindingRules.IsValidName(name)) errors.Add($"{label} : « name » : {BindingRules.NameRule}.");
                    else if (!names.Add(name)) errors.Add($"{label} : le nom « {name} » figure deux fois dans le fichier.");
                    else requests.Add(new PendingRequest(action, name, null, null, new SqlTarget("", "", ""), null, Remove: true));
                    continue;
                }
                var target = SqlValues(entry, root.TryGetProperty("sql", out var defaults) ? defaults : null, out var sqlError);
                var (request, error) = Build(action, name, Text(entry, "url") ?? Text(root, "url"), Text(entry, "code"), target, config);
                var problem = sqlError ?? error;
                if (problem is not null)
                {
                    errors.Add($"{label} : {problem}");
                }
                else if (!names.Add(request!.Name))
                {
                    errors.Add($"{label} : le nom « {request.Name} » figure deux fois dans le fichier.");
                }
                else
                {
                    requests.Add(request);
                }
            }
            return errors.Count > 0 ? ([], errors) : (requests, []);
        }
    }

    /// <summary>
    /// One binding's SQL values: its own, over the file's shared ones, key by key. A binding that declares
    /// <c>"auth": "windows"</c> ITSELF does not inherit the shared login — a user and a password belong to
    /// SQL authentication; only one written on that binding is an error.
    /// </summary>
    private static Dictionary<string, JsonElement>? SqlValues(JsonElement owner, JsonElement? shared, out string? error)
    {
        error = null;
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var own = owner.TryGetProperty("sql", out var ownSql) ? ownSql : (JsonElement?)null;
        foreach (var source in new[] { shared, own })
        {
            if (source is not { } element) continue;
            if (element.ValueKind != JsonValueKind.Object)
            {
                error = "« sql » est un objet { \"server\", \"database\", … }.";
                return null;
            }
            if (Unknown(element, SqlKeys) is { } key)
            {
                error = $"clé inconnue « sql.{key} » (attendues : {string.Join(", ", SqlKeys)}).";
                return null;
            }
            if (source.Equals(own) && Text(element, "auth") == SqlAuth.Windows)
            {
                merged.Remove("user");
                merged.Remove("password");
            }
            foreach (var property in element.EnumerateObject()) merged[property.Name] = property.Value;
        }
        if (merged.Count == 0) error = "« sql » est requis : { \"server\", \"database\", … }.";
        return merged;
    }

    private static (PendingRequest? Request, string? Error) Build(string action, string rawName, string? rawUrl, string? rawCode, Dictionary<string, JsonElement>? sql, bool config = false)
    {
        if (sql is null) return (null, "« sql » est requis.");
        var name = rawName.Trim();
        if (!BindingRules.IsValidName(name)) return (null, $"« name » : {BindingRules.NameRule}.");
        var server = Text(sql, "server");
        var database = Text(sql, "database");
        if (string.IsNullOrWhiteSpace(server)) return (null, "« sql.server » est requis.");
        if (string.IsNullOrWhiteSpace(database)) return (null, "« sql.database » est requis.");
        var auth = Text(sql, "auth") ?? SqlAuth.Sql;
        if (auth != SqlAuth.Sql && auth != SqlAuth.Windows) return (null, $"« sql.auth » vaut « {SqlAuth.Sql} » ou « {SqlAuth.Windows} », pas « {auth} ».");
        var user = Text(sql, "user");
        var password = Text(sql, "password");
        // In agent.json, an empty or protected password means « keep the one stored ».
        if (config && (password is "" or ProtectedMarker)) password = null;
        if (auth == SqlAuth.Sql && string.IsNullOrWhiteSpace(user)) return (null, "« sql.user » est requis avec « auth » : « sql ».");
        if (auth == SqlAuth.Windows && (user is not null || password is not null))
        {
            return (null, "avec « auth » : « windows », ni « user » ni « password » : c’est l’identité Windows du service qui se connecte.");
        }
        var trust = sql.TryGetValue("trustServerCertificate", out var t) && t.ValueKind == JsonValueKind.True;
        var target = new SqlTarget(server.Trim(), database.Trim(), auth == SqlAuth.Windows ? "" : user!.Trim(), trust, auth);

        Uri? url = null;
        string? code = null;
        if (action == AddBinding)
        {
            // In agent.json a code is there only to pair (again): « "" » once used. The address matters only then.
            var pairing = !config || !string.IsNullOrWhiteSpace(rawCode);
            if (pairing)
            {
                code = rawCode is { } c ? BindingRules.Code(c) : null;
                if (code is null) return (null, $"« code » : {BindingRules.CodeRule}.");
            }
            url = rawUrl is { } u && !string.IsNullOrWhiteSpace(u) ? BindingRules.Address(u) : null;
            if (url is null && pairing) return (null, $"« url » : {BindingRules.UrlRule}.");
        }
        return (new PendingRequest(action, name, url, code, target, password), null);
    }

    private static string? Text(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Text(Dictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Unknown(JsonElement element, HashSet<string> allowed) =>
        element.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !allowed.Contains(n));
}

/// <summary>
/// What the service did with a request file — written beside it as <c>&lt;fichier&gt;.result.json</c>; no
/// secret in it. <see cref="Bindings"/> says it binding by binding; <see cref="Ok"/> is true when ALL did.
/// </summary>
public sealed record PendingResult(bool Ok, int ExitCode, IReadOnlyList<string> Lines, DateTimeOffset At, IReadOnlyList<PendingBindingResult>? Bindings = null);

public sealed record PendingBindingResult(string Name, bool Ok, int ExitCode);
