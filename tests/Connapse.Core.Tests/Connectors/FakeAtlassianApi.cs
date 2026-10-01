using System.Net;
using System.Text;
using System.Text.Json;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// An in-memory stand-in for Atlassian: the client-credentials token endpoint, and whatever
/// Confluence routes a test registers with <see cref="Map"/>. A token is accepted only while it is
/// in the issued set, so <see cref="RevokeTokens"/> makes the next call that carries one answer 401,
/// the way a rotated service-account secret does.
/// </summary>
public sealed class FakeAtlassianApi : HttpMessageHandler
{
    public const string TokenPath = "/oauth/token";

    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = [];
    private readonly HashSet<string> _validTokens = [];
    private int _tokensIssued;

    /// <summary>Requests received, by absolute path (the token endpoint included).</summary>
    public Dictionary<string, int> Calls { get; } = [];

    public int TokenRequests => Calls.GetValueOrDefault(TokenPath);

    /// <summary>Every request received, as absolute URL.</summary>
    public List<Uri> Requests { get; } = [];

    /// <summary>The bearer token each non-token request carried.</summary>
    public List<string?> BearerTokens { get; } = [];

    /// <summary>The <c>expires_in</c> the token endpoint reports, in seconds.</summary>
    public int TokenExpiresIn { get; set; } = 3600;

    /// <summary>Answers the next Confluence request 429 with a <c>Retry-After</c> of 30 seconds.</summary>
    public bool RateLimitNext { get; set; }

    /// <summary>Answers every Confluence request with this status until cleared with <c>FailWith(null)</c>.</summary>
    public HttpStatusCode? FailingStatus { get; private set; }

    public void FailWith(HttpStatusCode? status) => FailingStatus = status;

    /// <summary>Makes every token issued so far invalid; the next request carrying one gets 401.</summary>
    public void RevokeTokens() => _validTokens.Clear();

    /// <summary>Registers the answer for a Confluence path, such as <c>/ex/confluence/{id}/wiki/api/v2/spaces</c>.</summary>
    public void Map(string absolutePath, Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        _routes[absolutePath] = handler;

    public void MapJson(string absolutePath, object body) =>
        Map(absolutePath, _ => Json(body));

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    public HttpClient CreateClient() => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        Requests.Add(uri);
        Calls[uri.AbsolutePath] = Calls.GetValueOrDefault(uri.AbsolutePath) + 1;

        if (uri.Host == "auth.atlassian.com" && uri.AbsolutePath == TokenPath)
            return await IssueTokenAsync(request, ct);

        string? token = request.Headers.Authorization?.Parameter;
        BearerTokens.Add(token);

        if (token is null || !_validTokens.Contains(token))
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        if (RateLimitNext)
        {
            RateLimitNext = false;
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.TryAddWithoutValidation("Retry-After", "30");
            return limited;
        }

        if (FailingStatus is { } failing)
            return new HttpResponseMessage(failing);

        return _routes.TryGetValue(uri.AbsolutePath, out var route)
            ? route(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private async Task<HttpResponseMessage> IssueTokenAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (FailingStatus is { } failing)
            return new HttpResponseMessage(failing);

        string body = await request.Content!.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.GetProperty("grant_type").GetString() != "client_credentials")
            return new HttpResponseMessage(HttpStatusCode.BadRequest);

        string token = "token-" + ++_tokensIssued;
        _validTokens.Add(token);
        return Json(new { access_token = token, expires_in = TokenExpiresIn, token_type = "Bearer" });
    }
}
