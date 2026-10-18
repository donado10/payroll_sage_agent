using System.Text.Json;

namespace PayrollSageAgent.Core.Tests;

public sealed class AgentProtocolTests
{
    [Fact]
    public void The_platform_still_serves_the_protocol_this_agent_speaks()
    {
        var path = RepoPaths.Platform("version.json");
        Assert.True(File.Exists(path), "platform/version.json is missing: run `npm --prefix /home/igf/igf_payroll run agent:handoff`.");

        using var version = JsonDocument.Parse(File.ReadAllText(path));
        var protocol = version.RootElement.GetProperty("protocol").GetInt32();
        var minimumSupported = version.RootElement.GetProperty("minimumSupported").GetInt32();

        Assert.InRange(AgentProtocol.Version, minimumSupported, protocol);
    }
}
