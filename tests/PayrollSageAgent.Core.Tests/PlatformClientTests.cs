using System.Net;
using System.Text.Json;
using PayrollSageAgent.Core.Contract;
using PayrollSageAgent.Core.Platform;

namespace PayrollSageAgent.Core.Tests;

public sealed class PlatformClientTests
{
    private static readonly Uri Endpoint = new("https://agents.example.com/api/agent");

    [Fact]
    public async Task Every_call_carries_both_headers_and_a_token_call_carries_the_bearer()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, """{"job":null,"pollSeconds":15}"""));
        var client = new PlatformClient(new HttpClient(handler), "1.2.3");

        var result = await client.ClaimAsync(Endpoint, "igfa_secret", TestContext.Current.CancellationToken);

        Assert.True(result.IsOk);
        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal("https://agents.example.com/api/agent/jobs/claim", request.RequestUri!.AbsoluteUri);
        Assert.Equal(AgentProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Headers.GetValues(ContractNames.ProtocolHeader).Single());
        Assert.Equal("1.2.3", request.Headers.GetValues(ContractNames.VersionHeader).Single());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("igfa_secret", request.Headers.Authorization.Parameter);
        Assert.Equal("{}", body);
        Assert.NotNull(request.Content!.Headers.ContentLength);
    }

    [Fact]
    public async Task Pairing_sends_no_token()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.NotFound,
            """{"error":{"code":"code_invalid","message":"Ce code d’appairage est inconnu, expiré ou déjà utilisé."}}"""));
        var client = new PlatformClient(new HttpClient(handler), "1.2.3");

        var result = await client.PairAsync(Endpoint, "K7QM-3XRP-9WTD", TestContext.Current.CancellationToken);

        Assert.True(result.IsRefused(ContractNames.CodeInvalid));
        Assert.Equal(404, result.Status);
        Assert.Null(handler.Seen.Single().Request.Headers.Authorization);
        Assert.Equal("""{"code":"K7QM-3XRP-9WTD"}""", handler.Seen.Single().Body);
    }

    [Fact]
    public async Task A_refusal_keeps_its_numbers_and_a_5xx_is_transient()
    {
        var tooOld = new PlatformClient(new HttpClient(new StubHandler((_, _) => StubHandler.Json((HttpStatusCode)426,
            """{"error":{"code":"protocol_unsupported","message":"…","protocol":2,"minimumSupported":1}}"""))), "1.2.3");
        var refused = await tooOld.ClaimAsync(Endpoint, "t", TestContext.Current.CancellationToken);
        Assert.True(refused.IsRefused(ContractNames.ProtocolUnsupported));
        Assert.Equal(1, refused.Refusal!.MinimumSupported);
        Assert.Equal(2, refused.Refusal.Protocol);

        var down = new PlatformClient(new HttpClient(new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.InternalServerError,
            """{"error":"internal_error"}"""))), "1.2.3");
        var failed = await down.ClaimAsync(Endpoint, "t", TestContext.Current.CancellationToken);
        Assert.False(failed.IsOk);
        Assert.Null(failed.Refusal);
        Assert.NotNull(failed.Transient);
    }

    [Fact]
    public async Task A_response_with_a_key_this_agent_does_not_know_is_still_read()
    {
        var client = new PlatformClient(new HttpClient(new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK,
            """{"job":null,"pollSeconds":20,"aNewField":{"x":1}}"""))), "1.2.3");
        var result = await client.ClaimAsync(Endpoint, "t", TestContext.Current.CancellationToken);
        Assert.Equal(20, result.Value!.PollSeconds);
    }

    [Fact]
    public async Task Unpair_reads_204_as_done()
    {
        var client = new PlatformClient(new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent))), "1.2.3");
        var result = await client.UnpairAsync(Endpoint, "t", TestContext.Current.CancellationToken);
        Assert.True(result.IsOk);
    }

    [Fact]
    public async Task Offline_posts_an_empty_body_with_the_token_and_reads_204_as_done()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = new PlatformClient(new HttpClient(handler), "1.2.3");

        var result = await client.OfflineAsync(Endpoint, "t", TestContext.Current.CancellationToken);

        Assert.True(result.IsOk);
        var (request, body) = handler.Seen.Single();
        Assert.EndsWith("/offline", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("Bearer t", request.Headers.Authorization?.ToString());
        Assert.Equal("{}", body);
    }

    [Fact]
    public async Task A_complete_body_is_the_contracts_shape_with_a_null_label_written()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK,
            """{"outcome":"applied","report":{"received":1,"created":0,"updated":0,"deactivated":0,"reactivated":0,"unchanged":0,"absentFromSage":0,"skipped":[{"number":"1","reason":"label_missing"}]}}"""));
        var client = new PlatformClient(new HttpClient(handler), "1.2.3");
        var request = new CompleteRequest("3c4d5e6f-7a8b-4c9d-8e0f-1a2b3c4d5e6f", 1,
            new SyncGlAccountsResult(ContractNames.SyncGlAccounts, DateTimeOffset.UnixEpoch, [new SageGlAccount("1", null, false, 0)]));

        var result = await client.CompleteAsync(Endpoint, "t", request, TestContext.Current.CancellationToken);

        Assert.True(result.IsOk);
        using var sent = JsonDocument.Parse(handler.Seen.Single().Body!);
        var account = sent.RootElement.GetProperty("result").GetProperty("accounts")[0];
        Assert.Equal(JsonValueKind.Null, account.GetProperty("label").ValueKind);
        Assert.Equal("3c4d5e6f-7a8b-4c9d-8e0f-1a2b3c4d5e6f", sent.RootElement.GetProperty("jobId").GetString());
    }

    [Fact]
    public async Task The_installer_streams_into_the_file_and_a_refusal_keeps_its_code()
    {
        var bytes = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var client = new PlatformClient(new HttpClient(handler), "0.5.0");
        using var file = new MemoryStream();

        var got = await client.DownloadUpdateAsync(Endpoint, "t", "0.5.1", file, TestContext.Current.CancellationToken);

        Assert.True(got.IsOk);
        Assert.Equal(bytes.Length, got.Value);
        Assert.Equal(bytes, file.ToArray());
        var (request, body) = handler.Seen.Single();
        Assert.Equal("https://agents.example.com/api/agent/update/download", request.RequestUri!.AbsoluteUri);
        Assert.Equal("""{"version":"0.5.1"}""", body);

        var refusing = new PlatformClient(new HttpClient(new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.NotFound,
            """{"error":{"code":"release_not_found","message":"…"}}"""))), "0.5.0");
        var refused = await refusing.DownloadUpdateAsync(Endpoint, "t", "9.9.9", new MemoryStream(), TestContext.Current.CancellationToken);
        Assert.True(refused.IsRefused(ContractNames.ReleaseNotFound));
    }

    [Fact]
    public async Task The_update_check_says_the_settings_and_the_last_attempt()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, """{"latest":null,"minimumVersion":"0.5.0","outdated":true}"""));
        var client = new PlatformClient(new HttpClient(handler), "0.4.1");

        var answer = await client.CheckUpdateAsync(Endpoint, "t",
            new UpdateRequest(false, "22:00-02:00", new UpdateAttempt("0.5.1", ContractNames.UpdateRolledBack, new DateTimeOffset(2026, 10, 19, 22, 14, 40, TimeSpan.Zero), "Revenu.")),
            TestContext.Current.CancellationToken);

        Assert.True(answer.Value!.Outdated);
        Assert.Equal("0.5.0", answer.Value.MinimumVersion);
        Assert.Equal(
            """{"auto":false,"window":"22:00-02:00","last":{"version":"0.5.1","outcome":"rolled_back","at":"2026-10-19T22:14:40+00:00","message":"Revenu."}}""",
            handler.Seen.Single().Body);
    }
}
