using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PayrollSageAgent.Core.Bindings;

/// <summary>
/// <c>agent.json</c> — the agent's CONFIG, in its own folder: the bindings it should have, one or several
/// (<see cref="PendingRequest.ParseFile"/> with <c>config: true</c>). IT edits it (Configurer-agent.bat),
/// then applies it (Redemarrer-agent.bat → <c>apply-config</c>). Its <c>update</c> object says how the agent
/// updates itself (<see cref="Update.UpdateSettings"/>); its <c>accountFilter</c> (top, or per binding) which
/// comptes généraux are synced (<see cref="AccountFilter"/>), and its <c>partnerFilter</c> which comptes tiers.
///
/// [HARD] Once applied, the secrets LEAVE it: a pairing code used becomes « "" » (a code is spent once
/// anyway), a password the agent protected becomes <see cref="PendingRequest.ProtectedMarker"/>, and an
/// entry carrying <c>"remove": true</c> that was unlinked disappears. The file stays the readable list of
/// what is linked — never a store of secrets. JSON comments do not survive the rewrite.
/// </summary>
public static class AgentConfigFile
{
    public const string FileName = "agent.json";
    public const string ReportName = "agent.result.txt";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>What the MSI writes on a first install: valid, empty, to be filled from the examples.</summary>
    public const string Template = """
        {
          "url": "http://ADRESSE-DE-LA-PLATEFORME/api/agent",
          "sql": {
            "server": "SERVEUR\\INSTANCE",
            "trustServerCertificate": true
          },
          "update": { "auto": true, "window": "03:00-05:00" },
          "accountFilter": [],
          "partnerFilter": [],
          "bindings": []
        }
        """;

    /// <summary>
    /// The file with the secrets of what WORKED taken out: <paramref name="paired"/> lose their code,
    /// <paramref name="protectedPasswords"/> their password (and the shared one, when every binding that
    /// inherits it is among them), <paramref name="removed"/> leave the list. Null when nothing changes.
    /// </summary>
    public static string? WithoutSecrets(string json, IReadOnlySet<string> paired, IReadOnlySet<string> protectedPasswords, IReadOnlySet<string> removed)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        if (root is null) return null;
        var changed = false;

        if (root["bindings"] is not JsonArray list)
        {
            // One binding, the whole file.
            var name = root["name"]?.GetValue<string>();
            if (name is null) return null;
            changed |= Scrub(root, paired.Contains(name), protectedPasswords.Contains(name));
            return changed ? root.ToJsonString(Indented) + Environment.NewLine : null;
        }

        var inheritors = new List<string>();
        foreach (var entry in list.OfType<JsonObject>().ToList())
        {
            var name = entry["name"]?.GetValue<string>();
            if (name is null) continue;
            if (removed.Contains(name))
            {
                list.Remove(entry);
                changed = true;
                continue;
            }
            changed |= Scrub(entry, paired.Contains(name), protectedPasswords.Contains(name));
            var own = entry["sql"] as JsonObject;
            if (own?["password"] is null && own?["auth"]?.GetValue<string>() != SqlAuth.Windows) inheritors.Add(name);
        }

        // The shared password goes once every binding that inherits it holds it protected.
        if (root["sql"] is JsonObject shared && IsSecret(shared["password"]) && inheritors.Count > 0 && inheritors.All(protectedPasswords.Contains))
        {
            shared["password"] = PendingRequest.ProtectedMarker;
            changed = true;
        }
        return changed ? root.ToJsonString(Indented) + Environment.NewLine : null;
    }

    private static bool Scrub(JsonObject entry, bool paired, bool passwordProtected)
    {
        var changed = false;
        if (paired && entry["code"] is JsonValue code && code.GetValue<string>() != "")
        {
            entry["code"] = "";
            changed = true;
        }
        if (passwordProtected && entry["sql"] is JsonObject sql && IsSecret(sql["password"]))
        {
            sql["password"] = PendingRequest.ProtectedMarker;
            changed = true;
        }
        return changed;
    }

    private static bool IsSecret(JsonNode? password) =>
        password is JsonValue value && value.TryGetValue<string>(out var text) && text != "" && text != PendingRequest.ProtectedMarker;
}
