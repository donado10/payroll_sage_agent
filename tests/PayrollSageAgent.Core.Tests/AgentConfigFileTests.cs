using System.Text.Json;
using PayrollSageAgent.Core.Bindings;

namespace PayrollSageAgent.Core.Tests;

public sealed class AgentConfigFileTests
{
    private const string Config = """
        {
          "url": "http://srv.exemple.sn/api/agent",
          "sql": { "server": "SRV\\SAGE", "user": "agent_commun", "password": "commun", "trustServerCertificate": true },
          "bindings": [
            { "name": "client-a", "code": "AAAA-BBBB-CCCC", "sql": { "database": "A" } },
            { "name": "client-b", "code": "", "sql": { "database": "B", "user": "agent_b", "password": "(protégé)" } },
            { "name": "client-c", "code": "DDDD-EEEE-FFFF", "sql": { "database": "C", "user": "agent_c", "password": "c" } },
            { "name": "client-d", "remove": true }
          ]
        }
        """;

    [Fact]
    public void The_template_is_a_valid_config_with_no_binding()
    {
        var (requests, errors) = PendingRequest.ParseFile(AgentConfigFile.Template, "agent", config: true);

        Assert.Empty(errors);
        Assert.Empty(requests);
    }

    [Fact]
    public void In_the_config_a_used_code_and_a_protected_password_mean_keep_what_is_stored()
    {
        var (requests, errors) = PendingRequest.ParseFile(Config, "agent", config: true);

        Assert.Empty(errors);
        Assert.Equal(["client-a", "client-b", "client-c", "client-d"], requests.Select(r => r.Name));
        Assert.Equal("AAAA-BBBB-CCCC", requests[0].Code);
        Assert.Equal("commun", requests[0].Password);
        Assert.Null(requests[1].Code);
        Assert.Null(requests[1].Password);
        Assert.True(requests[3].Remove);
    }

    [Fact]
    public void Remove_is_a_config_word_and_an_empty_code_is_refused_in_a_request()
    {
        var (_, errors) = PendingRequest.ParseFile(Config, "agent");

        Assert.Contains(errors, e => e.Contains("« code »", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("clé inconnue « remove »", StringComparison.Ordinal));
    }

    [Fact]
    public void Once_applied_the_secrets_of_what_worked_leave_the_file_and_the_rest_stays()
    {
        // client-a paired and protected its (shared) password; client-c failed; client-d was removed.
        var rewritten = AgentConfigFile.WithoutSecrets(Config, paired: new HashSet<string> { "client-a" },
            protectedPasswords: new HashSet<string> { "client-a" }, removed: new HashSet<string> { "client-d" });

        Assert.NotNull(rewritten);
        using var document = JsonDocument.Parse(rewritten);
        var bindings = document.RootElement.GetProperty("bindings");
        Assert.Equal(3, bindings.GetArrayLength());
        Assert.Equal("", bindings[0].GetProperty("code").GetString());
        Assert.Equal("DDDD-EEEE-FFFF", bindings[2].GetProperty("code").GetString());
        Assert.Equal("c", bindings[2].GetProperty("sql").GetProperty("password").GetString());
        // client-a is the only one inheriting the shared password, and it holds it protected now.
        Assert.Equal(PendingRequest.ProtectedMarker, document.RootElement.GetProperty("sql").GetProperty("password").GetString());
        Assert.Contains("(protégé)", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shared_password_stays_while_a_binding_that_inherits_it_has_not_protected_it()
    {
        var config = Config.Replace("\"user\": \"agent_c\", \"password\": \"c\" ", "", StringComparison.Ordinal);
        var rewritten = AgentConfigFile.WithoutSecrets(config, paired: new HashSet<string> { "client-a" },
            protectedPasswords: new HashSet<string> { "client-a" }, removed: new HashSet<string>());

        using var document = JsonDocument.Parse(rewritten!);
        Assert.Equal("commun", document.RootElement.GetProperty("sql").GetProperty("password").GetString());
    }

    [Fact]
    public void Nothing_to_take_out_is_no_rewrite()
    {
        Assert.Null(AgentConfigFile.WithoutSecrets(AgentConfigFile.Template, new HashSet<string>(), new HashSet<string>(), new HashSet<string>()));
    }
}
