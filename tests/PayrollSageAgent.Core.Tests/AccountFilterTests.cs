using System.Text.Json;
using PayrollSageAgent.Core.Bindings;

namespace PayrollSageAgent.Core.Tests;

/// <summary>agent.json › <c>accountFilter</c> (contract step 12): read, checked, inherited, kept.</summary>
public sealed class AccountFilterTests
{
    private const string Shared = """
        {
          "url": "http://srv.exemple.sn:53231/api/agent",
          "sql": { "server": "SRV\\SAGE", "user": "agent", "password": "secret" },
          "accountFilter": ["4%", "50%"],
          "bindings": [
            { "name": "inherits", "code": "ABCD-EFGH-JKMN", "sql": { "database": "A" } },
            { "name": "own", "code": "ABCD-EFGH-JKMP", "sql": { "database": "B" }, "accountFilter": ["%62%", "421100"] },
            { "name": "none", "code": "ABCD-EFGH-JKMQ", "sql": { "database": "C" }, "accountFilter": [] }
          ]
        }
        """;

    [Fact]
    public void A_binding_inherits_the_shared_filter_unless_it_states_its_own_and_an_empty_list_is_none()
    {
        var (requests, errors) = PendingRequest.ParseFile(Shared, "agent", config: true);

        Assert.Empty(errors);
        Assert.Equal<string>(["4%", "50%"], requests.Single(r => r.Name == "inherits").AccountFilter);
        Assert.Equal<string>(["%62%", "421100"], requests.Single(r => r.Name == "own").AccountFilter);
        Assert.Empty(requests.Single(r => r.Name == "none").AccountFilter);
    }

    [Fact]
    public void A_file_without_the_key_reads_as_the_whole_chart()
    {
        var json = """{ "url": "http://h/api/agent", "code": "ABCD-EFGH-JKMN", "sql": { "server": "S", "database": "D", "auth": "windows" } }""";
        var (requests, errors) = PendingRequest.ParseFile(json, "one");

        Assert.Empty(errors);
        Assert.Empty(Assert.Single(requests).AccountFilter);
    }

    [Fact]
    public void A_single_binding_file_carries_its_filter_too()
    {
        var json = """{ "url": "http://h/api/agent", "code": "ABCD-EFGH-JKMN", "sql": { "server": "S", "database": "D", "auth": "windows" }, "accountFilter": ["%62"] }""";
        var (requests, errors) = PendingRequest.ParseFile(json, "one");

        Assert.Empty(errors);
        Assert.Equal<string>(["%62"], Assert.Single(requests).AccountFilter);
    }

