using System.Security.Cryptography;
using System.Text.Json;
using PayrollSageAgent.Core.Bindings;
using PayrollSageAgent.Core.Contract;
using PayrollSageAgent.Core.Platform;
using PayrollSageAgent.Core.Update;

namespace PayrollSageAgent.Core.Tests;

public sealed class AgentVersionTests
{
    [Fact]
    public void Versions_are_ordered_as_numbers_and_a_suffix_is_ignored()
    {
        Assert.True(AgentVersion.IsOlder("0.9.3", "0.10.0"));
        Assert.False(AgentVersion.IsOlder("0.10.0", "0.9.3"));
        Assert.False(AgentVersion.IsOlder("0.5.0-dev", "0.5.0"));
        Assert.True(AgentVersion.Same("0.5.0-dev", "0.5.0"));
        Assert.Null(AgentVersion.Parse("1.2"));
        Assert.False(AgentVersion.IsOlder("garbage", "1.0.0"));
    }
}

public sealed class UpdateWindowTests
{
    [Theory]
    [InlineData("03:00-05:00", "03:00", true)]
    [InlineData("03:00-05:00", "04:59", true)]
    [InlineData("03:00-05:00", "05:00", false)]
    [InlineData("22:00-02:00", "23:30", true)]
    [InlineData("22:00-02:00", "01:59", true)]
    [InlineData("22:00-02:00", "12:00", false)]
    public void A_window_contains_its_hours_across_midnight_too(string window, string time, bool inside)
    {
        var (parsed, error) = UpdateWindow.Parse(window);
        Assert.Null(error);
        Assert.Equal(inside, parsed!.Contains(TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(window, parsed.ToString());
    }

    [Theory]
    [InlineData("3h-5h")]
    [InlineData("03:00-03:30")]
    [InlineData("03:00-03:00")]
    [InlineData("24:00-02:00")]
    public void A_window_that_could_fall_between_two_hourly_runs_is_refused(string window)
    {
        var (parsed, error) = UpdateWindow.Parse(window);
        Assert.Null(parsed);
        Assert.Contains("update.window", error, StringComparison.Ordinal);
    }
}

public sealed class UpdateSettingsTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Without_an_update_object_the_agent_updates_itself_between_three_and_five()
    {
        var (settings, error) = UpdateSettings.FromConfig(Root("""{ "bindings": [] }"""));
        Assert.Null(error);
        Assert.Equal(UpdateSettings.Default, settings);
        Assert.True(settings!.Auto);
        Assert.Equal("03:00-05:00", settings.Window.ToString());
    }

    [Fact]
    public void The_update_object_is_read_and_its_mistakes_are_said()
    {
        Assert.False(UpdateSettings.FromConfig(Root("""{ "update": { "auto": false } }""")).Settings!.Auto);
        Assert.Contains("update.wndow", UpdateSettings.FromConfig(Root("""{ "update": { "wndow": "03:00-05:00" } }""")).Error, StringComparison.Ordinal);
        Assert.Contains("update.auto", UpdateSettings.FromConfig(Root("""{ "update": { "auto": "oui" } }""")).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_json_is_checked_whole_update_included_and_the_template_is_valid()
    {
        var (requests, errors) = PendingRequest.ParseFile("""{ "update": { "window": "3h" }, "bindings": [] }""", "agent", config: true);
        Assert.Empty(requests);
        Assert.Contains(errors, e => e.Contains("update.window", StringComparison.Ordinal));

        var (_, refused) = PendingRequest.ParseFile("""{ "update": {}, "bindings": [ { "name": "a", "code": "AAAA-BBBB-CCCC", "url": "http://x/api/agent", "sql": { "server": "s", "database": "d", "auth": "windows" } } ] }""", "agent");
        Assert.Contains(refused, e => e.Contains("« update »", StringComparison.Ordinal));

        Assert.Contains("\"update\"", AgentConfigFile.Template, StringComparison.Ordinal);
        Assert.Empty(PendingRequest.ParseFile(AgentConfigFile.Template, "agent", config: true).Errors);
    }

    [Fact]
    public void Applied_settings_survive_a_round_trip_and_an_unreadable_file_means_the_defaults()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var file = Path.Combine(folder, "settings.json");
            new UpdateSettings(false, UpdateWindow.Parse("22:00-02:00").Window!).Write(file);
            Assert.Equal(new UpdateSettings(false, UpdateWindow.Parse("22:00-02:00").Window!), UpdateSettings.Read(file));
            File.WriteAllText(file, "{ broken");
            Assert.Equal(UpdateSettings.Default, UpdateSettings.Read(file));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public sealed class UpdatePathsTests
{
    [Fact]
    public void From_a_runner_copy_the_program_folder_is_two_levels_up()
    {
        var program = Path.Combine(Path.GetTempPath(), "IGF", "PayrollSageAgent");
        Assert.Equal(program, UpdatePaths.InstallFolderOf(Path.Combine(program, "update", "runner-0.5.0") + Path.DirectorySeparatorChar));
        Assert.Equal(program, UpdatePaths.InstallFolderOf(program + Path.DirectorySeparatorChar));
        Assert.Equal(Path.Combine(program, "elsewhere", "runner-0.5.0"), UpdatePaths.InstallFolderOf(Path.Combine(program, "elsewhere", "runner-0.5.0")));
    }
}

public sealed class HealthReportTests
{
    private static readonly DateTimeOffset Since = new(2026, 10, 18, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Healthy_means_the_new_version_started_after_the_install_and_reached_the_platform()
    {
        Assert.True(new HealthReport("0.5.1", Since.AddSeconds(20), Since.AddSeconds(30)).ShowsHealthy("0.5.1", Since, needsPlatform: true));
        Assert.False(new HealthReport("0.5.0", Since.AddSeconds(20), Since.AddSeconds(30)).ShowsHealthy("0.5.1", Since, true));
        Assert.False(new HealthReport("0.5.1", Since.AddSeconds(-5), Since.AddSeconds(30)).ShowsHealthy("0.5.1", Since, true));
        Assert.False(new HealthReport("0.5.1", Since.AddSeconds(20), null).ShowsHealthy("0.5.1", Since, true));
        Assert.True(new HealthReport("0.5.1", Since.AddSeconds(20), null).ShowsHealthy("0.5.1", Since, needsPlatform: false));
    }
}

public sealed class UpdaterTests : IDisposable
{
    private static readonly DateTimeOffset Night = new(2026, 10, 19, 3, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Bytes = "MSI de test 0.5.1"u8.ToArray();

    private readonly string _folder = Directory.CreateTempSubdirectory().FullName;
    private readonly List<string> _said = [];

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class Machine : IUpdateMachine
    {
        public string? CannotInstall { get; set; }
        public string? Signer { get; set; }
        public (bool, string?) TargetSignature { get; set; } = (false, null);
        public Queue<int> InstallCodes { get; } = new();
        public List<string> Steps { get; } = [];
        public bool Running { get; set; } = true;

        public string? InstalledVersion() => "0.5.0";

        public string? OwnSigner() => Signer;

        public (bool Valid, string? Signer) Signature(string file) => TargetSignature;

        public Task<int> InstallAsync(string msi, string log, CancellationToken ct)
        {
            Steps.Add($"install {Path.GetFileName(msi)}");
            return Task.FromResult(InstallCodes.Count > 0 ? InstallCodes.Dequeue() : 0);
        }

        public Task<int> UninstallAsync(string msi, string log, CancellationToken ct)
        {
            Steps.Add($"uninstall {Path.GetFileName(msi)}");
            return Task.FromResult(0);
        }

        public bool ServiceRunning() => Running;

        public Task StartServiceAsync(CancellationToken ct)
        {
            Steps.Add("start");
            return Task.CompletedTask;
        }
    }

    private static AgentRelease Release(string version = "0.5.1", byte[]? bytes = null, bool signed = false) =>
        new(version, 0, Convert.ToHexStringLower(SHA256.HashData(bytes ?? Bytes)), (bytes ?? Bytes).Length, signed, Night.AddHours(-6));

    private static UpdateBinding Binding(string name) => new(Make.Binding(name), $"token-{name}");

    private UpdatePaths Paths => new(Path.Combine(_folder, "update"));

    private Updater Make_(FakePlatform platform, Machine machine, DateTimeOffset now, Func<HealthReport?> health) =>
        new(platform, machine, Paths, new Clock(now), line =>
        {
            _said.Add(line);
            return Task.CompletedTask;
        }, health, (_, _) => Task.CompletedTask);

    private static FakePlatform Offering(AgentRelease? latest, bool outdated = false, string? minimum = null) => new()
    {
        Updates = _ => PlatformResult<UpdateResponse>.Ok(new UpdateResponse(latest, minimum, outdated), 200),
        Installer = Bytes,
    };

    [Fact]
    public void The_decision_follows_the_window_the_minimum_the_last_failure_and_a_person()
    {
        var latest = new UpdateResponse(Release(), null, false);
        var night = TimeOnly.FromDateTime(Night.UtcDateTime);
        var noon = TimeOnly.FromDateTime(Noon.UtcDateTime);
        var on = UpdateSettings.Default;
        var off = UpdateSettings.Default with { Auto = false };

        Assert.Equal(Updater.Plan.NoRelease, Updater.Decide("0.5.0", new UpdateResponse(null, null, false), on, night, Night, false, null).Plan);
        Assert.Equal(Updater.Plan.UpToDate, Updater.Decide("0.5.1", latest, on, night, Night, false, null).Plan);
        Assert.Equal(Updater.Plan.Install, Updater.Decide("0.5.0", latest, on, night, Night, false, null).Plan);
        Assert.Equal(Updater.Plan.OutsideWindow, Updater.Decide("0.5.0", latest, on, noon, Noon, false, null).Plan);
        Assert.Equal(Updater.Plan.Disabled, Updater.Decide("0.5.0", latest, off, night, Night, false, null).Plan);
        Assert.Equal(Updater.Plan.Install, Updater.Decide("0.5.0", latest, off, noon, Noon, forced: true, null).Plan);

        var outdated = latest with { Outdated = true, MinimumVersion = "0.5.1" };
        Assert.Equal(Updater.Plan.Install, Updater.Decide("0.5.0", outdated, on, noon, Noon, false, null).Plan);
        Assert.Equal(Updater.Plan.Disabled, Updater.Decide("0.5.0", outdated, off, noon, Noon, false, null).Plan);

        var failed = new UpdateAttempt("0.5.1", ContractNames.UpdateRolledBack, Night.AddHours(-2), null);
        Assert.Equal(Updater.Plan.RecentlyFailed, Updater.Decide("0.5.0", latest, on, night, Night, false, failed).Plan);
        Assert.Equal(Updater.Plan.Install, Updater.Decide("0.5.0", latest, on, night, Night.AddHours(19), false, failed).Plan);
        Assert.Equal(Updater.Plan.Install, Updater.Decide("0.5.0", latest, on, night, Night, forced: true, failed).Plan);

        var pending = new UpdateAttempt("0.5.1", ContractNames.UpdateRebootPending, Night, null);
        Assert.Equal(Updater.Plan.RebootPending, Updater.Decide("0.5.0", latest, on, night, Night, false, pending).Plan);
    }

    [Fact]
    public async Task Outside_its_window_it_checks_through_every_binding_and_downloads_nothing()
    {
        var platform = Offering(Release());
        var machine = new Machine();

        var exit = await Make_(platform, machine, Noon, () => null)
            .RunAsync("0.5.0", [Binding("a"), Binding("b")], UpdateSettings.Default, forced: false, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Equal(["token-a", "token-b"], platform.UpdateChecks.Select(c => c.Token));
        Assert.All(platform.UpdateChecks, c => Assert.Equal(new UpdateRequest(true, "03:00-05:00", null), c.Request));
        Assert.Empty(platform.Downloads);
        Assert.Empty(machine.Steps);
        Assert.Equal("0.5.1", UpdateState.Read(Paths.State).Available);
    }

    [Fact]
    public async Task In_its_window_it_downloads_verifies_installs_waits_for_the_new_service_and_says_so()
    {
        var platform = Offering(Release());
        var machine = new Machine();
        Directory.CreateDirectory(Paths.Folder);
        File.WriteAllText(Paths.Installed, "MSI 0.5.0");

        var exit = await Make_(platform, machine, Night, () => new HealthReport("0.5.1", Night.AddSeconds(30), Night.AddSeconds(40)))
            .RunAsync("0.5.0", [Binding("a")], UpdateSettings.Default, forced: false, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Equal(["0.5.1"], platform.Downloads);
        Assert.Equal(["install PayrollSageAgent-0.5.1-x64.msi"], machine.Steps);
        Assert.Equal("MSI 0.5.0", File.ReadAllText(Paths.Previous));
        var last = UpdateState.Read(Paths.State).Last;
        Assert.Equal(("0.5.1", ContractNames.UpdateUpdated), (last!.Version, last.Outcome));
        // The attempt is reported at once, through the binding.
        Assert.Equal(last, platform.UpdateChecks[^1].Request.Last);
        Assert.False(File.Exists(Paths.Download("0.5.1")));
    }

    [Fact]
    public async Task A_file_that_is_not_the_one_announced_is_never_installed()
    {
        var platform = Offering(Release());
        platform.Installer = "autre chose"u8.ToArray();
        var machine = new Machine();

        var exit = await Make_(platform, machine, Night, () => null)
            .RunAsync("0.5.0", [Binding("a")], UpdateSettings.Default, false, TestContext.Current.CancellationToken);

        Assert.Equal(4, exit);
        Assert.Empty(machine.Steps);
        Assert.Empty(Directory.EnumerateFiles(Paths.Folder, "*.msi*"));
        Assert.Null(UpdateState.Read(Paths.State).Last);
    }

    [Fact]
    public async Task A_new_version_that_does_not_reconnect_is_rolled_back_to_the_one_before()
    {
        var platform = Offering(Release());
        var machine = new Machine();
        Directory.CreateDirectory(Paths.Folder);
        File.WriteAllText(Paths.Installed, "MSI 0.5.0");

        var exit = await Make_(platform, machine, Night, () => new HealthReport("0.5.1", Night.AddSeconds(30), null))
            .RunAsync("0.5.0", [Binding("a")], UpdateSettings.Default, false, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Equal(["install PayrollSageAgent-0.5.1-x64.msi", "uninstall PayrollSageAgent-0.5.1-x64.msi", "install previous.msi"], machine.Steps);
        var last = UpdateState.Read(Paths.State).Last!;
        Assert.Equal(ContractNames.UpdateRolledBack, last.Outcome);
        Assert.Contains("0.5.0", last.Message, StringComparison.Ordinal);
        Assert.True(last.Message!.Length <= ContractNames.MaxUpdateMessage);
    }

    [Fact]
    public async Task A_refusal_of_windows_installer_is_a_failure_and_the_old_service_is_started_again()
    {
        var platform = Offering(Release());
        var machine = new Machine();
        machine.InstallCodes.Enqueue(1603);

        var exit = await Make_(platform, machine, Night, () => null)
            .RunAsync("0.5.0", [Binding("a")], UpdateSettings.Default, false, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Equal(["install PayrollSageAgent-0.5.1-x64.msi", "start"], machine.Steps);
        Assert.Equal(ContractNames.UpdateFailed, UpdateState.Read(Paths.State).Last!.Outcome);
    }

    [Fact]
    public async Task A_signed_agent_refuses_a_version_its_publisher_did_not_sign()
    {
        var platform = Offering(Release(signed: false));
        var machine = new Machine { Signer = "CN=IGF" };

        var exit = await Make_(platform, machine, Night, () => null)
            .RunAsync("0.5.0", [Binding("a")], UpdateSettings.Default, false, TestContext.Current.CancellationToken);

        Assert.Equal(4, exit);
        Assert.Empty(machine.Steps);
        Assert.Equal(ContractNames.UpdateFailed, UpdateState.Read(Paths.State).Last!.Outcome);
    }

    [Fact]
    public async Task Off_windows_it_stops_after_the_verified_download()
    {
        var platform = Offering(Release());
        var machine = new Machine { CannotInstall = "l’installation se fait sur Windows seulement." };

        var exit = await Make_(platform, machine, Noon, () => null)
            .RunAsync("0.5.0", [Binding("a")], UpdateSettings.Default, forced: true, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Empty(machine.Steps);
        Assert.True(File.Exists(Paths.Download("0.5.1")));
        Assert.Contains(_said, line => line.Contains("Windows seulement", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_no_platform_answers_nothing_happens()
    {
        var platform = new FakePlatform { Updates = _ => PlatformResult<UpdateResponse>.Failed("réseau") };

        var exit = await Make_(platform, new Machine(), Night, () => null)
            .RunAsync("0.5.0", [Binding("a")], UpdateSettings.Default, false, TestContext.Current.CancellationToken);

        Assert.Equal(5, exit);
    }
}

public sealed class InstallerExampleTests
{
    [Fact]
    public void The_example_shipped_beside_agent_json_is_a_valid_agent_json()
    {
        var text = File.ReadAllText(Path.Combine(RepoPaths.Root, "installer", "windows", "exemple-plusieurs-liaisons.json"));
        var (requests, errors) = PendingRequest.ParseFile(text, "agent", config: true);
        Assert.Empty(errors);
        Assert.Equal(3, requests.Count);
        Assert.Equal(UpdateSettings.Default, UpdateSettings.FromConfig(JsonDocument.Parse(text).RootElement).Settings);
    }
}
