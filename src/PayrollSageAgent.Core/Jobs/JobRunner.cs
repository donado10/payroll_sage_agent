using PayrollSageAgent.Core.Bindings;
using PayrollSageAgent.Core.Contract;
using PayrollSageAgent.Core.Sync;

namespace PayrollSageAgent.Core.Jobs;

/// <summary>What running one job came to: the body of <c>complete</c>, or the body of <c>fail</c>.</summary>
/// <summary>What to send — and, for a failure, the local detail that is logged and never sent.</summary>
public sealed record JobOutcome(CompleteRequest? Complete, FailRequest? Fail, string? LocalDetail = null);

/// <summary>
/// Runs ONE claimed job for ONE binding. The agent decides nothing about the data: it reads, maps and
/// sends; what is skipped or deactivated is the platform's rule (platform/protocol.md › « sync_gl_accounts »,
/// « sync_partner_accounts »). The one choice is IT's, not the agent's: the binding's filter for the job's
/// type — <c>accountFilter</c> for the chart, <c>partnerFilter</c> for the comptes tiers — narrows what is
/// READ, and is sent with the result so that the platform removes what lies outside it.
///
/// [HARD] A GUEST ON A SHARED SERVER: every read of Sage waits on <paramref name="sqlGate"/>, one gate
/// for the whole agent, so N bindings never put N queries on the instance at once.
/// </summary>
public sealed class JobRunner(ISageChartReader reader, SemaphoreSlim sqlGate, TimeProvider clock)
{
    public async Task<JobOutcome> RunAsync(AgentJob job, SqlTarget target, string sqlPassword,
        IReadOnlyList<string> accountFilter, IReadOnlyList<string> partnerFilter, CancellationToken ct)
    {
        if (job.Type is not (ContractNames.SyncGlAccounts or ContractNames.SyncPartnerAccounts))
        {
            return Fail(job, ContractNames.Internal, false, $"Cet agent ne sait pas faire « {job.Type} » : mettez-le à jour.");
        }
        var partners = job.Type == ContractNames.SyncPartnerAccounts;
        var filter = partners ? partnerFilter : accountFilter;

        IReadOnlyList<SageAccount> accounts;
        await sqlGate.WaitAsync(ct);
        try
        {
            accounts = partners
                ? await reader.ReadPartnersAsync(target, sqlPassword, filter, ct)
                : await reader.ReadChartAsync(target, sqlPassword, filter, ct);
        }
        catch (SageReadException error)
        {
            return Fail(job, error.Code, error.Retryable, error.Message, error.Detail);
        }
        finally
        {
            sqlGate.Release();
        }

        // A filter that matched nothing is a mistake in agent.json, never « delete every account » — the
        // platform would refuse it too; said here, in words IT can act on.
        if (filter.Count > 0 && accounts.Count == 0)
        {
            return Fail(job, ContractNames.SourceInvalid, false, partners
                ? $"Aucun compte tiers de Sage ne correspond au filtre « {AccountFilter.Describe(filter)} » (agent.json › partnerFilter) : rien n’a été envoyé, les comptes tiers de la plateforme sont inchangés."
                : $"Aucun compte de Sage ne correspond au filtre « {AccountFilter.Describe(filter)} » (agent.json › accountFilter) : rien n’a été envoyé, le plan comptable de la plateforme est inchangé.");
        }

        if (accounts.Count > ContractNames.MaxAccounts)
        {
            return Fail(job, ContractNames.SourceInvalid, false, !partners
                ? $"Le plan comptable de Sage compte {accounts.Count} comptes : la plateforme en reprend {ContractNames.MaxAccounts} au plus."
                : $"Sage compte {accounts.Count} comptes tiers (ses clients compris) : la plateforme en reprend {ContractNames.MaxAccounts} au plus. Ne gardez que ceux de la paie avec un filtre — agent.json › partnerFilter, par exemple [\"SAL%\", \"IPRES\", \"CSS\"].");
        }

        var duplicate = accounts.GroupBy(a => a.Number, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            return Fail(job, ContractNames.SourceInvalid, false, partners
                ? $"Le numéro de compte tiers {duplicate.Key} figure plusieurs fois dans Sage."
                : $"Le numéro de compte {duplicate.Key} figure plusieurs fois dans Sage.");
        }

        var result = new SyncAccountsResult(job.Type, clock.GetUtcNow(), accounts, filter.Count > 0 ? filter : null);
        return new JobOutcome(new CompleteRequest(job.Id, job.Attempt, result), null);
    }

    private static JobOutcome Fail(AgentJob job, string code, bool retryable, string message, string? localDetail = null) =>
        new(null, new FailRequest(job.Id, job.Attempt, new JobError(code, Truncate(message)), retryable), localDetail);

    // The contract caps a message at 500 characters; the detail stays in the local log.
    private static string Truncate(string message) => message.Length <= 500 ? message : message[..497] + "…";
}
