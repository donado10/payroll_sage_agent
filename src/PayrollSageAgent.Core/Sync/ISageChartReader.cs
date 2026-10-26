using PayrollSageAgent.Core.Bindings;
using PayrollSageAgent.Core.Contract;

namespace PayrollSageAgent.Core.Sync;

/// <summary>Reads Sage's chart of accounts and comptes tiers for ONE binding — the CODE only reads (PayrollSageAgent.Sage).</summary>
public interface ISageChartReader
{
    /// <summary>
    /// F_COMPTEG, mapped: number and label trimmed, an empty label as null — the whole chart, or, when
    /// <paramref name="accountFilter"/> is not empty, the accounts whose TRIMMED number is <c>LIKE</c> one of
    /// its patterns (agent.json › <c>accountFilter</c>, already checked by <see cref="AccountFilter"/>).
    /// </summary>
    Task<IReadOnlyList<SageAccount>> ReadChartAsync(SqlTarget target, string password, IReadOnlyList<string> accountFilter, CancellationToken ct);

    /// <summary>
    /// F_COMPTET, mapped the same way — every tiers (client, fournisseur, salarié, autre), or, when
    /// <paramref name="partnerFilter"/> is not empty, those whose TRIMMED <c>CT_Num</c> is <c>LIKE</c> one of its
    /// patterns (agent.json › <c>partnerFilter</c>). <c>CT_Sommeil</c> and <c>CT_Type</c> are NULLABLE there:
    /// NULL reads as not dormant, and a null <see cref="SageAccount.SageType"/>.
    /// </summary>
    Task<IReadOnlyList<SageAccount>> ReadPartnersAsync(SqlTarget target, string password, IReadOnlyList<string> partnerFilter, CancellationToken ct);

    /// <summary>
    /// Before a binding is created: the login connects and can read F_COMPTEG — or throws
    /// <see cref="SageReadException"/> with the reason. What ELSE it may do is returned, not refused: any
    /// login is accepted, <c>sa</c> included (the user's decision of 2026-10-17 — the passation comptable
    /// will write into Sage), and the person adding it is told.
    /// </summary>
    Task<SqlLoginRights> CheckAsync(SqlTarget target, string password, CancellationToken ct);
}

/// <summary>What a binding's login may do beyond reading F_COMPTEG — said to IT, never a refusal.</summary>
public sealed record SqlLoginRights(bool CanWrite, bool SeesAllDatabases);

/// <summary>
/// A failure reading Sage, already named in the contract's words (platform/protocol.md › « Failures »).
/// <see cref="Exception.Message"/> is the French sentence that may travel to the platform;
/// <see cref="Detail"/> is SQL Server's own error — number and text — which STAYS on this machine: on the
/// console of the person who typed the command, and in the agent's log file. It carries no secret (SQL
/// Server never puts a password in an error), and without it « ne répond pas » could not be diagnosed.
/// </summary>
public sealed class SageReadException(string code, bool retryable, string message, Exception? inner = null, string? detail = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public string? Detail { get; } = detail;
}
