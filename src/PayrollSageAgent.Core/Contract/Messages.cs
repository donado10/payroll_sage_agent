using System.Text.Json;
using System.Text.Json.Serialization;

namespace PayrollSageAgent.Core.Contract;

// The platform's contract, message by message — platform/protocol.md and
// platform/schema/agent-protocol.schema.json, owned by igf_payroll. [HARD] Never invent a field here:
// a change is made THERE, then handed over (.claude/rules/platform-contract.md).
//
// A tolerant reader: an unknown key in a RESPONSE is ignored (System.Text.Json's default), which is
// what lets the platform add a field without breaking agents already installed. Codes are STRINGS,
// not enums, for the same reason — a code the agent does not know yet must not fail a whole response.

public sealed record PairRequest([property: JsonPropertyName("code")] string Code);

public sealed record PairResponse(
    [property: JsonPropertyName("bindingId")] string BindingId,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("company")] CompanyRef Company,
    [property: JsonPropertyName("connection")] ConnectionRef Connection,
    [property: JsonPropertyName("pollSeconds")] int PollSeconds);

public sealed record CompanyRef([property: JsonPropertyName("name")] string Name);

public sealed record ConnectionRef(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind);

public sealed record UnpairRequest;

/// <summary>« Je m'arrête » — the last call of a service that stops (<c>POST /offline</c>, step 11). Empty.</summary>
public sealed record OfflineRequest;

public sealed record ClaimRequest;

public sealed record ClaimResponse(
    [property: JsonPropertyName("job")] AgentJob? Job,
    [property: JsonPropertyName("pollSeconds")] int PollSeconds);

public sealed record AgentJob(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("leaseExpiresAt")] DateTimeOffset LeaseExpiresAt,
    [property: JsonPropertyName("payload")] JsonElement Payload);

/// <summary>An account of F_COMPTEG, as read — facts; the platform decides what it keeps.</summary>
public sealed record SageGlAccount(
    [property: JsonPropertyName("number")] string Number,
    [property: JsonPropertyName("label"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Label,
    [property: JsonPropertyName("dormant")] bool Dormant,
    [property: JsonPropertyName("sageType")] int SageType);

public sealed record SyncGlAccountsResult(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("readAt")] DateTimeOffset ReadAt,
    [property: JsonPropertyName("accounts")] IReadOnlyList<SageGlAccount> Accounts);

public sealed record CompleteRequest(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("result")] SyncGlAccountsResult Result);

public sealed record CompleteResponse(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("report")] SyncReport Report);

public sealed record SyncReport(
    [property: JsonPropertyName("received")] int Received,
    [property: JsonPropertyName("created")] int Created,
    [property: JsonPropertyName("updated")] int Updated,
    [property: JsonPropertyName("deactivated")] int Deactivated,
    [property: JsonPropertyName("reactivated")] int Reactivated,
    [property: JsonPropertyName("unchanged")] int Unchanged,
    [property: JsonPropertyName("absentFromSage")] int AbsentFromSage,
    [property: JsonPropertyName("skipped")] IReadOnlyList<SkippedAccount> Skipped);

public sealed record SkippedAccount(
    [property: JsonPropertyName("number")] string Number,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record FailRequest(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("error")] JobError Error,
    [property: JsonPropertyName("retryable")] bool Retryable);

public sealed record JobError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record FailResponse([property: JsonPropertyName("outcome")] string Outcome);

// ---- Updates (platform/protocol.md › « Updates ») — the way out: these shapes only ever grow. ----

/// <summary>What the agent says of its updates at every check, for each of its bindings.</summary>
public sealed record UpdateRequest(
    [property: JsonPropertyName("auto")] bool Auto,
    [property: JsonPropertyName("window")] string? Window,
    [property: JsonPropertyName("last")] UpdateAttempt? Last);

/// <summary>The last attempt: the version tried, its outcome, when, and one French sentence (or none).</summary>
public sealed record UpdateAttempt(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("message")] string? Message);

public sealed record UpdateResponse(
    [property: JsonPropertyName("latest")] AgentRelease? Latest,
    [property: JsonPropertyName("minimumVersion")] string? MinimumVersion,
    [property: JsonPropertyName("outdated")] bool Outdated);

/// <summary>A published version: the agent trusts <see cref="Sha256"/> and <see cref="Bytes"/>, never a header.</summary>
public sealed record AgentRelease(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("signed")] bool Signed,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset PublishedAt);

public sealed record UpdateDownloadRequest([property: JsonPropertyName("version")] string Version);

public sealed record ErrorResponse([property: JsonPropertyName("error")] PlatformError Error);

public sealed record PlatformError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("protocol"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Protocol = null,
    [property: JsonPropertyName("minimumSupported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MinimumSupported = null,
    [property: JsonPropertyName("retryAfterSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RetryAfterSeconds = null,
    [property: JsonPropertyName("minimumVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MinimumVersion = null);
