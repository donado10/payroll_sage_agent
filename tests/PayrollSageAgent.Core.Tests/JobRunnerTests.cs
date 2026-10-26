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
        var chart = new List<SageAccount>
        {
            new("42200000", "Personnel", false, 0),
            new("66000000", "Total classe 6", false, 1),
            new("66400000", null, true, 0),
        };
        var job = Make.Job(attempt: 2);
        var outcome = await Runner(new FakeReader(_ => chart)).RunAsync(job, Make.Binding("a").Sql, "pw", [], [], TestContext.Current.CancellationToken);

        Assert.Null(outcome.Fail);
        Assert.Equal(job.Id, outcome.Complete!.JobId);
        Assert.Equal(2, outcome.Complete.Attempt);
        Assert.Equal(ContractNames.SyncGlAccounts, outcome.Complete.Result.Type);
        // The type-1 account and the label-less one are SENT: skipping them is the platform's rule.
        Assert.Equal(chart, outcome.Complete.Result.Accounts);
        // No filter: the key is not sent at all — a platform older than the filter refuses it.
        Assert.Null(outcome.Complete.Result.AccountFilter);
        Assert.DoesNotContain("accountFilter", System.Text.Json.JsonSerializer.Serialize(outcome.Complete, AgentJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_filter_is_handed_to_the_reader_and_sent_with_the_chart()
    {
        var reader = new FakeReader(_ => [new("42200000", "Personnel", false, 0)]);
        var outcome = await Runner(reader).RunAsync(Make.Job(), Make.Binding("a").Sql, "pw", ["4%", "%62"], [], TestContext.Current.CancellationToken);

        Assert.Equal(["4%", "%62"], reader.LastFilter);
        Assert.Equal(["4%", "%62"], outcome.Complete!.Result.AccountFilter);
        Assert.Contains("\"accountFilter\":[\"4%\",\"%62\"]", System.Text.Json.JsonSerializer.Serialize(outcome.Complete, AgentJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_fails_and_sends_no_chart()
    {
        var outcome = await Runner(new FakeReader(_ => [])).RunAsync(Make.Job(), Make.Binding("a").Sql, "pw", ["9Z%"], [], TestContext.Current.CancellationToken);

        Assert.Null(outcome.Complete);
        Assert.Equal(ContractNames.SourceInvalid, outcome.Fail!.Error.Code);
        Assert.False(outcome.Fail.Retryable);
        Assert.Contains("9Z%", outcome.Fail.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_filter_an_empty_chart_is_still_sent()
    {
        var outcome = await Runner(new FakeReader(_ => [])).RunAsync(Make.Job(), Make.Binding("a").Sql, "pw", [], [], TestContext.Current.CancellationToken);
        Assert.NotNull(outcome.Complete);
    }

    [Fact]
    public async Task A_failure_reading_Sage_is_reported_in_the_contracts_words()
    {
        var reader = new FakeReader(_ => throw new SageReadException(ContractNames.SqlUnreachable, true, "Le serveur SQL ne répond pas."));
        var outcome = await Runner(reader).RunAsync(Make.Job(), Make.Binding("a").Sql, "pw", [], [], TestContext.Current.CancellationToken);

        Assert.Null(outcome.Complete);
        Assert.Equal(ContractNames.SqlUnreachable, outcome.Fail!.Error.Code);
        Assert.True(outcome.Fail.Retryable);
    }

    [Fact]
    public async Task A_chart_the_contract_cannot_carry_fails_as_source_invalid_and_is_not_cut()
    {
        var huge = Enumerable.Range(0, ContractNames.MaxAccounts + 1).Select(i => new SageAccount($"{i:D8}", "x", false, 0)).ToList();
        var tooMany = await Runner(new FakeReader(_ => huge)).RunAsync(Make.Job(), Make.Binding("a").Sql, "pw", [], [], TestContext.Current.CancellationToken);
        Assert.Equal(ContractNames.SourceInvalid, tooMany.Fail!.Error.Code);
        Assert.False(tooMany.Fail.Retryable);

        var twice = new List<SageAccount> { new("411", "a", false, 0), new("411", "b", false, 0) };
        var duplicate = await Runner(new FakeReader(_ => twice)).RunAsync(Make.Job(), Make.Binding("a").Sql, "pw", [], [], TestContext.Current.CancellationToken);
        Assert.Equal(ContractNames.SourceInvalid, duplicate.Fail!.Error.Code);
    }

    [Fact]
    public async Task A_job_type_this_agent_does_not_know_is_refused_without_reading_Sage()
    {
        var reader = new FakeReader(_ => []);
        var job = Make.Job() with { Type = "write_entries" };
        var outcome = await Runner(reader).RunAsync(job, Make.Binding("a").Sql, "pw", [], [], TestContext.Current.CancellationToken);

        Assert.Equal(ContractNames.Internal, outcome.Fail!.Error.Code);
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task A_tiers_sync_reads_F_COMPTET_with_the_partner_filter_and_sends_null_types()
    {
        var reader = new FakeReader(_ => [new("SAL0042", "DIOP Awa", false, 2), new("CSS", "Caisse", false, null)]);
        var job = Make.Job(type: ContractNames.SyncPartnerAccounts);
        var outcome = await Runner(reader).RunAsync(job, Make.Binding("a").Sql, "pw", ["4%"], ["SAL%", "CSS"], TestContext.Current.CancellationToken);

        Assert.Equal("F_COMPTET", reader.LastTable);
        // Each type reads ITS filter: the tiers never get accountFilter.
        Assert.Equal(["SAL%", "CSS"], reader.LastFilter);
        Assert.Equal(ContractNames.SyncPartnerAccounts, outcome.Complete!.Result.Type);
        Assert.Equal(["SAL%", "CSS"], outcome.Complete.Result.AccountFilter);
        var json = System.Text.Json.JsonSerializer.Serialize(outcome.Complete, AgentJson.Options);
        Assert.Contains("\"type\":\"sync_partner_accounts\"", json, StringComparison.Ordinal);
        // CT_Type is NULLABLE in Sage: the key is still sent, as null — the contract requires it.
        Assert.Contains("\"sageType\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chart_sync_still_reads_F_COMPTEG_with_the_account_filter()
    {
        var reader = new FakeReader(_ => [new("42200000", "Personnel", false, 0)]);
        await Runner(reader).RunAsync(Make.Job(), Make.Binding("a").Sql, "pw", ["4%"], ["SAL%"], TestContext.Current.CancellationToken);

        Assert.Equal("F_COMPTEG", reader.LastTable);
        Assert.Equal(["4%"], reader.LastFilter);
    }

    [Fact]
    public async Task A_partner_filter_that_matches_nothing_fails_and_names_partnerFilter()
    {
        var outcome = await Runner(new FakeReader(_ => [])).RunAsync(Make.Job(type: ContractNames.SyncPartnerAccounts), Make.Binding("a").Sql, "pw", [], ["9Z%"], TestContext.Current.CancellationToken);

        Assert.Null(outcome.Complete);
        Assert.Equal(ContractNames.SourceInvalid, outcome.Fail!.Error.Code);
        Assert.False(outcome.Fail.Retryable);
        Assert.Contains("partnerFilter", outcome.Fail.Error.Message, StringComparison.Ordinal);
        Assert.Contains("9Z%", outcome.Fail.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Too_many_tiers_fail_and_suggest_a_partner_filter()
    {
        var huge = Enumerable.Range(0, ContractNames.MaxAccounts + 1).Select(i => new SageAccount($"C{i:D7}", "x", false, 0)).ToList();
        var outcome = await Runner(new FakeReader(_ => huge)).RunAsync(Make.Job(type: ContractNames.SyncPartnerAccounts), Make.Binding("a").Sql, "pw", [], [], TestContext.Current.CancellationToken);

        Assert.Equal(ContractNames.SourceInvalid, outcome.Fail!.Error.Code);
        Assert.Contains("partnerFilter", outcome.Fail.Error.Message, StringComparison.Ordinal);
    }
}
