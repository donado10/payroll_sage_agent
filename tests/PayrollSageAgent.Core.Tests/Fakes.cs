using System.Net;
using System.Text;
using PayrollSageAgent.Core.Bindings;
using PayrollSageAgent.Core.Contract;
using PayrollSageAgent.Core.Platform;
using PayrollSageAgent.Core.Sync;

namespace PayrollSageAgent.Core.Tests;

internal static class Make
{
    public static BindingConfig Binding(string name) => new()
    {
        Name = name,
        Endpoint = new Uri("https://agents.example.com/api/agent"),
        BindingId = Guid.NewGuid().ToString(),
        CompanyName = "Société exemple",
        ConnectionId = Guid.NewGuid().ToString(),
        ConnectionName = $"Sage {name}",
        Sql = new SqlTarget("sql.example", $"DB_{name}", $"agent_{name}"),
        ProtectedToken = "x",
        ProtectedSqlPassword = "x",
        PairedAt = DateTimeOffset.UtcNow,
    };

    public static AgentJob Job(int attempt = 1, string type = ContractNames.SyncGlAccounts) =>
        new(Guid.NewGuid().ToString(), type, attempt, DateTimeOffset.UtcNow.AddMinutes(5),
            System.Text.Json.JsonDocument.Parse("{}").RootElement);
}

/// <summary>A Sage that answers what it is told — a chart, or a failure — for the comptes généraux and the tiers alike.</summary>
internal sealed class FakeReader(Func<SqlTarget, IReadOnlyList<SageAccount>> read) : ISageChartReader
{
    public int Reads { get; private set; }

    /// <summary>The filter of the last read — what the runner handed down.</summary>
    public IReadOnlyList<string>? LastFilter { get; private set; }

    /// <summary>Which table the last read was: F_COMPTEG or F_COMPTET.</summary>
    public string? LastTable { get; private set; }

    public Task<IReadOnlyList<SageAccount>> ReadChartAsync(SqlTarget target, string password, IReadOnlyList<string> accountFilter, CancellationToken ct)
    {
        Reads++;
        LastFilter = accountFilter;
        LastTable = "F_COMPTEG";
        return Task.FromResult(read(target));
    }

    public Task<IReadOnlyList<SageAccount>> ReadPartnersAsync(SqlTarget target, string password, IReadOnlyList<string> partnerFilter, CancellationToken ct)
    {
        Reads++;
        LastFilter = partnerFilter;
        LastTable = "F_COMPTET";
        return Task.FromResult(read(target));
    }

    public Task<SqlLoginRights> CheckAsync(SqlTarget target, string password, CancellationToken ct) => Task.FromResult(new SqlLoginRights(false, false));
}

/// <summary>A platform that hands each token its own script of claims, and records what it receives.</summary>
internal sealed class FakePlatform : IPlatformClient
{
    public Dictionary<string, Queue<PlatformResult<ClaimResponse>>> Claims { get; } = new(StringComparer.Ordinal);
    public List<(string Token, CompleteRequest Request)> Completed { get; } = [];
    public List<(string Token, FailRequest Request)> Failed { get; } = [];

    public Task<PlatformResult<ClaimResponse>> ClaimAsync(Uri endpoint, string token, CancellationToken ct)
    {
        var next = Claims.TryGetValue(token, out var q) && q.Count > 0 ? q.Dequeue() : PlatformResult<ClaimResponse>.Ok(new ClaimResponse(null, 15), 200);
        return Task.FromResult(next);
    }

    public Task<PlatformResult<CompleteResponse>> CompleteAsync(Uri endpoint, string token, CompleteRequest request, CancellationToken ct)
    {
        Completed.Add((token, request));
        var report = new SyncReport(request.Result.Accounts.Count, request.Result.Accounts.Count, 0, 0, 0, 0, 0, []);
        return Task.FromResult(PlatformResult<CompleteResponse>.Ok(new CompleteResponse("applied", report), 200));
    }

    public Task<PlatformResult<FailResponse>> FailAsync(Uri endpoint, string token, FailRequest request, CancellationToken ct)
    {
        Failed.Add((token, request));
        return Task.FromResult(PlatformResult<FailResponse>.Ok(new FailResponse("retry_scheduled"), 200));
    }

    /// <summary>The update check: what each token is answered; every request is kept.</summary>
    public Func<string, PlatformResult<UpdateResponse>> Updates { get; set; } =
        _ => PlatformResult<UpdateResponse>.Ok(new UpdateResponse(null, null, false), 200);
    public List<(string Token, UpdateRequest Request)> UpdateChecks { get; } = [];

    /// <summary>The installer's bytes the download writes — or, when set, the answer it gives instead.</summary>
    public byte[] Installer { get; set; } = [];
    public PlatformResult<long>? DownloadAnswer { get; set; }
    public List<string> Downloads { get; } = [];

    public Task<PlatformResult<UpdateResponse>> CheckUpdateAsync(Uri endpoint, string token, UpdateRequest request, CancellationToken ct)
    {
        UpdateChecks.Add((token, request));
        return Task.FromResult(Updates(token));
    }

    public async Task<PlatformResult<long>> DownloadUpdateAsync(Uri endpoint, string token, string version, Stream destination, CancellationToken ct)
    {
        Downloads.Add(version);
        if (DownloadAnswer is { } answer) return answer;
        await destination.WriteAsync(Installer, ct);
        return PlatformResult<long>.Ok(Installer.Length, 200);
    }

    public Task<PlatformResult<PairResponse>> PairAsync(Uri endpoint, string code, CancellationToken ct) => throw new NotSupportedException();

    public Task<PlatformResult<bool>> UnpairAsync(Uri endpoint, string token, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>« Je m'arrête »: what each token is answered — or, when it returns null, a call that never ends.</summary>
    public Func<string, PlatformResult<bool>?> Offline { get; set; } = _ => PlatformResult<bool>.Ok(true, 204);
    public List<string> Stopped { get; } = [];

    public async Task<PlatformResult<bool>> OfflineAsync(Uri endpoint, string token, CancellationToken ct)
    {
        lock (Stopped) Stopped.Add(token);
        if (Offline(token) is { } answer) return answer;
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    }
}

/// <summary>An HTTP server in a box: answers every request with what <c>respond</c> says, and keeps the requests.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Seen.Add((request, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
