using Microsoft.Extensions.Logging;

namespace PayrollSageAgent.Core.Platform;

/// <summary>One binding the service was running when it stopped: where to call, and with what.</summary>
public sealed record RunningBinding(string Name, Uri Endpoint, string Token);

/// <summary>
/// « Je m'arrête » (contract step 11): the service, stopping, tells the platform for each binding whose loop it
/// ran, so the screen says « Hors ligne » at once instead of after 45 s of silence.
///
/// [HARD] Called AFTER the loops have ended. A claim still in flight that landed after this call would put the
/// binding back « En ligne » on the platform, for a service that is gone.
///
/// Best effort, and it never holds the stop: every binding at once, <see cref="Budget"/> for all of them
/// together, never retried. A failure, a refusal (a binding revoked meanwhile) or the budget running out is one
/// log line; the platform falls back on the silence. Windows' own stop timeout comes first anyway — a server
/// shutting down kills a service that takes too long, and that is acceptable here.
/// </summary>
public static partial class OfflineNotice
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <returns>How many bindings the platform heard.</returns>
    public static async Task<int> SendAsync(IPlatformClient platform, IReadOnlyCollection<RunningBinding> bindings, ILogger logger, TimeSpan? budget = null)
    {
        if (bindings.Count == 0) return 0;
        using var timeout = new CancellationTokenSource(budget ?? Budget);
        var told = await Task.WhenAll(bindings.Select(b => TellAsync(platform, b, logger, timeout.Token)));
        var heard = told.Count(t => t);
        LogSent(logger, heard, bindings.Count);
        return heard;
    }

    private static async Task<bool> TellAsync(IPlatformClient platform, RunningBinding binding, ILogger logger, CancellationToken ct)
    {
        try
        {
            var result = await platform.OfflineAsync(binding.Endpoint, binding.Token, ct);
            if (result.IsOk) return true;
            LogNotHeard(logger, binding.Name, result.Refusal?.Code ?? result.Transient ?? "?");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogNotHeard(logger, binding.Name, "délai dépassé");
        }
        catch (Exception error)
        {
            LogNotHeard(logger, binding.Name, error.GetType().Name);
        }
        return false;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Arrêt signalé à la plateforme : {Heard} liaison(s) sur {Count}.")]
    private static partial void LogSent(ILogger logger, int heard, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Liaison « {Name} » : l’arrêt n’a pas pu être signalé ({Why}) ; la plateforme la verra hors ligne après 45 s de silence.")]
    private static partial void LogNotHeard(ILogger logger, string name, string why);
}
