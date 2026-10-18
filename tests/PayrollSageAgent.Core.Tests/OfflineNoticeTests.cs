using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PayrollSageAgent.Core.Contract;
using PayrollSageAgent.Core.Platform;

namespace PayrollSageAgent.Core.Tests;

/// <summary>« Je m'arrête » — contract step 11: every binding told, none able to hold the stop.</summary>
public sealed class OfflineNoticeTests
{
    private static readonly Uri Endpoint = new("https://agents.example.com/api/agent");

    private sealed class Lines : ILogger
    {
        public List<string> Written { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Written) Written.Add(formatter(state, exception));
        }
    }

    private static RunningBinding Running(string name) => new(name, Endpoint, $"igfa_token_of_{name}");

    [Fact]
    public async Task Every_running_binding_is_told()
    {
        var platform = new FakePlatform();
        var log = new Lines();

        var heard = await OfflineNotice.SendAsync(platform, [Running("a"), Running("b")], log);

        Assert.Equal(2, heard);
        Assert.Equal(["igfa_token_of_a", "igfa_token_of_b"], platform.Stopped.Order(StringComparer.Ordinal));
        Assert.Contains(log.Written, l => l.Contains("2 liaison(s) sur 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refusal_or_a_dead_network_is_said_once_and_the_others_are_still_told()
    {
        var platform = new FakePlatform
        {
            Offline = token => token switch
            {
                "igfa_token_of_revoked" => PlatformResult<bool>.Refused(new PlatformError(ContractNames.TokenInvalid, "…"), 401),
                "igfa_token_of_offline" => PlatformResult<bool>.Failed("HttpRequestException"),
                _ => PlatformResult<bool>.Ok(true, 204),
            },
        };
        var log = new Lines();

        var heard = await OfflineNotice.SendAsync(platform, [Running("revoked"), Running("offline"), Running("ok")], log);

        Assert.Equal(1, heard);
        Assert.Equal(3, platform.Stopped.Count);
        Assert.Contains(log.Written, l => l.Contains("« revoked »", StringComparison.Ordinal) && l.Contains(ContractNames.TokenInvalid, StringComparison.Ordinal));
        Assert.Contains(log.Written, l => l.Contains("« offline »", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_call_that_never_answers_cannot_hold_the_stop_past_the_budget()
    {
        var platform = new FakePlatform { Offline = token => token == "igfa_token_of_hung" ? null : PlatformResult<bool>.Ok(true, 204) };
        var log = new Lines();
        var clock = Stopwatch.StartNew();

        var heard = await OfflineNotice.SendAsync(platform, [Running("hung"), Running("ok")], log, TimeSpan.FromMilliseconds(200));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"the stop waited {clock.Elapsed}");
        Assert.Equal(1, heard);
        Assert.Contains(log.Written, l => l.Contains("« hung »", StringComparison.Ordinal) && l.Contains("délai dépassé", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_binding_no_call_and_no_line()
    {
        var platform = new FakePlatform();
        var log = new Lines();

        Assert.Equal(0, await OfflineNotice.SendAsync(platform, [], log));
        Assert.Empty(platform.Stopped);
        Assert.Empty(log.Written);
    }

    [Fact]
    public async Task No_token_ever_reaches_the_log()
    {
        var platform = new FakePlatform { Offline = _ => PlatformResult<bool>.Failed("HttpRequestException") };
        var log = new Lines();

        await OfflineNotice.SendAsync(platform, [Running("a")], log);

        Assert.DoesNotContain(log.Written, l => l.Contains("igfa_", StringComparison.Ordinal));
    }
}
