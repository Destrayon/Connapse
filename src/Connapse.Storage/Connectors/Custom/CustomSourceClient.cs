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
        string? token = null;
        do
        {
            CustomPage<CustomScopeItem> page = await GetJsonAsync<CustomPage<CustomScopeItem>>(
                "/v1/scopes" + Query(("page_token", token)), ct);
            scopes.AddRange(page.Items ?? []);
            token = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(token));
        return scopes;
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
            throw new FileNotFoundException($"The middle man no longer has document '{id}'.", id);
        }

        await EnsureSuccessAsync(response, path, ct);
        return await response.Content.ReadAsStreamAsync(ct);
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(path, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureSuccessAsync(response, path, ct);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new CustomSourceException($"The middle man answered {path} with an empty body.");
        }
        catch (JsonException ex)
        {
            throw new CustomSourceException($"The middle man answered {path} with JSON that does not match the protocol.", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string path, HttpCompletionOption completion, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, _baseUrl + path);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            return await http.SendAsync(request, completion, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new CustomSourceException($"Could not reach the middle man at {_baseUrl}: {ex.Message}", ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        string detail = await ProblemTitleAsync(response, ct);
        int status = (int)response.StatusCode;

        throw response.StatusCode switch
        {
            HttpStatusCode.Gone => new CustomSourceCursorExpiredException(
                $"The middle man answered {path} with 410: its cursor or page token is no longer valid."),
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => new CustomSourceRateLimitedException(
                $"The middle man answered {status} and asked Connapse to wait"
                + (retryAfter is { } wait ? $" {Math.Ceiling(wait.TotalSeconds)} s" : "") + ".", retryAfter),
            _ when status is >= 300 and < 400 => new CustomSourceException(
                $"The middle man answered {path} with a {status} redirect, which Connapse does not follow."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new CustomSourceException(
                $"The middle man refused the token ({status}). Check the token entered for this source."),
            _ => new CustomSourceException($"The middle man answered {path} with {status}{detail}."),
        };
    }

    private static async Task<string> ProblemTitleAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentType?.MediaType is not ("application/problem+json" or "application/json"))
            return "";
        try
        {
            using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return problem.RootElement.TryGetProperty("title", out JsonElement title) && title.ValueKind == JsonValueKind.String
                ? $": {title.GetString()}"
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
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

public sealed record CustomCapabilities(bool Changes, bool Tombstones, int? MinSyncIntervalSeconds, int? MaxPageSize);

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
    string? Url);

public sealed record CustomListingPage(IReadOnlyList<CustomDocument>? Items, string? NextPageToken, string? ChangeCursor);

public sealed record CustomChangePage(IReadOnlyList<CustomDocument>? Items, string? NextPageToken, string? NextCursor);

/// <summary>The middle man failed or answered outside the protocol.</summary>
public class CustomSourceException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>A 410: the cursor or page token is no longer valid, so the source must be listed again.</summary>
public sealed class CustomSourceCursorExpiredException(string message) : CustomSourceException(message);

/// <summary>A 429 or 503: the middle man asked Connapse to come back later.</summary>
public sealed class CustomSourceRateLimitedException(string message, TimeSpan? retryAfter) : CustomSourceException(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
