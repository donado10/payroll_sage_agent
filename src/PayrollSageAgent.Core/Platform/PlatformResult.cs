using PayrollSageAgent.Core.Contract;

namespace PayrollSageAgent.Core.Platform;

/// <summary>
/// What one call to the platform came to: the answer, a REFUSAL the platform worded (a 4xx in the
/// contract's shape — `token_invalid`, `job_not_claimed`…), or a TRANSIENT failure (network, 5xx,
/// timeout) the caller retries with backoff. Nothing here throws for an expected outcome.
/// </summary>
public sealed record PlatformResult<T>
{
    public T? Value { get; private init; }
    public PlatformError? Refusal { get; private init; }
    public int Status { get; private init; }
    /// <summary>Why a transient failure happened — for the local log only, never a secret.</summary>
    public string? Transient { get; private init; }

    public bool IsOk => Refusal is null && Transient is null;

    public static PlatformResult<T> Ok(T value, int status) => new() { Value = value, Status = status };
    public static PlatformResult<T> Refused(PlatformError error, int status) => new() { Refusal = error, Status = status };
    public static PlatformResult<T> Failed(string why, int status = 0) => new() { Transient = why, Status = status };

    public bool IsRefused(string code) => Refusal?.Code == code;
}
