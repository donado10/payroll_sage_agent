using System.Text.Json;
using PayrollSageAgent.Core.Contract;

namespace PayrollSageAgent.Core.Update;

/// <summary>
/// What the SERVICE says of itself, in <c>health.json</c> at the root of its folder: its version, when it
/// started, and when it last reached the platform. The updater reads it after an install — the new version
/// is kept only once it has started AND reached the platform (when it has bindings), else it is rolled back.
///
/// The service writes it (its account may write its folder); the updater, as SYSTEM, only READS it — a file
/// the service account controls decides at most a rollback, never what is installed.
/// </summary>
public sealed record HealthReport(string Version, DateTimeOffset StartedAt, DateTimeOffset? PlatformReachedAt)
{
    public bool ShowsHealthy(string version, DateTimeOffset since, bool needsPlatform) =>
        AgentVersion.Same(Version, version)
        && StartedAt >= since
        && (!needsPlatform || PlatformReachedAt is { } reached && reached >= StartedAt);

    public static HealthReport? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<HealthReport>(File.ReadAllText(path), AgentJson.Options) : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string path)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, AgentJson.Options));
        File.Move(temporary, path, overwrite: true);
    }
}
