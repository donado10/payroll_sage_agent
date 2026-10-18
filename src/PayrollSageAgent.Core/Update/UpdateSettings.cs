using System.Text.Json;

namespace PayrollSageAgent.Core.Update;

/// <summary>
/// How the agent updates itself — <c>agent.json</c> › <c>update</c>, APPLIED by <c>Redemarrer-agent.bat</c> into
/// the update folder's <c>settings.json</c>, which is what the updater reads (an <c>agent.json</c> half-edited
/// never decides an install). Absent from the file: the defaults — automatic, 03:00–05:00.
/// <code>
/// "update": { "auto": true, "window": "03:00-05:00" }
/// </code>
/// </summary>
public sealed record UpdateSettings(bool Auto, UpdateWindow Window)
{
    public static readonly UpdateSettings Default = new(true, UpdateWindow.Default);

    private static readonly HashSet<string> Keys = ["auto", "window"];

    /// <summary>What the apply says — and LISEZMOI.txt repeats.</summary>
    public string Sentence => Auto
        ? $"Mises à jour automatiques : activées, {Window.Sentence} (heure de ce serveur)."
        : "Mises à jour automatiques : désactivées — Mettre-a-jour-agent.bat installe la dernière version à la demande.";

    /// <summary>
    /// The <c>update</c> object of agent.json's root, or the defaults when there is none — or why it is wrong, in
    /// French, so the whole-file check (<c>PendingRequest.ParseFile</c>) can list it with the rest.
    /// </summary>
    public static (UpdateSettings? Settings, string? Error) FromConfig(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("update", out var update) ? FromObject(update) : (Default, null);

    /// <summary>The object itself — agent.json's <c>update</c>, or the whole of <c>settings.json</c>.</summary>
    public static (UpdateSettings? Settings, string? Error) FromObject(JsonElement update)
    {
        if (update.ValueKind != JsonValueKind.Object) return (null, "« update » est un objet : { \"auto\": true, \"window\": \"03:00-05:00\" }.");
        var unknown = update.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !Keys.Contains(n));
        if (unknown is not null) return (null, $"clé inconnue « update.{unknown} » (attendues : auto, window).");

        var auto = Default.Auto;
        if (update.TryGetProperty("auto", out var a))
        {
            if (a.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return (null, "« update.auto » vaut true ou false.");
            auto = a.GetBoolean();
        }
        var window = Default.Window;
        if (update.TryGetProperty("window", out var w))
        {
            var (parsed, error) = UpdateWindow.Parse(w.ValueKind == JsonValueKind.String ? w.GetString() : null);
            if (parsed is null) return (null, error);
            window = parsed;
        }
        return (new UpdateSettings(auto, window), null);
    }

    /// <summary>The applied settings, or the defaults when none were applied yet (or the file is unreadable).</summary>
    public static UpdateSettings Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return Default;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return FromObject(document.RootElement).Settings ?? Default;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return Default;
        }
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { auto = Auto, window = Window.ToString() }) + Environment.NewLine);
        File.Move(temporary, path, overwrite: true);
    }
}
