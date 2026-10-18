using Microsoft.Extensions.Logging;
using PayrollSageAgent.Core.Logging;

namespace PayrollSageAgent.Core.Tests;

public sealed class FileLoggerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "agent-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_line_goes_to_the_file_of_its_day_with_its_level_and_short_category()
    {
        var clock = new SteppedClock(new DateTimeOffset(2026, 10, 17, 18, 30, 22, TimeSpan.Zero));
        using (var provider = new FileLoggerProvider(_folder, clock))
        {
            provider.CreateLogger("PayrollSageAgent.Core.Jobs.BindingLoop").LogWarning("Liaison « a » : échec signalé.");
        }

        var text = File.ReadAllText(Path.Combine(_folder, "agent-2026-10-17.log"));
        Assert.Equal("2026-10-17 18:30:22 +00:00 [WRN] BindingLoop : Liaison « a » : échec signalé." + Environment.NewLine, text);
    }

    [Fact]
    public void A_new_day_opens_a_new_file_and_files_past_thirty_days_are_removed_by_their_name_only()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "agent-2026-09-01.log"), "old");
        File.WriteAllText(Path.Combine(_folder, "agent-2026-09-18.log"), "kept: the 30th day back");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not ours");
        var clock = new SteppedClock(new DateTimeOffset(2026, 10, 16, 23, 59, 59, TimeSpan.Zero));

        using (var provider = new FileLoggerProvider(_folder, clock))
        {
            var logger = provider.CreateLogger("Test");
            logger.LogInformation("avant minuit");
            clock.Now = clock.Now.AddSeconds(2);
            logger.LogInformation("après minuit");
        }

        Assert.Contains("avant minuit", File.ReadAllText(Path.Combine(_folder, "agent-2026-10-16.log")), StringComparison.Ordinal);
        Assert.Contains("après minuit", File.ReadAllText(Path.Combine(_folder, "agent-2026-10-17.log")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_folder, "agent-2026-09-01.log")));
        Assert.True(File.Exists(Path.Combine(_folder, "agent-2026-09-18.log")));
        Assert.True(File.Exists(Path.Combine(_folder, "notes.txt")));
    }

    [Fact]
    public void A_folder_that_cannot_be_written_drops_the_line_and_throws_nothing()
    {
        // A FILE where the folder should be: every open fails, as an unwritable folder would.
        File.WriteAllText(_folder, "in the way");
        try
        {
            using var provider = new FileLoggerProvider(_folder, TimeProvider.System);
            var error = Record.Exception(() => provider.CreateLogger("Test").LogError("rien ne s’écrit"));
            Assert.Null(error);
        }
        finally
        {
            File.Delete(_folder);
        }
    }

    [Fact]
    public void The_service_account_sid_is_computed_as_windows_does()
    {
        // TrustedInstaller's SID is published; the same derivation names NT SERVICE\PayrollSageAgent.
        Assert.Equal("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", ServiceIdentity.Sid("TrustedInstaller"));
        Assert.Equal(ServiceIdentity.Sid("PAYROLLSAGEAGENT"), ServiceIdentity.Sid(ServiceIdentity.Name));
    }

    private sealed class SteppedClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
