using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connapse.Storage.Connectors.Custom;

/// <summary>
/// Calls one middle man over custom source protocol v1 (<c>docs/protocols/custom-source-v1.md</c>). Every
/// request goes to the base URL it was built with: paths are appended to it, never taken from responses, and
/// the named client never follows redirects, so nothing the middle man answers can point Connapse elsewhere.
/// </summary>
public sealed class CustomSourceClient(HttpClient http, string baseUrl, string? token)
{
    /// <summary>The named client, registered with redirects off.</summary>
    public const string HttpClientName = "CustomSource";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _baseUrl = baseUrl.TrimEnd('/');

    public Task<CustomDiscovery> GetDiscoveryAsync(CancellationToken ct) =>
        GetJsonAsync<CustomDiscovery>("/.well-known/connapse-source", ct);

    public async Task<IReadOnlyList<CustomScopeItem>> ListScopesAsync(CancellationToken ct)
    {
        List<CustomScopeItem> scopes = [];
        PageGuard guard = new("scopes");
        string? token = null;
        while (true)
        {
            CustomPage<CustomScopeItem> page = await GetJsonAsync<CustomPage<CustomScopeItem>>(
                "/v1/scopes" + Query(("page_token", token)), ct);
            scopes.AddRange(page.Items ?? []);
            if (string.IsNullOrEmpty(page.NextPageToken)) return scopes;
            guard.Next(page.NextPageToken);
            token = page.NextPageToken;
        }
    }

    public Task<CustomListingPage> ListDocumentsAsync(string scope, string? pageToken, CancellationToken ct) =>
        GetJsonAsync<CustomListingPage>(
            $"/v1/scopes/{Uri.EscapeDataString(scope)}/documents" + Query(("page_token", pageToken)), ct);

    public Task<CustomChangePage> ListChangesAsync(string scope, string? cursor, string? pageToken, CancellationToken ct) =>
        GetJsonAsync<CustomChangePage>(
            $"/v1/scopes/{Uri.EscapeDataString(scope)}/changes"
            + Query(pageToken is null ? ("cursor", cursor) : ("page_token", pageToken)), ct);

    /// <summary>The document's bytes. <see cref="FileNotFoundException"/> when the middle man says it is gone.</summary>
    public async Task<Stream> OpenContentAsync(string scope, string id, CancellationToken ct)
    {
        string path = $"/v1/scopes/{Uri.EscapeDataString(scope)}/documents/{Uri.EscapeDataString(id)}/content";
        HttpResponseMessage response = await SendAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            response.Dispose();
            throw new FileNotFoundException($"The source server no longer has document '{id}'.", id);
        }

