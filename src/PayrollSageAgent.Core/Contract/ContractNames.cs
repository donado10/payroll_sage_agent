namespace PayrollSageAgent.Core.Contract;

/// <summary>The contract's closed lists, as strings — see <see cref="PairRequest"/> for why not enums.</summary>
public static class ContractNames
{
    public const string ProtocolHeader = "X-Agent-Protocol";
    public const string VersionHeader = "X-Agent-Version";
    public const string TokenPrefix = "igfa_";

    public const string SyncGlAccounts = "sync_gl_accounts";
    /// <summary>The comptes tiers of F_COMPTET (contract step 13, 0.7.0).</summary>
    public const string SyncPartnerAccounts = "sync_partner_accounts";

    // The platform's refusals.
    public const string RequestInvalid = "request_invalid";
    public const string TokenInvalid = "token_invalid";
    public const string ProtocolUnsupported = "protocol_unsupported";
    public const string CodeInvalid = "code_invalid";
    public const string ConnectionNotSage = "connection_not_sage";
    public const string JobNotClaimed = "job_not_claimed";
    public const string ResultTooLarge = "result_too_large";
    public const string RateLimited = "rate_limited";
    public const string AgentOutdated = "agent_outdated";
    public const string ReleaseNotFound = "release_not_found";

    // The outcome of an update attempt (UPDATE_OUTCOMES).
    public const string UpdateUpdated = "updated";
    public const string UpdateFailed = "failed";
    public const string UpdateRolledBack = "rolled_back";
    public const string UpdateRebootPending = "reboot_pending";

    /// <summary>The download's media type, and the header that repeats its SHA-256 (AGENT_DOWNLOAD).</summary>
    public const string InstallerMediaType = "application/x-msi";
    public const string ReleaseSha256Header = "X-Agent-Release-SHA256";

    /// <summary>The contract's bound on <see cref="UpdateAttempt.Message"/> (AGENT_LIMITS.maxUpdateMessage).</summary>
    public const int MaxUpdateMessage = 300;

    // What the agent reports on a job.
    public const string SqlUnreachable = "sql_unreachable";
    public const string SqlLoginFailed = "sql_login_failed";
    public const string SqlPermissionDenied = "sql_permission_denied";
    public const string SqlTimeout = "sql_timeout";
    public const string SourceInvalid = "source_invalid";
    public const string Internal = "internal";

    /// <summary>The contract's bound on one chart, or on the comptes tiers read at once (AGENT_LIMITS.maxAccounts).</summary>
    public const int MaxAccounts = 10_000;
}
