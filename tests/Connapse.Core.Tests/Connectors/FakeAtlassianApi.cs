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
    public const string TenantInfoPath = "/_edge/tenant_info";

    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = [];
    private readonly HashSet<string> _validTokens = [];
    private readonly object _gate = new();
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

    /// <summary>Answers every Confluence request (not the token endpoint) with this status until cleared with <c>FailWith(null)</c>.</summary>
    public HttpStatusCode? FailingStatus { get; private set; }

    /// <summary>Answers every token-endpoint request with this status until cleared with <c>FailTokenWith(null)</c>.</summary>
    public HttpStatusCode? FailingTokenStatus { get; private set; }

    /// <summary>The Content-Type of each token request.</summary>
    public List<string?> TokenContentTypes { get; } = [];

    public void FailWith(HttpStatusCode? status) { lock (_gate) FailingStatus = status; }

    public void FailTokenWith(HttpStatusCode? status) { lock (_gate) FailingTokenStatus = status; }

    /// <summary>Answers every token-endpoint request 200 with this raw JSON body until cleared with <c>AnswerTokenWith(null)</c>.</summary>
    public string? RawTokenBody { get; private set; }

    public void AnswerTokenWith(string? rawBody) { lock (_gate) RawTokenBody = rawBody; }

    /// <summary>Makes every token issued so far invalid; the next request carrying one gets 401.</summary>
    public void RevokeTokens() { lock (_gate) _validTokens.Clear(); }

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
        string? token = request.Headers.Authorization?.Parameter;
        bool isToken = uri.Host == "auth.atlassian.com" && uri.AbsolutePath == TokenPath;
        string? tokenBody = isToken ? await request.Content!.ReadAsStringAsync(ct) : null;

        Func<HttpRequestMessage, HttpResponseMessage>? route = null;
        lock (_gate)
        {
            Requests.Add(uri);
            Calls[uri.AbsolutePath] = Calls.GetValueOrDefault(uri.AbsolutePath) + 1;

            if (isToken)
            {
                TokenContentTypes.Add(request.Content!.Headers.ContentType?.MediaType);
                return IssueToken(tokenBody!);
            }

            // A site's tenant_info answers anyone, with no token.
            if (uri.AbsolutePath == TenantInfoPath)
            {
                return _routes.TryGetValue(TenantInfoPath, out var tenant)
                    ? tenant(request)
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

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

            _routes.TryGetValue(uri.AbsolutePath, out route);
        }

        return route is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : route(request);
    }

    // Caller holds _gate. Accepts the form body Atlassian documents, and JSON too.
    private HttpResponseMessage IssueToken(string body)
    {
        if (FailingTokenStatus is { } failing)
            return new HttpResponseMessage(failing);

        if (RawTokenBody is { } raw)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw, Encoding.UTF8, "application/json") };

        string? grantType = body.TrimStart().StartsWith('{')
            ? JsonDocument.Parse(body).RootElement.GetProperty("grant_type").GetString()
            : System.Web.HttpUtility.ParseQueryString(body)["grant_type"];
        if (grantType != "client_credentials")
            return new HttpResponseMessage(HttpStatusCode.BadRequest);

        string token = "token-" + ++_tokensIssued;
        _validTokens.Add(token);
        return Json(new { access_token = token, expires_in = TokenExpiresIn, token_type = "Bearer" });
    }
}
