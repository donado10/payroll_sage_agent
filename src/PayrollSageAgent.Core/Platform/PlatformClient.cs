using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PayrollSageAgent.Core.Contract;

namespace PayrollSageAgent.Core.Platform;

/// <summary>
/// The platform, over HTTP(S) — every call agent → platform, never the reverse.
///
/// Every call carries <c>X-Agent-Protocol</c> (<see cref="AgentProtocol.Version"/>), <c>X-Agent-Version</c>
/// and a <c>User-Agent</c>; every call but <c>/pair</c> carries the binding's bearer token. Bodies are
/// sent with a Content-Length (serialised first), never chunked. The token is never logged here, and
/// a transient failure's reason names the status or the exception's TYPE — not a body, not a header.
/// </summary>
public sealed class PlatformClient(HttpClient http, string agentVersion) : IPlatformClient
{
    public Task<PlatformResult<PairResponse>> PairAsync(Uri endpoint, string code, CancellationToken ct) =>
        PostAsync<PairResponse>(endpoint, "/pair", new PairRequest(code), token: null, ct);

    public Task<PlatformResult<ClaimResponse>> ClaimAsync(Uri endpoint, string token, CancellationToken ct) =>
        PostAsync<ClaimResponse>(endpoint, "/jobs/claim", new ClaimRequest(), token, ct);

    public Task<PlatformResult<CompleteResponse>> CompleteAsync(Uri endpoint, string token, CompleteRequest request, CancellationToken ct) =>
        PostAsync<CompleteResponse>(endpoint, "/jobs/complete", request, token, ct);

    public Task<PlatformResult<FailResponse>> FailAsync(Uri endpoint, string token, FailRequest request, CancellationToken ct) =>
        PostAsync<FailResponse>(endpoint, "/jobs/fail", request, token, ct);

    public Task<PlatformResult<bool>> UnpairAsync(Uri endpoint, string token, CancellationToken ct) =>
        NoContentAsync(endpoint, "/unpair", new UnpairRequest(), token, ct);

    public Task<PlatformResult<bool>> OfflineAsync(Uri endpoint, string token, CancellationToken ct) =>
        NoContentAsync(endpoint, "/offline", new OfflineRequest(), token, ct);

    /// <summary>A route answered <c>204</c>: done, or the contract's refusal, or a transient failure.</summary>
    private async Task<PlatformResult<bool>> NoContentAsync(Uri endpoint, string path, object body, string token, CancellationToken ct)
    {
        var result = await PostAsync<JsonElement>(endpoint, path, body, token, ct);
        if (result.Refusal is not null) return PlatformResult<bool>.Refused(result.Refusal, result.Status);
        if (result.Transient is not null) return PlatformResult<bool>.Failed(result.Transient, result.Status);
        return PlatformResult<bool>.Ok(true, result.Status);
    }

    public Task<PlatformResult<UpdateResponse>> CheckUpdateAsync(Uri endpoint, string token, UpdateRequest request, CancellationToken ct) =>
        PostAsync<UpdateResponse>(endpoint, "/update", request, token, ct);

    /// <summary>
    /// The installer's bytes, streamed into <paramref name="destination"/> as they arrive (≈ 40 MB: never held
    /// whole in memory). The CALLER checks them against <see cref="AgentRelease.Sha256"/> and
    /// <see cref="AgentRelease.Bytes"/>; a refusal comes back in the contract's shape, like every other call.
    /// The HttpClient given to this client for a download needs a timeout in minutes, not the 30 s of the
    /// other calls (<c>AgentServices.DownloadHttp</c>).
    /// </summary>
    public async Task<PlatformResult<long>> DownloadUpdateAsync(Uri endpoint, string token, string version, Stream destination, CancellationToken ct)
    {
        using var request = Request(endpoint, "/update/download", new UpdateDownloadRequest(version), token);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return PlatformResult<long>.Failed("délai dépassé");
        }
        catch (HttpRequestException error)
        {
            return PlatformResult<long>.Failed($"réseau ({error.HttpRequestError})");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var text = await ReadTextAsync(response, ct);
                if (status >= 500 || text is null) return PlatformResult<long>.Failed($"erreur {status} de la plateforme", status);
                return Refusal<long>(text, status);
            }
            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                var before = destination.CanSeek ? destination.Position : 0;
                await body.CopyToAsync(destination, ct);
                return PlatformResult<long>.Ok(destination.CanSeek ? destination.Position - before : -1, status);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return PlatformResult<long>.Failed("délai dépassé pendant le téléchargement", status);
            }
            catch (Exception error) when (error is HttpRequestException or IOException)
            {
                return PlatformResult<long>.Failed($"téléchargement interrompu ({error.GetType().Name})", status);
            }
        }
    }

    /// <summary>The endpoint the binding holds, plus a route: <c>https://host/api/agent</c> + <c>/jobs/claim</c>.</summary>
    internal static Uri Route(Uri endpoint, string path) => new(endpoint.AbsoluteUri.TrimEnd('/') + path);

    private HttpRequestMessage Request(Uri endpoint, string path, object body, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route(endpoint, path))
        {
            Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), AgentJson.Options), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(ContractNames.ProtocolHeader, AgentProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(ContractNames.VersionHeader, agentVersion);
        request.Headers.UserAgent.ParseAdd($"PayrollSageAgent/{agentVersion}");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<string?> ReadTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>A 4xx body: the contract's refusal, or a transient failure when it is not one.</summary>
    private static PlatformResult<T> Refusal<T>(string text, int status)
    {
        try
        {
            var refusal = JsonSerializer.Deserialize<ErrorResponse>(text, AgentJson.Options);
            return refusal?.Error is { Code: not null } error
                ? PlatformResult<T>.Refused(error, status)
                : PlatformResult<T>.Failed($"refus {status} hors contrat", status);
        }
        catch (JsonException)
        {
            return PlatformResult<T>.Failed($"réponse {status} illisible", status);
        }
    }

    private async Task<PlatformResult<T>> PostAsync<T>(Uri endpoint, string path, object body, string? token, CancellationToken ct)
    {
        using var request = Request(endpoint, path, body, token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return PlatformResult<T>.Failed("délai dépassé");
        }
        catch (HttpRequestException error)
        {
            return PlatformResult<T>.Failed($"réseau ({error.HttpRequestError})");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.NoContent) return PlatformResult<T>.Ok(default!, status);

            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException error)
            {
                return PlatformResult<T>.Failed($"réponse interrompue ({error.HttpRequestError})", status);
            }

            if (status >= 500) return PlatformResult<T>.Failed($"erreur {status} de la plateforme", status);

            if (!response.IsSuccessStatusCode) return Refusal<T>(text, status);
            try
            {
                var value = JsonSerializer.Deserialize<T>(text, AgentJson.Options);
                return value is null ? PlatformResult<T>.Failed($"réponse {status} vide", status) : PlatformResult<T>.Ok(value, status);
            }
            catch (JsonException)
            {
                return PlatformResult<T>.Failed($"réponse {status} illisible", status);
            }
        }
    }
}
