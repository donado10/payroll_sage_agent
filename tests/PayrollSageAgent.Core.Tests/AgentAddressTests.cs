using PayrollSageAgent.Core.Platform;

namespace PayrollSageAgent.Core.Tests;

public sealed class AgentAddressTests
{
    [Theory]
    // On the LAN: the platform's LAN address, or a dedicated public name.
    [InlineData("http://172.16.2.4:3000/api/agent", "http://172.16.2.4:3000/api/agent", true)]
    [InlineData("http://srv-paie:3000/api/agent", "http://172.16.2.4:3000/api/agent", true)]
    [InlineData("http://172.16.2.4:3000/api/agent", "https://agents.exemple.sn/api/agent", true)]
    // Over the WAN: a public address, then the same or another public one.
    [InlineData("http://41.82.10.5:8080/api/agent", "http://41.82.10.5:8080/api/agent", true)]
    [InlineData("https://agents.exemple.sn/api/agent", "https://agents.exemple.sn/api/agent", true)]
    // Development on one machine.
    [InlineData("http://localhost:3500/api/agent", "http://localhost:3500/api/agent", true)]
    // Back at this server while the platform was elsewhere — the « localhost » defect of 2026-10-17.
    [InlineData("http://172.16.2.4:3500/api/agent", "http://localhost:3500/api/agent", false)]
    [InlineData("http://41.82.10.5:8080/api/agent", "http://127.0.0.1:3000/api/agent", false)]
    // Inward from the WAN: an address that, at the customer's, is one of THEIR machines.
    [InlineData("http://41.82.10.5:8080/api/agent", "http://172.16.2.4:3000/api/agent", false)]
    [InlineData("https://agents.exemple.sn/api/agent", "http://10.0.0.12/api/agent", false)]
    [InlineData("https://agents.exemple.sn/api/agent", "http://192.168.1.20/api/agent", false)]
    [InlineData("https://agents.exemple.sn/api/agent", "http://paie.local/api/agent", false)]
    [InlineData("https://agents.exemple.sn/api/agent", "http://[fd00::12]/api/agent", false)]
    [InlineData("https://agents.exemple.sn/api/agent", "ftp://agents.exemple.sn/api/agent", false)]
    public void An_agent_follows_the_platforms_address_unless_it_points_inward(string calledAt, string endpoint, bool allowed)
    {
        Assert.Equal(allowed, AgentAddress.MayFollow(new Uri(calledAt), new Uri(endpoint)));
    }
}
