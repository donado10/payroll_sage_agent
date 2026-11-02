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
/// « sync_partner_accounts », « sync_journals »). The one choice is IT's, not the agent's: the binding's
/// filter for the job's type — <c>accountFilter</c> for the chart, <c>partnerFilter</c> for the comptes tiers,
/// <c>journalFilter</c> for the journals — narrows what is READ, and is sent with the result so that the
/// platform removes what lies outside it.
///
/// [HARD] A GUEST ON A SHARED SERVER: every read of Sage waits on <paramref name="sqlGate"/>, one gate
/// for the whole agent, so N bindings never put N queries on the instance at once.
/// </summary>
public sealed class JobRunner(ISageChartReader reader, SemaphoreSlim sqlGate, TimeProvider clock)
{
    /// <summary>What each job type reads, and how it is said when it goes wrong.</summary>
    private sealed record Kind(string FilterKey, string NoneMatch, string TooMany, string Duplicate);

    private static readonly Dictionary<string, Kind> Kinds = new(StringComparer.Ordinal)
    {
        [ContractNames.SyncGlAccounts] = new("accountFilter",
            "Aucun compte de Sage ne correspond au filtre « {0} » (agent.json › accountFilter) : rien n’a été envoyé, le plan comptable de la plateforme est inchangé.",
            "Le plan comptable de Sage compte {0} comptes : la plateforme en reprend {1} au plus.",
            "Le numéro de compte {0} figure plusieurs fois dans Sage."),
        [ContractNames.SyncPartnerAccounts] = new("partnerFilter",
            "Aucun compte tiers de Sage ne correspond au filtre « {0} » (agent.json › partnerFilter) : rien n’a été envoyé, les comptes tiers de la plateforme sont inchangés.",
            "Sage compte {0} comptes tiers (ses clients compris) : la plateforme en reprend {1} au plus. Ne gardez que ceux de la paie avec un filtre — agent.json › partnerFilter, par exemple [\"SAL%\", \"IPRES\", \"CSS\"].",
            "Le numéro de compte tiers {0} figure plusieurs fois dans Sage."),
        [ContractNames.SyncJournals] = new("journalFilter",
            "Aucun journal de Sage ne correspond au filtre « {0} » (agent.json › journalFilter) : rien n’a été envoyé, les journaux de la plateforme sont inchangés.",
            "Sage compte {0} journaux : la plateforme en reprend {1} au plus.",
            "Le code de journal {0} figure plusieurs fois dans Sage."),
    };

    public async Task<JobOutcome> RunAsync(AgentJob job, SqlTarget target, string sqlPassword,
        IReadOnlyList<string> accountFilter, IReadOnlyList<string> partnerFilter, IReadOnlyList<string> journalFilter, CancellationToken ct)
    {
        if (!Kinds.TryGetValue(job.Type, out var kind))
        {
            return Fail(job, ContractNames.Internal, false, $"Cet agent ne sait pas faire « {job.Type} » : mettez-le à jour.");
        }
        var filter = job.Type switch
        {
            ContractNames.SyncPartnerAccounts => partnerFilter,
            ContractNames.SyncJournals => journalFilter,
            _ => accountFilter,
        };

        IReadOnlyList<SageAccount> accounts = [];
        IReadOnlyList<SageJournal> journals = [];
        await sqlGate.WaitAsync(ct);
        try
        {
            switch (job.Type)
            {
                case ContractNames.SyncJournals:
                    journals = await reader.ReadJournalsAsync(target, sqlPassword, filter, ct);
                    break;
                case ContractNames.SyncPartnerAccounts:
                    accounts = await reader.ReadPartnersAsync(target, sqlPassword, filter, ct);
                    break;
                default:
                    accounts = await reader.ReadChartAsync(target, sqlPassword, filter, ct);
                    break;
            }
        }
        catch (SageReadException error)
        {
            return Fail(job, error.Code, error.Retryable, error.Message, error.Detail);
        }
        finally
        {
            sqlGate.Release();
        }

        var count = job.Type == ContractNames.SyncJournals ? journals.Count : accounts.Count;

        // A filter that matched nothing is a mistake in agent.json, never « delete every account » — the
        // platform would refuse it too; said here, in words IT can act on.
        if (filter.Count > 0 && count == 0)
        {
            return Fail(job, ContractNames.SourceInvalid, false, string.Format(System.Globalization.CultureInfo.InvariantCulture, kind.NoneMatch, AccountFilter.Describe(filter)));
        }

        if (count > ContractNames.MaxAccounts)
        {
            return Fail(job, ContractNames.SourceInvalid, false, string.Format(System.Globalization.CultureInfo.InvariantCulture, kind.TooMany, count, ContractNames.MaxAccounts));
        }

        // A journal code is unique in Sage WITHOUT case (French_CI_AS), and so it is on the platform.
        var duplicate = job.Type == ContractNames.SyncJournals
            ? journals.GroupBy(j => j.Code, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1)?.Key
            : accounts.GroupBy(a => a.Number, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            return Fail(job, ContractNames.SourceInvalid, false, string.Format(System.Globalization.CultureInfo.InvariantCulture, kind.Duplicate, duplicate));
        }

        var sent = filter.Count > 0 ? filter : null;
        SyncResult result = job.Type == ContractNames.SyncJournals
            ? new SyncJournalsResult(job.Type, clock.GetUtcNow(), journals, sent)
            : new SyncAccountsResult(job.Type, clock.GetUtcNow(), accounts, sent);
        return new JobOutcome(new CompleteRequest(job.Id, job.Attempt, result), null);
    }

    private static JobOutcome Fail(AgentJob job, string code, bool retryable, string message, string? localDetail = null) =>
        new(null, new FailRequest(job.Id, job.Attempt, new JobError(code, Truncate(message)), retryable), localDetail);

    // The contract caps a message at 500 characters; the detail stays in the local log.
    private static string Truncate(string message) => message.Length <= 500 ? message : message[..497] + "…";
}
