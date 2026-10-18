namespace PayrollSageAgent.Core;

/// <summary>
/// The version of the platform contract this agent speaks — igf_payroll's
/// <c>docs/agent-protocol/version.json › protocol</c>, copied into <c>platform/version.json</c>.
/// It moves only when the agent implements a new version; a test asserts the platform still
/// serves it (<c>minimumSupported ≤ Version ≤ protocol</c>).
/// </summary>
public static class AgentProtocol
{
    public const int Version = 0;

    /// <summary>The agent's own build version (<c>X-Agent-Version</c>): <c>1.2.3</c>, from the assembly.</summary>
    public static string BuildVersion { get; } = ReadBuildVersion();

    private static string ReadBuildVersion()
    {
        var version = typeof(AgentProtocol).Assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }
}
