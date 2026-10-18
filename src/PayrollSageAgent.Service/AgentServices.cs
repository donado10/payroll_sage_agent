using PayrollSageAgent.Core;
using PayrollSageAgent.Core.Bindings;
using PayrollSageAgent.Core.Jobs;
using PayrollSageAgent.Core.Platform;
using PayrollSageAgent.Core.Sync;
using PayrollSageAgent.Sage;
using PayrollSageAgent.Service.Bindings;
using PayrollSageAgent.Service.Secrets;

namespace PayrollSageAgent.Service;

/// <summary>What the service and the verbs share — built once, the same way for both.</summary>
public static class AgentServices
{
    /// <summary>[HARD] One gate for the whole agent: at most two queries on the instance at once.</summary>
    private const int MaxConcurrentQueries = 2;

    public static AgentHome Home() => AgentHome.ForThisMachine();

    public static HttpClient Http() => new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>For the installer's download alone: ≈ 40 MB over a customer's line takes minutes, not 30 s.</summary>
    public static HttpClient DownloadHttp() => new() { Timeout = TimeSpan.FromMinutes(30) };

    public static IServiceCollection AddAgent(this IServiceCollection services)
    {
        var home = Home();
        services.AddSingleton(home);
        services.AddSingleton<BindingStore>();
        services.AddSingleton<ISecretProtector>(_ => SecretProtectors.ForThisMachine(home));
        services.AddSingleton(_ => Http());
        services.AddSingleton<IPlatformClient>(sp => new PlatformClient(sp.GetRequiredService<HttpClient>(), AgentProtocol.BuildVersion));
        services.AddSingleton<ISageChartReader, SageChartReader>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<BindingPairing>();
        services.AddSingleton<PendingRequests>();
        services.AddSingleton(sp => new JobRunner(
            sp.GetRequiredService<ISageChartReader>(), new SemaphoreSlim(MaxConcurrentQueries), sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