    [Theory]
    [InlineData("""["4 %"]""", "« 4 % » n’est pas un motif")]
    [InlineData("""["[4]%"]""", "« [4]% » n’est pas un motif")]
    [InlineData("""[""]""", "«  » n’est pas un motif")]
    [InlineData("""[4]""", "4 n’est pas un motif")]
    [InlineData("\"4%\"", "« accountFilter » : une liste de motifs")]
    public void A_pattern_the_platform_would_read_differently_is_refused_with_a_sentence(string filter, string expected)
    {
        var json = $$"""{ "url": "http://h/api/agent", "code": "ABCD-EFGH-JKMN", "sql": { "server": "S", "database": "D", "auth": "windows" }, "accountFilter": {{filter}} }""";
        var (requests, errors) = PendingRequest.ParseFile(json, "one");

        Assert.Empty(requests);
        Assert.Contains(expected, Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void More_than_fifty_patterns_are_refused()
    {
        var many = JsonSerializer.Serialize(Enumerable.Range(1, AccountFilter.MaxPatterns + 1).Select(i => $"{i}%"));
        var json = $$"""{ "url": "http://h/api/agent", "code": "ABCD-EFGH-JKMN", "sql": { "server": "S", "database": "D", "auth": "windows" }, "accountFilter": {{many}} }""";

        Assert.Contains("50 motifs au plus", Assert.Single(PendingRequest.ParseFile(json, "one").Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_shared_filter_is_said_once_and_nothing_is_done()
    {
        var json = Shared.Replace("[\"4%\", \"50%\"]", "[\"4 %\"]", StringComparison.Ordinal);
        var (requests, errors) = PendingRequest.ParseFile(json, "agent", config: true);

        Assert.Empty(requests);
        Assert.Single(errors, e => e.Contains("« 4 % »", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("4%")]
    [InlineData("%62")]
    [InlineData("%62%")]
    [InlineData("421100")]
    [InlineData("4_1%")]
    [InlineData("ab%")]
    public void The_shapes_the_user_writes_are_patterns(string pattern) => Assert.True(AccountFilter.IsValid(pattern));

    [Fact]
    public void A_binding_file_written_before_the_filter_reads_as_the_whole_chart()
    {
        // The shape BindingStore writes (Web defaults), from a 0.5.x agent: no accountFilter.
        var json = """
            {
              "name": "a", "endpoint": "http://h/api/agent", "bindingId": "b", "companyName": "C", "connectionId": "c",
              "connectionName": "Sage", "sql": { "server": "S", "database": "D", "user": "u" },
              "protectedToken": "t", "protectedSqlPassword": "p", "pairedAt": "2026-10-17T20:00:00+00:00", "state": "active"
            }
            """;
        var binding = JsonSerializer.Deserialize<BindingConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(binding);
        Assert.Empty(binding.AccountFilter);
    }

    [Fact]
    public void Two_filters_are_the_same_by_their_patterns_not_by_reference()
    {
        Assert.True(AccountFilter.Same(["4%", "50%"], new List<string> { "4%", "50%" }));
        Assert.False(AccountFilter.Same(["4%", "50%"], ["50%", "4%"]));
        Assert.False(AccountFilter.Same(["4%"], []));
    }

    // ---- partnerFilter (contract step 13): the comptes tiers, the same grammar, its own key -----------------

    private const string SharedPartners = """
        {
          "url": "http://srv.exemple.sn:53231/api/agent",
          "sql": { "server": "SRV\\SAGE", "user": "agent", "password": "secret" },
          "accountFilter": ["4%"],
          "partnerFilter": ["SAL%", "IPRES"],
          "bindings": [
            { "name": "inherits", "code": "ABCD-EFGH-JKMN", "sql": { "database": "A" } },
            { "name": "own", "code": "ABCD-EFGH-JKMP", "sql": { "database": "B" }, "partnerFilter": ["C%"] },
            { "name": "none", "code": "ABCD-EFGH-JKMQ", "sql": { "database": "C" }, "partnerFilter": [] }
          ]
        }
        """;

    [Fact]
    public void The_partner_filter_is_inherited_and_overridden_on_its_own_beside_the_account_filter()
    {
        var (requests, errors) = PendingRequest.ParseFile(SharedPartners, "agent", config: true);

        Assert.Empty(errors);
        Assert.Equal<string>(["SAL%", "IPRES"], requests.Single(r => r.Name == "inherits").PartnerFilter);
        Assert.Equal<string>(["C%"], requests.Single(r => r.Name == "own").PartnerFilter);
        Assert.Empty(requests.Single(r => r.Name == "none").PartnerFilter);
        // Overriding one filter leaves the other inherited.
        Assert.Equal<string>(["4%"], requests.Single(r => r.Name == "own").AccountFilter);
    }

    [Fact]
    public void A_single_binding_file_carries_its_partner_filter_and_without_it_every_tiers()
    {
        var with = """{ "url": "http://h/api/agent", "code": "ABCD-EFGH-JKMN", "sql": { "server": "S", "database": "D", "auth": "windows" }, "partnerFilter": ["SAL%"] }""";
        var without = """{ "url": "http://h/api/agent", "code": "ABCD-EFGH-JKMN", "sql": { "server": "S", "database": "D", "auth": "windows" } }""";

        Assert.Equal<string>(["SAL%"], Assert.Single(PendingRequest.ParseFile(with, "one").Requests).PartnerFilter);
        Assert.Empty(Assert.Single(PendingRequest.ParseFile(without, "one").Requests).PartnerFilter);
    }

    [Fact]
    public void A_bad_partner_pattern_is_refused_with_a_sentence_naming_partnerFilter()
    {
        var json = """{ "url": "http://h/api/agent", "code": "ABCD-EFGH-JKMN", "sql": { "server": "S", "database": "D", "auth": "windows" }, "partnerFilter": ["SAL %"] }""";
        var (requests, errors) = PendingRequest.ParseFile(json, "one");

        Assert.Empty(requests);
        Assert.Contains("« partnerFilter » : « SAL % » n’est pas un motif", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void A_binding_file_written_before_0_7_0_reads_as_every_tiers()
    {
        var json = """
            {
              "name": "a", "endpoint": "http://h/api/agent", "bindingId": "b", "companyName": "C", "connectionId": "c",
              "connectionName": "Sage", "sql": { "server": "S", "database": "D", "user": "u" },
              "protectedToken": "t", "protectedSqlPassword": "p", "pairedAt": "2026-10-17T20:00:00+00:00", "state": "active",
              "accountFilter": ["4%"]
            }
            """;
        var binding = JsonSerializer.Deserialize<BindingConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(binding);
        Assert.Equal<string>(["4%"], binding.AccountFilter);
        Assert.Empty(binding.PartnerFilter);
    }
}
