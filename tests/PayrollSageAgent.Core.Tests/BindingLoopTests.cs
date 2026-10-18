using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PayrollSageAgent.Core.Bindings;
using PayrollSageAgent.Core.Contract;
using PayrollSageAgent.Core.Jobs;
using PayrollSageAgent.Core.Platform;
using PayrollSageAgent.Core.Sync;

namespace PayrollSageAgent.Core.Tests;

public sealed class BindingLoopTests
{
    private sealed class Events : IBindingEvents
    {
        public List<string> Revoked { get; } = [];
        public int Reached { get; private set; }

        public void PlatformReached() => Reached++;

        public Task RevokedAsync(BindingConfig binding, string reason, CancellationToken ct)
        {
            Revoked.Add(binding.Name);
            return Task.CompletedTask;
        }
    }

    /// <summary>A logger that keeps every line, to prove what never reaches a log.</summary>
    private sealed class Capture : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private static Func<TimeSpan, CancellationToken, Task> StopAfter(int waits, CancellationTokenSource stop)
    {
        var count = 0;
        return (_, _) =>
        {
            if (++count >= waits) stop.Cancel();
            return Task.CompletedTask;
        };
    }

    [Fact]
    public async Task A_claimed_sync_is_read_and_completed_with_its_own_token()
    {
        var platform = new FakePlatform();
        platform.Claims["token-a"] = new([PlatformResult<ClaimResponse>.Ok(new ClaimResponse(Make.Job(), 15), 200)]);
        var reader = new FakeReader(_ => [new SageGlAccount("411", "Clients", false, 0)]);
        using var stop = new CancellationTokenSource();
        var loop = new BindingLoop(Make.Binding("a"), "token-a", "pw", platform, new JobRunner(reader, new SemaphoreSlim(2), TimeProvider.System),
            new Events(), NullLogger.Instance, StopAfter(2, stop));

        await loop.RunAsync(stop.Token);

        var (token, request) = Assert.Single(platform.Completed);
        Assert.Equal("token-a", token);
        Assert.Single(request.Result.Accounts);
    }

    [Fact]
    public async Task A_revoked_token_stops_the_binding_and_says_so()
    {
        var platform = new FakePlatform();
        platform.Claims["token-a"] = new([PlatformResult<ClaimResponse>.Refused(new PlatformError(ContractNames.TokenInvalid, "…"), 401)]);
        var events = new Events();
        var loop = new BindingLoop(Make.Binding("a"), "token-a", "pw", platform, new JobRunner(new FakeReader(_ => []), new SemaphoreSlim(2), TimeProvider.System),
            events, NullLogger.Instance, (_, _) => Task.CompletedTask);

        await loop.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["a"], events.Revoked);
    }

    [Fact]
    public async Task One_binding_whose_database_is_down_leaves_the_other_syncing()
    {
        var platform = new FakePlatform();
        platform.Claims["token-a"] = new([PlatformResult<ClaimResponse>.Ok(new ClaimResponse(Make.Job(), 15), 200)]);
        platform.Claims["token-b"] = new([PlatformResult<ClaimResponse>.Ok(new ClaimResponse(Make.Job(), 15), 200)]);
        var reader = new FakeReader(target => target.Database == "DB_a"
            ? throw new SageReadException(ContractNames.SqlUnreachable, true, "Le serveur SQL ne répond pas.")
            : [new SageGlAccount("411", "Clients", false, 0)]);
        var runner = new JobRunner(reader, new SemaphoreSlim(2), TimeProvider.System);

        using var stopA = new CancellationTokenSource();
        using var stopB = new CancellationTokenSource();
        var a = new BindingLoop(Make.Binding("a"), "token-a", "pw", platform, runner, new Events(), NullLogger.Instance, StopAfter(2, stopA));
        var b = new BindingLoop(Make.Binding("b"), "token-b", "pw", platform, runner, new Events(), NullLogger.Instance, StopAfter(2, stopB));
        await Task.WhenAll(a.RunAsync(stopA.Token), b.RunAsync(stopB.Token));

        Assert.Equal("token-a", Assert.Single(platform.Failed).Token);
        Assert.Equal("token-b", Assert.Single(platform.Completed).Token);
    }

    [Fact]
    public async Task No_log_line_carries_the_token_the_password_or_an_account()
    {
        var platform = new FakePlatform();
        platform.Claims["igfa_TOKEN_VALUE"] = new([
            PlatformResult<ClaimResponse>.Failed("réseau (ConnectionError)"),
            PlatformResult<ClaimResponse>.Ok(new ClaimResponse(Make.Job(), 15), 200),
        ]);
        var reader = new FakeReader(_ => [new SageGlAccount("41100000", "Clients Dakar", false, 0)]);
        var capture = new Capture();
        using var stop = new CancellationTokenSource();
        var loop = new BindingLoop(Make.Binding("a"), "igfa_TOKEN_VALUE", "SQL_PASSWORD_VALUE", platform,
            new JobRunner(reader, new SemaphoreSlim(2), TimeProvider.System), new Events(), capture, StopAfter(3, stop));

        await loop.RunAsync(stop.Token);

        Assert.NotEmpty(capture.Lines);
        Assert.DoesNotContain(capture.Lines, l => l.Contains("TOKEN_VALUE", StringComparison.Ordinal)
            || l.Contains("PASSWORD_VALUE", StringComparison.Ordinal)
            || l.Contains("41100000", StringComparison.Ordinal)
            || l.Contains("Clients Dakar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_outdated_build_keeps_its_binding_and_calls_again_in_five_minutes()
    {
        var platform = new FakePlatform();
        platform.Claims["token-a"] = new([PlatformResult<ClaimResponse>.Refused(new PlatformError(ContractNames.AgentOutdated, "…", MinimumVersion: "0.5.0"), 426)]);
        var waits = new List<TimeSpan>();
        using var stop = new CancellationTokenSource();
        var events = new Events();
        var loop = new BindingLoop(Make.Binding("a"), "token-a", "pw", platform, new JobRunner(new FakeReader(_ => []), new SemaphoreSlim(2), TimeProvider.System),
            events, NullLogger.Instance, (wait, _) =>
            {
                waits.Add(wait);
                stop.Cancel();
                return Task.CompletedTask;
            });

        await loop.RunAsync(stop.Token);

        Assert.Empty(events.Revoked);
        Assert.Equal(TimeSpan.FromMinutes(5), Assert.Single(waits));
        Assert.Equal(1, events.Reached);
    }
}
