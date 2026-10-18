using PayrollSageAgent.Core.Bindings;

namespace PayrollSageAgent.Service.Secrets;

public static class SecretProtectors
{
    /// <summary>DPAPI on Windows; the development stand-in only where it was explicitly asked for.</summary>
    public static ISecretProtector ForThisMachine(AgentHome home)
    {
        if (OperatingSystem.IsWindows()) return new DpapiSecretProtector();
        if (Environment.GetEnvironmentVariable("PAYROLL_AGENT_DEV_SECRETS") == "1") return new DevSecretProtector(home);
        throw new PlatformNotSupportedException(
            "Hors de Windows, l’agent ne protège ses secrets qu’en mode développement : PAYROLL_AGENT_DEV_SECRETS=1 (jamais chez un client).");
    }
}
