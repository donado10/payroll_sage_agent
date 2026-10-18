using PayrollSageAgent.Core.Contract;

namespace PayrollSageAgent.Core.Platform;

/// <summary>The calls of the contract (platform/protocol.md › « Routes »), as ONE binding.</summary>
public interface IPlatformClient
{
    /// <summary>The update check — not a heartbeat; the way out, open to an agent too old for the rest.</summary>
    Task<PlatformResult<UpdateResponse>> CheckUpdateAsync(Uri endpoint, string token, UpdateRequest request, CancellationToken ct);

    /// <summary>The installer's bytes, written to <paramref name="destination"/>; the value is how many.</summary>
    Task<PlatformResult<long>> DownloadUpdateAsync(Uri endpoint, string token, string version, Stream destination, CancellationToken ct);

    Task<PlatformResult<PairResponse>> PairAsync(Uri endpoint, string code, CancellationToken ct);

    Task<PlatformResult<ClaimResponse>> ClaimAsync(Uri endpoint, string token, CancellationToken ct);

    Task<PlatformResult<CompleteResponse>> CompleteAsync(Uri endpoint, string token, CompleteRequest request, CancellationToken ct);

    Task<PlatformResult<FailResponse>> FailAsync(Uri endpoint, string token, FailRequest request, CancellationToken ct);

    Task<PlatformResult<bool>> UnpairAsync(Uri endpoint, string token, CancellationToken ct);

    /// <summary>« Je m'arrête » — the binding shows « Hors ligne » at once, until its next call.</summary>
    Task<PlatformResult<bool>> OfflineAsync(Uri endpoint, string token, CancellationToken ct);
}
