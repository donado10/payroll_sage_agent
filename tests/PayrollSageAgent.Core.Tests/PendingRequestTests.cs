using System.Text.Json;
using PayrollSageAgent.Core.Bindings;

namespace PayrollSageAgent.Core.Tests;

public sealed class PendingRequestTests
{
    private const string Sql = """
        {
          "name": "compta21",
          "url": "http://srv.exemple.sn:53231/api/agent",
          "code": "abcd-efgh-jkmn",
          "sql": { "server": "SRV\\SAGE", "database": "COMPTA21", "user": "agent_paie", "password": "secret", "trustServerCertificate": true }
        }
        """;

    [Fact]
    public void A_sql_request_reads_with_its_code_normalised_and_sql_auth_by_default()
    {
        var (requests, errors) = PendingRequest.ParseFile(Sql, "ignored");
        var request = Assert.Single(requests);
        var error = errors.FirstOrDefault();

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(PendingRequest.AddBinding, request.Action);
        Assert.Equal("compta21", request.Name);
        Assert.Equal("ABCD-EFGH-JKMN", request.Code);
        Assert.Equal(new SqlTarget("SRV\\SAGE", "COMPTA21", "agent_paie", true, SqlAuth.Sql), request.Sql);
        Assert.Equal("secret", request.Password);
    }

    [Fact]
    public void A_windows_request_has_no_user_and_no_password_and_takes_its_name_from_the_file()
    {
        var json = """{ "url": "http://h/api/agent", "code": "ABCD-EFGH-JKMN", "sql": { "server": "S", "database": "D", "auth": "windows" } }""";
        var (requests, errors) = PendingRequest.ParseFile(json, "from-file");
        var request = Assert.Single(requests);
        var error = errors.FirstOrDefault();

        Assert.Null(error);
        Assert.Equal("from-file", request!.Name);
        Assert.True(request.Sql.IsWindows);
        Assert.Equal("", request.Sql.User);
        Assert.Null(request.Password);
    }

