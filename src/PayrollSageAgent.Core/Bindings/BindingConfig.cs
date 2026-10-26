namespace PayrollSageAgent.Core.Bindings;

/// <summary>
/// One binding, as the agent keeps it on the server — one file per binding in the agent's home
/// (bindings/&lt;name&gt;.json). [HARD] Nothing in it ever spans two bindings, and its secrets are
/// PROTECTED (DPAPI on Windows): the platform token and the SQL password never sit in clear. Under
/// Windows authentication there is no SQL password: <see cref="ProtectedSqlPassword"/> is empty.
/// </summary>
public sealed record BindingConfig
{
    /// <summary>A local name the IT person chose — <c>[a-z0-9-]</c>, 1 to 40 — never sent to the platform.</summary>
    public required string Name { get; init; }

    /// <summary>The agent's address, as pairing confirmed it (<c>PairResponse.endpoint</c>).</summary>
    public required Uri Endpoint { get; init; }

    public required string BindingId { get; init; }
    public required string CompanyName { get; init; }
    public required string ConnectionId { get; init; }
    public required string ConnectionName { get; init; }
    public required SqlTarget Sql { get; init; }
    public required string ProtectedToken { get; init; }
    public string ProtectedSqlPassword { get; init; } = "";
    public required DateTimeOffset PairedAt { get; init; }

    /// <summary>
    /// The comptes généraux this binding syncs — agent.json › <c>accountFilter</c> (<see cref="Bindings.AccountFilter"/>);
    /// empty: the whole chart. A binding file written before 0.6.0 has none, and reads as the whole chart.
    /// [!] A list: record equality compares it by REFERENCE — compare with <see cref="Bindings.AccountFilter.Same"/>.
    /// </summary>
    public IReadOnlyList<string> AccountFilter { get; init; } = [];

    /// <summary>
    /// The comptes tiers this binding syncs — agent.json › <c>partnerFilter</c>, the same grammar; empty: every
    /// tiers of F_COMPTET. A binding file written before 0.7.0 has none, and reads as every tiers.
    /// [!] A list, like <see cref="AccountFilter"/>: compare with <see cref="Bindings.AccountFilter.Same"/>.
    /// </summary>
    public IReadOnlyList<string> PartnerFilter { get; init; } = [];

    /// <summary><c>active</c>, or <c>revoked</c> once the platform answered <c>token_invalid</c>.</summary>
    public string State { get; init; } = BindingStates.Active;

    public string? StateReason { get; init; }
}

public static class BindingStates
{
    public const string Active = "active";
    public const string Revoked = "revoked";
}
