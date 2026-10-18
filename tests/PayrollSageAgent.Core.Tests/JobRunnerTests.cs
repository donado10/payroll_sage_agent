using PayrollSageAgent.Core.Contract;
using PayrollSageAgent.Core.Jobs;
using PayrollSageAgent.Core.Sync;

namespace PayrollSageAgent.Core.Tests;

public sealed class JobRunnerTests
{
    private static JobRunner Runner(ISageChartReader reader) => new(reader, new SemaphoreSlim(2), TimeProvider.System);

    [Fact]
    public async Task A_sync_sends_the_chart_as_read_and_decides_nothing()
    {
        var chart = new List<SageGlAccount>
        {
            new("42200000", "Personnel", false, 0),
            new("66000000", "Total classe 6", false, 1),
            new("66400000", null, true, 0),
        };
        var job = Make.Job(attempt: 2);
        var outcome = await Runner(new FakeReader(_ => chart)).RunAsync(job, Make.Binding("a").Sql, "pw", TestContext.Current.CancellationToken);

        Assert.Null(outcome.Fail);
        Assert.Equal(job.Id, outcome.Complete!.JobId);
        Assert.Equal(2, outcome.Complete.Attempt);
        Assert.Equal(ContractNames.SyncGlAccounts, outcome.Complete.Result.Type);
        // The type-1 account and the label-less one are SENT: skipping them is the platform's rule.
        Assert.Equal(chart, outcome.Complete.Result.Accounts);
    }

    [Fact]
    public async Task A_failure_reading_Sage_is_reported_in_the_contracts_words()
    {
        var reader = new FakeReader(_ => throw new SageReadException(ContractNames.SqlUnreachable, true, "Le serveur SQL ne répond pas."));
        var outcome = await Runner(reader).RunAsync(Make.Job(), Make.Binding("a").Sql, "pw", TestContext.Current.CancellationToken);

        Assert.Null(outcome.Complete);
        Assert.Equal(ContractNames.SqlUnreachable, outcome.Fail!.Error.Code);
        Assert.True(outcome.Fail.Retryable);
    }

    [Fact]
    public async Task A_chart_the_contract_cannot_carry_fails_as_source_invalid_and_is_not_cut()
    {
        var huge = Enumerable.Range(0, ContractNames.MaxAccounts + 1).Select(i => new SageGlAccount($"{i:D8}", "x", false, 0)).ToList();
        var tooMany = await Runner(new FakeReader(_ => huge)).RunAsync(Make.Job(), Make.Binding("a").Sql, "pw", TestContext.Current.CancellationToken);
        Assert.Equal(ContractNames.SourceInvalid, tooMany.Fail!.Error.Code);
        Assert.False(tooMany.Fail.Retryable);

        var twice = new List<SageGlAccount> { new("411", "a", false, 0), new("411", "b", false, 0) };
        var duplicate = await Runner(new FakeReader(_ => twice)).RunAsync(Make.Job(), Make.Binding("a").Sql, "pw", TestContext.Current.CancellationToken);
        Assert.Equal(ContractNames.SourceInvalid, duplicate.Fail!.Error.Code);
    }

    [Fact]
    public async Task A_job_type_this_agent_does_not_know_is_refused_without_reading_Sage()
    {
        var reader = new FakeReader(_ => []);
        var job = Make.Job() with { Type = "write_entries" };
        var outcome = await Runner(reader).RunAsync(job, Make.Binding("a").Sql, "pw", TestContext.Current.CancellationToken);

        Assert.Equal(ContractNames.Internal, outcome.Fail!.Error.Code);
        Assert.Equal(0, reader.Reads);
    }
}