        await EnsureSuccessAsync(response, path, ct);
        return await response.Content.ReadAsStreamAsync(ct);
    }

    /// <summary>
    /// The access keys <paramref name="userId"/> holds in <paramref name="scope"/>
    /// (<c>POST /access/v1/search/resource</c>), stopping after <paramref name="maxKeys"/>.
    /// </summary>
    public async Task<CustomAccessKeys> SearchAccessKeysAsync(string scope, string userId, int maxKeys, CancellationToken ct)
    {
        List<string> keys = [];
        PageGuard guard = new("access keys");
        string token = "";
        while (true)
        {
            CustomKeySearchResponse page = await PostJsonAsync<CustomKeySearchResponse>("/access/v1/search/resource", new
            {
                subject = new { type = "user", id = userId },
                action = new { name = "read" },
                resource = new { type = "access_key", properties = new { scope } },
                page = new { token, limit = Math.Min(1000, maxKeys) },
            }, ct);

            foreach (CustomResourceRef result in page.Results ?? [])
            {
                if (result.Type != "access_key" || string.IsNullOrEmpty(result.Id)) continue;
                if (keys.Count == maxKeys) return new CustomAccessKeys(keys, Truncated: true);
                keys.Add(result.Id);
            }

            string? next = page.Page?.NextToken;
            if (string.IsNullOrEmpty(next)) return new CustomAccessKeys(keys, Truncated: false);
            if (keys.Count == maxKeys) return new CustomAccessKeys(keys, Truncated: true);
            guard.Next(next);
            token = next;
        }
    }

    /// <summary>
    /// Whether <paramref name="userId"/> may read each document, in order (<c>POST /access/v1/evaluations</c>),
    /// in batches of at most <paramref name="maxBatch"/>. Only an explicit <c>decision: true</c> with no
    /// <c>context.error</c> is true. An answer that cannot be matched to the request — a failed call, or a
    /// different number of results — throws, so the caller denies every document in it.
    /// </summary>
    public async Task<IReadOnlyList<bool>> EvaluateAsync(
        string userId, string scope, IReadOnlyList<string> documentIds, int maxBatch, CancellationToken ct)
    {
        List<bool> decisions = new(documentIds.Count);
        foreach (string[] batch in documentIds.Chunk(Math.Max(1, maxBatch)))
        {
            CustomEvaluationsResponse response = await PostJsonAsync<CustomEvaluationsResponse>("/access/v1/evaluations", new
            {
                subject = new { type = "user", id = userId },
                action = new { name = "read" },
                evaluations = batch.Select(id => new
                {
                    resource = new { type = "document", id, properties = new { scope } },
                }).ToArray(),
            }, ct);

            IReadOnlyList<CustomDecision?> results = response.Evaluations ?? [];
            if (results.Count != batch.Length)
                throw new CustomSourceException(
                    $"The source server answered {results.Count} decision(s) for {batch.Length} document(s); none can be trusted.");

            decisions.AddRange(results.Select(r => r is { Decision: true } && r.Context?.Error is null));
        }
        return decisions;
    }

    /// <summary>
    /// Asks the middle man to activate the pending link behind <paramref name="code"/> for this Connapse user
    /// (<c>POST /v1/links/confirm</c>). Null when it refuses: the code is unknown, used, expired or was issued
    /// for another user (<c>409</c>).
    /// </summary>
    public async Task<CustomLinkStatus?> ConfirmLinkAsync(string code, string connapseUserId, CancellationToken ct)
    {
        const string path = "/v1/links/confirm";
        using HttpResponseMessage response = await SendAsync(path, HttpCompletionOption.ResponseContentRead, ct,
            JsonContent.Create(new { code, connapseUserId }, options: Json));
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        return await ReadJsonAsync<CustomLinkStatus>(response, path, ct);
    }

    /// <summary>Whether this Connapse user is linked, read live (<c>GET /v1/links/{id}</c>).</summary>
    public Task<CustomLinkStatus> GetLinkAsync(string connapseUserId, CancellationToken ct) =>
        GetJsonAsync<CustomLinkStatus>($"/v1/links/{Uri.EscapeDataString(connapseUserId)}", ct);

    /// <summary>Removes this Connapse user's link (<c>DELETE /v1/links/{id}</c>); a <c>404</c> counts as done.</summary>
    /// <param name="strict">
    /// Require the protocol's 204 even when there was no link. A 404 cannot be told apart from a server with no
    /// unlink endpoint at all, so the setup check is strict; removing a real user's link accepts it as gone.
    /// </param>
    public async Task UnlinkAsync(string connapseUserId, CancellationToken ct, bool strict = false)
    {
        string path = $"/v1/links/{Uri.EscapeDataString(connapseUserId)}";
        using HttpResponseMessage response = await SendAsync(path, HttpCompletionOption.ResponseContentRead, ct, method: HttpMethod.Delete);
        if (response.StatusCode == HttpStatusCode.NotFound && !strict) return;
        if (strict && response.StatusCode != HttpStatusCode.NoContent)
            throw new CustomSourceException($"The source server answered the unlink call with {(int)response.StatusCode}, not 204.");
        await EnsureSuccessAsync(response, path, ct);
    }

    private async Task<T> PostJsonAsync<T>(string path, object body, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(path, HttpCompletionOption.ResponseContentRead, ct, JsonContent.Create(body, options: Json));
        return await ReadJsonAsync<T>(response, path, ct);
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(path, HttpCompletionOption.ResponseContentRead, ct);
        return await ReadJsonAsync<T>(response, path, ct);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string path, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, path, ct);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new CustomSourceException($"The source server answered {Describe(path)} with an empty body.");
        }
        catch (JsonException ex)
        {
            throw new CustomSourceException($"The source server answered {Describe(path)} with JSON that does not match the protocol.", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        string path, HttpCompletionOption completion, CancellationToken ct, HttpContent? content = null, HttpMethod? method = null)
    {
        using HttpRequestMessage request = new(method ?? (content is null ? HttpMethod.Get : HttpMethod.Post), _baseUrl + path);
        request.Content = content;
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            return await http.SendAsync(request, completion, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new CustomSourceException($"Could not reach the source server at {_baseUrl}: " + ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "its host name does not resolve from Connapse. Check the address.",
                HttpRequestError.ConnectionError => "nothing answered. Check it is running and reachable from the Connapse server or container.",
                HttpRequestError.SecureConnectionError => "the HTTPS connection failed. Check its certificate is valid and trusted.",
                _ => "the connection failed. Check it is running and reachable from the Connapse server or container.",
            }, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CustomSourceException(
                $"Could not reach the source server at {_baseUrl}: it did not answer in time. Check it is running and not overloaded.", ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        int status = (int)response.StatusCode;

        // Only Connapse's own words: these messages reach sync errors, logs and the unlink queue, and a middle
        // man's problem title could quote the bearer token it was sent.

        throw response.StatusCode switch
        {
            HttpStatusCode.Gone => new CustomSourceCursorExpiredException(
                $"The source server answered {Describe(path)} with 410: its cursor or page token is no longer valid."),
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => new CustomSourceRateLimitedException(
                $"The source server answered {status} and asked Connapse to wait"
                + (retryAfter is { } wait ? $" {Math.Ceiling(wait.TotalSeconds)} s" : "") + ".", retryAfter),
            _ when status is >= 300 and < 400 => new CustomSourceException(
                $"The source server answered {Describe(path)} with a {status} redirect, which Connapse does not follow."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new CustomSourceUnauthorizedException(
                $"The source server refused the token ({status}). Enter the token configured on the source server."),
            _ => new CustomSourceException($"The source server answered {Describe(path)} with {status}. Check the source server's own logs."),
        };
    }

    /// <summary>
    /// The endpoint a request went to, by name. Never the path itself: its scope, document ID, page token and
    /// cursor are values the middle man chose, and a message can reach sync errors, logs and the setup screens.
    /// </summary>
    private static string Describe(string path)
    {
        string bare = path.Split('?')[0];
        return bare switch
        {
            "/.well-known/connapse-source" => "the discovery request",
            "/v1/scopes" => "the scopes listing",
            "/access/v1/evaluations" => "the permission check",
            "/access/v1/search/resource" => "the access key search",
            "/v1/links/confirm" => "the link confirmation",
            _ when bare.StartsWith("/v1/links/", StringComparison.Ordinal) => "a link request",
            _ when bare.EndsWith("/content", StringComparison.Ordinal) => "a document content request",
            _ when bare.EndsWith("/changes", StringComparison.Ordinal) => "the change feed",
            _ when bare.EndsWith("/documents", StringComparison.Ordinal) => "the document listing",
            _ => "a request",
        };
    }

    private static string Query(params (string Name, string? Value)[] parameters)
    {
        string[] parts = parameters.Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}")
            .ToArray();
        return parts.Length == 0 ? "" : "?" + string.Join('&', parts);
    }
}

public sealed record CustomDiscovery(IReadOnlyList<string>? ProtocolVersions, string? Name, CustomCapabilities? Capabilities);

public sealed record CustomCapabilities(
    bool Changes, bool Tombstones, int? MinSyncIntervalSeconds, int? MaxPageSize, CustomPermissionCapabilities? Permissions = null);

/// <summary>The <c>permissions</c> capabilities; <c>Check</c> absent means the middle man answers no permission questions.</summary>
public sealed record CustomPermissionCapabilities(CustomCheckCapability? Check, CustomAccessKeysCapability? AccessKeys, CustomLinkingCapability? Linking);

public sealed record CustomCheckCapability(int MaxBatch);

public sealed record CustomAccessKeysCapability(int CacheTtlSeconds);

public sealed record CustomLinkingCapability(string? LinkUrl);

/// <summary>The keys a user holds in a scope; <c>Truncated</c> when there were more than Connapse reads.</summary>
public sealed record CustomAccessKeys(IReadOnlyList<string> Keys, bool Truncated);

public sealed record CustomResourceRef(string? Type, string? Id);

public sealed record CustomPageOut(string? NextToken);

public sealed record CustomKeySearchResponse(IReadOnlyList<CustomResourceRef>? Results, CustomPageOut? Page);

public sealed record CustomDecisionContext(string? Error);

public sealed record CustomDecision(bool? Decision, CustomDecisionContext? Context);

public sealed record CustomEvaluationsResponse(IReadOnlyList<CustomDecision?>? Evaluations);

/// <summary>A user's link as the middle man reports it; <c>DisplayName</c> names their account in its system.</summary>
public sealed record CustomLinkStatus(bool Linked, string? DisplayName);

public sealed record CustomScopeItem(string Id, string Name, string? Description);

public sealed record CustomPage<T>(IReadOnlyList<T>? Items, string? NextPageToken);

public sealed record CustomDocument(
    string Id,
    string? Op,
    string? Path,
    string? Title,
    string? ContentType,
    long? Size,
    string? Version,
    DateTimeOffset? ModifiedAt,
    string? Url,
    IReadOnlyList<string>? AccessKeys = null);

public sealed record CustomListingPage(IReadOnlyList<CustomDocument>? Items, string? NextPageToken, string? ChangeCursor);

public sealed record CustomChangePage(IReadOnlyList<CustomDocument>? Items, string? NextPageToken, string? NextCursor);

/// <summary>The middle man failed or answered outside the protocol.</summary>
public class CustomSourceException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>No custom provider has the id, so there is no middle man to ask.</summary>
public sealed class CustomProviderNotFoundException(string message) : CustomSourceException(message);

/// <summary>A 401 or 403: the middle man refused the token.</summary>
public sealed class CustomSourceUnauthorizedException(string message) : CustomSourceException(message);

/// <summary>A 410: the cursor or page token is no longer valid, so the source must be listed again.</summary>
public sealed class CustomSourceCursorExpiredException(string message) : CustomSourceException(message);

/// <summary>A 429 or 503: the middle man asked Connapse to come back later.</summary>
public sealed class CustomSourceRateLimitedException(string message, TimeSpan? retryAfter) : CustomSourceException(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
