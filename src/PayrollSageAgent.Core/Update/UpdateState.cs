using System.Text.Json;
using PayrollSageAgent.Core.Contract;

namespace PayrollSageAgent.Core.Update;

/// <summary>
/// What the updater remembers between two runs, in the update folder's <c>state.json</c>: its LAST attempt —
/// reported to the platform at every check (<c>UpdateRequest.last</c>), and what keeps a version that failed
/// from being retried before 20 hours —, when it last checked, and the version then available.
/// </summary>
public sealed record UpdateState(UpdateAttempt? Last = null, DateTimeOffset? CheckedAt = null, string? Available = null)
{
    public static UpdateState Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(path), AgentJson.Options) ?? new UpdateState() : new UpdateState();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new UpdateState();
        }
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, AgentJson.Options) + Environment.NewLine);
        File.Move(temporary, path, overwrite: true);
    }
}
