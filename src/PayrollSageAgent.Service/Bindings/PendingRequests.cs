using System.Text.Json;
using PayrollSageAgent.Core.Bindings;

namespace PayrollSageAgent.Service.Bindings;

/// <summary>
/// The request files dropped into <c>pending\</c> (<see cref="PendingRequest"/> — one binding, or several
/// sharing a server), carried out BY THE SERVICE —
/// under its own identity, which is the point for Windows authentication — at every pass of the supervisor.
///
/// [HARD] A request file may hold a pairing code and a SQL password in clear, so it is read and DELETED at
/// once, before anything else happens; there is one attempt per file, success or not, and no retry with a
/// password left lying on disk. What happened goes to <c>&lt;nom&gt;.result.json</c> (no secret in it) and to
/// the log. A file written less than two seconds ago is left for the next pass: it may still be written.
/// Results are kept seven days.
/// </summary>
public sealed partial class PendingRequests(AgentHome home, BindingPairing pairing, TimeProvider clock, ILogger<PendingRequests> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KeepResults = TimeSpan.FromDays(7);
    public const string ResultSuffix = ".result.json";

    public static string ResultPath(AgentHome home, string stem) => Path.Combine(home.Pending, stem + ResultSuffix);

    public async Task ProcessAsync(CancellationToken ct)
    {
        if (!Directory.Exists(home.Pending)) return;
        var now = clock.GetUtcNow();
        foreach (var file in Directory.EnumerateFiles(home.Pending, "*.json").Order(StringComparer.Ordinal).ToList())
        {
            if (file.EndsWith(ResultSuffix, StringComparison.OrdinalIgnoreCase))
            {
                if (now - File.GetLastWriteTimeUtc(file) > KeepResults) TryDelete(file);
                continue;
            }
            if (now - File.GetLastWriteTimeUtc(file) < Settle) continue;

            var stem = Path.GetFileNameWithoutExtension(file);
            string text;
            try
            {
                text = File.ReadAllText(file);
                File.Delete(file);
            }
            catch (IOException)
            {
                continue; // still open elsewhere: the next pass
            }

            LogReceived(stem);
            var lines = new List<string>();
            Task Say(string line)
            {
                lines.Add(line);
                LogLine(stem, line);
                return Task.CompletedTask;
            }

            var (requests, errors) = PendingRequest.ParseFile(text, stem);
            var outcomes = new List<PendingBindingResult>();
            if (errors.Count > 0)
            {
                // The file whole first: one malformed binding, and nothing is done — every problem said at once.
                foreach (var error in errors) await Say($"Refusé : {error}");
                await Say("Rien n’a été fait : corrigez le fichier et déposez-le de nouveau.");
            }
            else
            {
                // Then each binding on its own: a code that expired, a login refused, for one client only.
                foreach (var request in requests)
                {
                    if (requests.Count > 1) await Say($"— {request.Name} —");
                    var one = await RunAsync(request, Say, ct);
                    outcomes.Add(new PendingBindingResult(request.Name, one == 0, one));
                }
                if (requests.Count > 1) await Say($"{outcomes.Count(o => o.Ok)} liaison(s) sur {outcomes.Count} faite(s).");
            }

            var exit = errors.Count > 0 ? 2 : outcomes.FirstOrDefault(o => !o.Ok)?.ExitCode ?? 0;
            WriteResult(stem, new PendingResult(exit == 0, exit, lines, clock.GetUtcNow(), outcomes));
            LogDone(stem, exit);
        }
    }

    private async Task<int> RunAsync(PendingRequest request, Func<string, Task> say, CancellationToken ct)
    {
        if (!request.Sql.IsWindows && request.Password is null)
        {
            await say("Refusé : « sql.password » manque. Donnez-le dans le fichier, ou utilisez PayrollSageAgent add-binding --config <fichier>, qui le demande à l’invite.");
            return 2;
        }
        return request.Action == PendingRequest.CheckSql
            ? await pairing.CheckAsync(request.Sql, request.Password ?? "", say, ct)
            : await pairing.AddAsync(request.Name, request.Url!, request.Code!, request.Sql, request.Password ?? "", say, ct);
    }

    private void WriteResult(string stem, PendingResult result)
    {
        var target = ResultPath(home, stem);
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(result, Json));
        File.Move(temporary, target, overwrite: true);
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // the next pass
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Demande « {Stem} » reçue dans pending : fichier lu puis effacé.")]
    private partial void LogReceived(string stem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demande « {Stem} » : {Line}")]
    private partial void LogLine(string stem, string line);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demande « {Stem} » terminée, code {Exit}.")]
    private partial void LogDone(string stem, int exit);
}