    [Theory]
    [InlineData("""{ "url": "http://h/api/agent", "code": "ABCD-EFGH-JKMN", "sql": { "server": "SRV\SAGE", "database": "D", "user": "u" } }""", "barre oblique inverse s’écrit deux fois")]
    [InlineData("""{ "url": "http://h/api/agent", "code": "ABCD-EFGH-JKMN", "sql": { "server": "S", "database": "D", "user": "u", "pasword": "x" } }""", "clé inconnue « sql.pasword »")]
    [InlineData("""{ "url": "ftp://h", "code": "ABCD-EFGH-JKMN", "sql": { "server": "S", "database": "D", "user": "u" } }""", "« url »")]
    [InlineData("""{ "url": "http://h/api/agent", "code": "1234", "sql": { "server": "S", "database": "D", "user": "u" } }""", "« code »")]
    [InlineData("""{ "url": "http://h/api/agent", "code": "ABCD-EFGH-JKMN", "sql": { "server": "S", "database": "D" } }""", "« sql.user » est requis")]
    [InlineData("""{ "url": "http://h/api/agent", "code": "ABCD-EFGH-JKMN", "sql": { "server": "S", "database": "D", "auth": "windows", "password": "x" } }""", "ni « user » ni « password »")]
    [InlineData("""{ "name": "Pas Bon", "url": "http://h/api/agent", "code": "ABCD-EFGH-JKMN", "sql": { "server": "S", "database": "D", "user": "u" } }""", "« name »")]
    public void A_wrong_file_is_refused_with_a_sentence_that_says_what_to_fix(string json, string expected)
    {
        var (requests, errors) = PendingRequest.ParseFile(json, "f");

        Assert.Empty(requests);
        Assert.Contains(expected, Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void A_check_request_needs_no_url_and_no_code()
    {
        var (requests, errors) = PendingRequest.ParseFile("""{ "action": "check-sql", "sql": { "server": "S", "database": "D", "auth": "windows" } }""", "check-1");

        Assert.Empty(errors);
        var request = Assert.Single(requests);
        Assert.Equal(PendingRequest.CheckSql, request.Action);
        Assert.Null(request.Url);
    }

    private const string Several = """
        {
          "url": "http://srv.exemple.sn:53231/api/agent",
          "sql": { "server": "SRV\\SAGE", "user": "agent_commun", "password": "commun", "trustServerCertificate": true },
          "bindings": [
            { "name": "client-a", "code": "AAAA-BBBB-CCCC", "sql": { "database": "CLIENT_A" } },
            { "name": "client-b", "code": "DDDD-EEEE-FFFF", "sql": { "database": "CLIENT_B", "user": "agent_b", "password": "b" } },
            { "name": "client-c", "code": "GGGG-HHHH-JJJJ", "url": "http://autre.exemple.sn/api/agent", "sql": { "server": "SRV2", "database": "CLIENT_C", "auth": "windows", "user": null, "password": null } }
          ]
        }
        """;

    [Fact]
    public void Several_bindings_share_the_top_values_and_each_overrides_its_own()
    {
        var json = Several.Replace("\"user\": null, \"password\": null ", "", StringComparison.Ordinal);
        var (requests, errors) = PendingRequest.ParseFile(json, "hebergeur");

        Assert.Empty(errors);
        Assert.Equal(["client-a", "client-b", "client-c"], requests.Select(r => r.Name));
        Assert.Equal(new SqlTarget("SRV\\SAGE", "CLIENT_A", "agent_commun", true), requests[0].Sql);
        Assert.Equal("commun", requests[0].Password);
        Assert.Equal(new SqlTarget("SRV\\SAGE", "CLIENT_B", "agent_b", true), requests[1].Sql);
        Assert.Equal("b", requests[1].Password);
        Assert.Equal("AAAA-BBBB-CCCC", requests[0].Code);
        Assert.Equal(new Uri("http://srv.exemple.sn:53231/api/agent"), requests[1].Url);
        Assert.Equal(new Uri("http://autre.exemple.sn/api/agent"), requests[2].Url);
    }

    [Fact]
    public void A_binding_that_asks_for_windows_auth_drops_the_shared_login_but_not_one_of_its_own()
    {
        var inherited = Several.Replace("\"user\": null, \"password\": null ", "", StringComparison.Ordinal);
        var (requests, _) = PendingRequest.ParseFile(inherited, "x");
        Assert.True(requests[2].Sql.IsWindows);
        Assert.Equal("", requests[2].Sql.User);
        Assert.Null(requests[2].Password);

        var own = Several.Replace("\"user\": null, \"password\": null ", "\"user\": \"agent_c\" ", StringComparison.Ordinal);
        var (none, errors) = PendingRequest.ParseFile(own, "x");
        Assert.Empty(none);
        Assert.Contains("liaison 3 (« client-c ») : avec « auth » : « windows », ni « user » ni « password »", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void One_malformed_binding_and_nothing_is_done_with_every_problem_said_at_once()
    {
        var json = """
            {
              "url": "http://h/api/agent",
              "sql": { "server": "S", "user": "u", "password": "p" },
              "bindings": [
                { "name": "ok", "code": "AAAA-BBBB-CCCC", "sql": { "database": "A" } },
                { "name": "sans-code", "sql": { "database": "B" } },
                { "code": "DDDD-EEEE-FFFF", "sql": { "database": "C" } },
                { "name": "ok", "code": "GGGG-HHHH-JJJJ", "sql": { "database": "D" } }
              ]
            }
            """;
        var (requests, errors) = PendingRequest.ParseFile(json, "x");

        Assert.Empty(requests);
        Assert.Collection(errors,
            e => Assert.StartsWith("liaison 2 (« sans-code ») : « code »", e, StringComparison.Ordinal),
            e => Assert.StartsWith("liaison 3 : « name » est requis", e, StringComparison.Ordinal),
            e => Assert.StartsWith("liaison 4 (« ok ») : le nom « ok » figure deux fois", e, StringComparison.Ordinal));
    }

    [Fact]
    public void With_bindings_the_name_and_the_code_belong_to_each_binding_not_the_top()
    {
        var (requests, errors) = PendingRequest.ParseFile("""{ "code": "AAAA-BBBB-CCCC", "bindings": [ { "name": "a" } ] }""", "x");

        Assert.Empty(requests);
        Assert.Contains("le nom et le code vont dans chaque liaison", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void A_binding_file_written_before_windows_auth_still_reads_as_a_sql_login()
    {
        // The shape 0.2.x wrote: no « auth », a protected password.
        var json = """
            { "name": "agent", "endpoint": "http://h/api/agent", "bindingId": "b", "companyName": "C", "connectionId": "c",
              "connectionName": "N", "sql": { "server": "S", "database": "D", "user": "u", "trustServerCertificate": true },
              "protectedToken": "t", "protectedSqlPassword": "p", "pairedAt": "2026-10-17T19:59:30Z", "state": "active" }
            """;
        var binding = JsonSerializer.Deserialize<BindingConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(binding);
        Assert.Equal(SqlAuth.Sql, binding.Sql.Auth);
        Assert.False(binding.Sql.IsWindows);
        Assert.Equal("p", binding.ProtectedSqlPassword);
    }
}
