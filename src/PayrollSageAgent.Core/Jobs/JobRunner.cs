using PayrollSageAgent.Core.Bindings;
using PayrollSageAgent.Core.Contract;
using PayrollSageAgent.Core.Sync;

namespace PayrollSageAgent.Core.Jobs;

/// <summary>What running one job came to: the body of <c>complete</c>, or the body of <c>fail</c>.</summary>
/// <summary>What to send — and, for a failure, the local detail that is logged and never sent.</summary>
public sealed record JobOutcome(CompleteRequest? Complete, FailRequest? Fail, string? LocalDetail = null);

/// <summary>
/// Runs ONE claimed job for ONE binding. The agent decides nothing about the data: it reads, maps and
/// sends; what is skipped or deactivated is the platform's rule (platform/protocol.md › « sync_gl_accounts »).
///
/// [HARD] A GUEST ON A SHARED SERVER: every read of Sage waits on <paramref name="sqlGate"/>, one gate
/// for the whole agent, so N bindings never put N queries on the instance at once.
/// </summary>
public sealed class JobRunner(ISageChartReader reader, SemaphoreSlim sqlGate, TimeProvider clock)
{
    public async Task<JobOutcome> RunAsync(AgentJob job, SqlTarget target, string sqlPassword, CancellationToken ct)
    {
        if (job.Type != ContractNames.SyncGlAccounts)
        {
            return Fail(job, ContractNames.Internal, false, $"Cet agent ne sait pas faire « {job.Type} » : mettez-le à jour.");
        }

        IReadOnlyList<SageGlAccount> accounts;
        await sqlGate.WaitAsync(ct);
        try
        {
            accounts = await reader.ReadChartAsync(target, sqlPassword, ct);
        }
        catch (SageReadException error)
        {
            return Fail(job, error.Code, error.Retryable, error.Message, error.Detail);
        }
        finally
        {
            sqlGate.Release();
        }

        if (accounts.Count > ContractNames.MaxAccounts)
        {
            return Fail(job, ContractNames.SourceInvalid, false,
                $"Le plan comptable de Sage compte {accounts.Count} comptes : la plateforme en reprend {ContractNames.MaxAccounts} au plus.");
        }

        var duplicate = accounts.GroupBy(a => a.Number, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            return Fail(job, ContractNames.SourceInvalid, false,
                $"Le numéro de compte {duplicate.Key} figure plusieurs fois dans Sage.");
        }

        var result = new SyncGlAccountsResult(ContractNames.SyncGlAccounts, clock.GetUtcNow(), accounts);
        return new JobOutcome(new CompleteRequest(job.Id, job.Attempt, result), null);
    }

    private static JobOutcome Fail(AgentJob job, string code, bool retryable, string message, string? localDetail = null) =>
        new(null, new FailRequest(job.Id, job.Attempt, new JobError(code, Truncate(message)), retryable), localDetail);

    // The contract caps a message at 500 characters; the detail stays in the local log.
    private static string Truncate(string message) => message.Length <= 500 ? message : message[..497] + "…";
}
