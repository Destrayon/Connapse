using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>
/// Reads one Atlassian Cloud site's Confluence API as a service account. Every request is pinned
/// to <c>https://api.atlassian.com/ex/confluence/{cloudId}</c>: a relative path is resolved
/// against it, and an absolute address (a paging link comes from the network) is followed only
/// when it points back at that same site on that same host.
/// </summary>
/// <param name="downloads">
/// The client <see cref="DownloadAsync"/> uses. It must not follow redirects itself (see
/// <see cref="DownloadHttpClientName"/>); without one, <paramref name="http"/> is used, which is
/// only right when it does not follow them either, as in tests.
/// </param>
public sealed class AtlassianApiClient(
    HttpClient http, AtlassianTokenSource tokens, AtlassianSite site, string clientSecret, TimeProvider? time = null,
    HttpClient? downloads = null)
{
    /// <summary>The name of the <see cref="IHttpClientFactory"/> client this class is built on.</summary>
    public const string HttpClientName = "Atlassian";

    /// <summary>
    /// The named client for downloads, registered with automatic redirects off so that
    /// <see cref="DownloadAsync"/> sees, and checks, every hop.
    /// </summary>
    public const string DownloadHttpClientName = "AtlassianDownload";

    private HttpClient DownloadClient => downloads ?? http;

    public const string ApiHost = "api.atlassian.com";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private string SitePath => $"/ex/confluence/{site.CloudId}";

    public Uri ConfluenceBase => new($"https://{ApiHost}{SitePath}/wiki/");

    public async Task<T> GetJsonAsync<T>(string relative, CancellationToken ct)
    {
        Uri url = Resolve(relative);
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
            ?? throw new InvalidOperationException("Atlassian returned an empty answer for " + url.AbsolutePath);
    }

    /// <summary>
    /// Posts a JSON body and hands back the response for the caller to read the status of. The
    /// caller owns (and must dispose) it. Rate limits and rejected credentials still throw.
    /// </summary>
    public Task<HttpResponseMessage> PostAsync(string relative, object body, CancellationToken ct)
    {
        Uri url = Resolve(relative);
        return SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) }, ct);
    }

    /// <summary>
    /// Walks a paged list. Each response's <c>results</c> go through <paramref name="results"/>;
    /// the next page is <c>_links.next</c>, or failing that the <c>Link: rel="next"</c> header.
    /// </summary>
    public async IAsyncEnumerable<T> PageAsync<T>(
        string relative, Func<JsonElement, IEnumerable<T>> results, [EnumeratorCancellation] CancellationToken ct)
    {
        Uri? next = Resolve(relative);

        // A server that hands back a link it already gave would otherwise be walked forever.
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (next is not null)
        {
            Uri current = next;
            if (!visited.Add(current.AbsoluteUri))
                throw new InvalidOperationException("Atlassian repeated a paging link; refusing to loop.");

            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, current), ct);
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            foreach (T item in results(doc.RootElement))
                yield return item;

            string? link = NextFromBody(doc.RootElement) ?? NextFromHeader(response);
            next = link is null ? null : Resolve(link);
        }
    }

    public async Task<Stream> GetStreamAsync(string relative, CancellationToken ct)
    {
        Uri url = Resolve(relative);
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        try
        {
            response.EnsureSuccessStatusCode();
            return new ResponseStream(await response.Content.ReadAsStreamAsync(ct), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Downloads a file, following Confluence's redirect to Atlassian's media service. The named
    /// client does not follow redirects itself (it would carry on to any host), so each hop is
    /// checked here: it must be HTTPS on the default port to <c>api.atlassian.com</c> under this
    /// site, or to another <c>*.atlassian.com</c> host. Only this site's API gets the bearer token;
    /// a media address carries its own short-lived token in its query.
    /// </summary>
    public async Task<Stream> DownloadAsync(string relative, CancellationToken ct)
    {
        Uri url = Resolve(relative);
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct, DownloadClient);

        for (int hops = 0; IsRedirect(response.StatusCode); hops++)
        {
            Uri? location = response.Headers.Location;
            response.Dispose();

            Uri? target = location is null ? null : new Uri(url, location);
            if (hops >= MaxDownloadRedirects || target is null || !IsAtlassianHost(target))
                throw new InvalidOperationException("Refusing to follow a download redirect outside Atlassian.");

            url = target;
            if (string.Equals(target.Host, ApiHost, StringComparison.OrdinalIgnoreCase))
            {
                Uri pinned = Resolve(target.AbsoluteUri);
                response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, pinned), ct, DownloadClient);
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                response = await DownloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
        }

        try
        {
            response.EnsureSuccessStatusCode();
            return new ResponseStream(await response.Content.ReadAsStreamAsync(ct), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private const int MaxDownloadRedirects = 3;

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsAtlassianHost(Uri target) =>
        target.Scheme == Uri.UriSchemeHttps
        && target.IsDefaultPort
        && (string.Equals(target.Host, ApiHost, StringComparison.OrdinalIgnoreCase)
            || target.Host.EndsWith(".atlassian.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Sends with a bearer token. A 401 means the cached token was revoked or expired early, so it
    /// is dropped and the request is repeated once with a fresh one; a second 401 is the account
    /// itself being refused.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> build, CancellationToken ct, HttpClient? client = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            string token = await tokens.GetTokenAsync(site, clientSecret, ct);

            using var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await (client ?? http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = RetryAfterOf(response, _time);
                response.Dispose();
                throw new AtlassianRateLimitedException(wait);
            }

            if (response.StatusCode != HttpStatusCode.Unauthorized)
                return response;

            response.Dispose();
            tokens.Invalidate(site, token);
            if (attempt >= 1)
                throw new AtlassianAuthException(
                    "Atlassian rejected a freshly issued token. Check that the service account can use this site's Confluence API.");
        }
    }

    /// <summary>
    /// Turns a path or link into an address on this site's API, or throws. Paths starting
    /// <c>/wiki/</c> (what a v2 <c>next</c> link carries) are relative to the site root; any other
    /// relative path (a v1 <c>next</c> such as <c>/rest/api/search?...</c>) is relative to the wiki.
    /// </summary>
    private Uri Resolve(string pathOrLink)
    {
        if (pathOrLink.StartsWith("//", StringComparison.Ordinal) || pathOrLink.StartsWith('\\'))
            throw new InvalidOperationException("Refusing to follow a link that could name another host.");

        // Only an explicit scheme counts as absolute: on Linux a path like "/wiki/x" also parses
        // as an absolute file:// URI.
        if (pathOrLink.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || pathOrLink.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(pathOrLink, UriKind.Absolute, out Uri? absolute)
                || absolute.Scheme != Uri.UriSchemeHttps
                || !absolute.IsDefaultPort
                || !string.Equals(absolute.Host, ApiHost, StringComparison.OrdinalIgnoreCase)
                || !absolute.AbsolutePath.StartsWith(SitePath + "/", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to follow a link outside this site's Atlassian API.");
            return absolute;
        }

        Uri resolved = pathOrLink.StartsWith("/wiki/", StringComparison.Ordinal)
            ? new Uri($"https://{ApiHost}{SitePath}{pathOrLink}")
            : new Uri(ConfluenceBase, pathOrLink.TrimStart('/'));

        if (!string.Equals(resolved.Host, ApiHost, StringComparison.OrdinalIgnoreCase)
            || !resolved.AbsolutePath.StartsWith(SitePath + "/", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to follow a link outside this site's Atlassian API.");

        return resolved;
    }

    private static string? NextFromBody(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("_links", out var links) && links.ValueKind == JsonValueKind.Object
        && links.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String
            ? next.GetString()
            : null;

    private static string? NextFromHeader(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
            return null;

        foreach (string part in values.SelectMany(v => v.Split(',')))
        {
            string[] segments = part.Split(';');
            if (segments.Length < 2 || !segments.Skip(1).Any(s => s.Trim().Replace(" ", "") == "rel=\"next\""))
                continue;

            return segments[0].Trim().TrimStart('<').TrimEnd('>');
        }

        return null;
    }

    /// <summary><c>Retry-After</c> as a wait: either a number of seconds or an HTTP date.</summary>
    internal static TimeSpan? RetryAfterOf(HttpResponseMessage response, TimeProvider time)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date)
        {
            var wait = date - time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>Keeps the response alive for as long as its body is being read.</summary>
    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
