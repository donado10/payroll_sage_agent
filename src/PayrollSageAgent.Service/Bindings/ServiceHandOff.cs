using System.Text.Json;
using PayrollSageAgent.Core.Bindings;

namespace PayrollSageAgent.Service.Bindings;

/// <summary>
/// Asking the RUNNING SERVICE to do something under its own identity: a request file written into
/// <c>pending\</c>, then its answer read back. What needs it: the SQL check under Windows authentication,
/// which must be made AS the identity that will read Sage — the service's, never the administrator's at a
/// console nor SYSTEM's in the MSI. 25 s for the service to pick the request up (else it is withdrawn), 120 s
/// for the answer.
/// </summary>
public sealed class ServiceHandOff(AgentHome home)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan ServiceAnswer = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ServicePickUp = TimeSpan.FromSeconds(25);

    /// <summary>The access check of a Windows-authentication target, made by the service; its lines said through <paramref name="say"/>.</summary>
    public async Task<int> CheckAsync(SqlTarget target, Func<string, Task> say, CancellationToken ct)
    {
        await say("Authentification Windows : la vérification se fait sous l’identité du SERVICE…");
        var body = new Dictionary<string, object?>
        {
            ["action"] = PendingRequest.CheckSql,
            ["sql"] = new Dictionary<string, object?>
            {
                ["server"] = target.Server,
                ["database"] = target.Database,
                ["auth"] = target.Auth,
                ["trustServerCertificate"] = target.TrustServerCertificate,
            },
        };
        var (exit, lines) = await HandOverAsync($"check-{DateTime.UtcNow:yyyyMMddHHmmssfff}", JsonSerializer.Serialize(body, Json), ct);
        foreach (var line in lines) await say(line);
        return exit;
    }

    /// <summary>
    /// Puts a request file into <c>pending\</c> and waits for the SERVICE's answer: its exit code and lines, or
    /// 7 with a sentence when the service did not pick the request up (it is then withdrawn) or did not answer.
    /// </summary>
    public async Task<(int Exit, IReadOnlyList<string> Lines)> HandOverAsync(string stem, string json, CancellationToken ct)
    {
        home.Ensure();
        var file = Path.Combine(home.Pending, stem + ".json");
        var result = PendingRequests.ResultPath(home, stem);
        if (File.Exists(file)) return (2, [$"Une demande « {stem} » attend déjà dans {home.Pending}. Retirez-la, ou attendez que le service la traite."]);
        if (File.Exists(result)) File.Delete(result);

        var temporary = file + ".tmp";
        await File.WriteAllTextAsync(temporary, json, ct);
        File.Move(temporary, file);

        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < ServiceAnswer)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            if (File.Exists(result))
            {
                var answer = JsonSerializer.Deserialize<PendingResult>(await File.ReadAllTextAsync(result, ct), Json);
                File.Delete(result);
                return (answer?.ExitCode ?? 1, answer?.Lines ?? []);
            }
            if (File.Exists(file) && DateTime.UtcNow - started > ServicePickUp)
            {
                File.Delete(file);
                return (7, ["Le service ne traite pas la demande : est-il démarré ? services.msc (le service PayrollSageAgent, « IGF Paie - Agent Sage » sauf libellé changé). Demande retirée."]);
            }
        }
        return (7, [$"Le service n’a pas encore répondu. Son journal dira la suite ; sa réponse arrivera dans {result}."]);
    }
}
