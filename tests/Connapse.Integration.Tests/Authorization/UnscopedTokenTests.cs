using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Identity.Authentication;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Connapse.Integration.Tests.Authorization;

/// <summary>
/// A token issued with no scopes carries no scope claim, which every scope check used to read as a sign-in
/// (#932). Its owner is the administrator, so only the scope check stands between it and each route.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class UnscopedTokenTests(SharedWebAppFixture fixture)
{
    private async Task<HttpClient> UnscopedPatClientAsync()
    {
        using var created = await fixture.AdminClient.PostAsJsonAsync("/api/v1/auth/pats",
            new PatCreateRequest($"unscoped-{Guid.NewGuid():N}", [], RequiresApproval: false));
        created.EnsureSuccessStatusCode();
        var pat = (await created.Content.ReadFromJsonAsync<PatCreateResponse>())!;
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationOptions.HeaderName, pat.Token);
        return client;
    }

    [Theory]
    [InlineData("/api/v1/audit/?take=1")]
    [InlineData("/api/v1/oauth-clients/")]
    [InlineData("/api/v1/auth/links")]
    [InlineData("/api/v1/connections/")]
    [InlineData("/api/v1/atlassian/sites/")]
    public async Task ScopeCheckedRoute_AdministratorsTokenWithNoScopes_IsForbidden(string route)
    {
        using var client = await UnscopedPatClientAsync();

        using var response = await client.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("POST", "/pause")]
    [InlineData("DELETE", "")]
    public async Task ConnectionChange_AdministratorsReadOnlyToken_IsForbidden(string method, string suffix)
    {
        // An administrator's knowledge:read token keeps their roles; without admin:sources it can't pause or delete
        // a connection (CON-11).
        using var created = await fixture.AdminClient.PostAsJsonAsync("/api/v1/auth/pats",
            new PatCreateRequest($"read-{Guid.NewGuid():N}", ["knowledge:read"], RequiresApproval: false));
        created.EnsureSuccessStatusCode();
        var pat = (await created.Content.ReadFromJsonAsync<PatCreateResponse>())!;
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationOptions.HeaderName, pat.Token);

        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/v1/connections/{Guid.NewGuid()}{suffix}");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<HttpClient> AdminPatClientAsync(params string[] scopes)
    {
        using var created = await fixture.AdminClient.PostAsJsonAsync("/api/v1/auth/pats",
            new PatCreateRequest($"scoped-{Guid.NewGuid():N}", scopes, RequiresApproval: false));
        created.EnsureSuccessStatusCode();
        var pat = (await created.Content.ReadFromJsonAsync<PatCreateResponse>())!;
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationOptions.HeaderName, pat.Token);
        return client;
    }

    private static HttpRequestMessage SourceChange(string action)
    {
        string source = $"/api/sources/{Guid.NewGuid()}";
        return action switch
        {
            "create" => new HttpRequestMessage(HttpMethod.Post, "/api/sources/") { Content = JsonContent.Create(new { name = "x", connectionId = Guid.NewGuid() }) },
            "update" => new HttpRequestMessage(HttpMethod.Patch, source) { Content = JsonContent.Create(new { name = "renamed" }) },
            "sync" => new HttpRequestMessage(HttpMethod.Post, $"{source}/sync"),
            "retry-failed" => new HttpRequestMessage(HttpMethod.Post, $"{source}/retry-failed"),
            "delete" => new HttpRequestMessage(HttpMethod.Delete, $"{source}?confirmName=x"),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [Theory]
    [InlineData("create", "knowledge:read")]
    [InlineData("update", "knowledge:read")]
    [InlineData("sync", "knowledge:read")]
    [InlineData("retry-failed", "knowledge:read")]
    [InlineData("delete", "knowledge:read")]
    [InlineData("create", "knowledge:write")]
    [InlineData("update", "knowledge:write")]
    [InlineData("sync", "knowledge:write")]
    [InlineData("retry-failed", "knowledge:write")]
    [InlineData("delete", "knowledge:write")]
    [InlineData("create", null)]
    [InlineData("update", null)]
    [InlineData("sync", null)]
    [InlineData("retry-failed", null)]
    [InlineData("delete", null)]
    public async Task SourceChange_AdministratorsTokenWithoutAdminSources_IsForbidden(string action, string? scope)
    {
        // An administrator's token keeps their roles; without admin:sources it can't create, change, sync or delete a
        // source, the same rule connections follow (CON-124).
        using var client = await AdminPatClientAsync(scope is null ? [] : [scope]);

        using var request = SourceChange(action);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("sync")]
    [InlineData("retry-failed")]
    [InlineData("delete")]
    public async Task SourceChange_AdministratorsTokenWithAdminSources_IsLetThrough(string action)
    {
        using var client = await AdminPatClientAsync("admin:sources");

        using var request = SourceChange(action);
        using var response = await client.SendAsync(request);

        // The source doesn't exist, so a caller the rule admits is told so rather than refused.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task McpTools_AdministratorsTokenWithNoScopes_ListsNoScopedTool()
    {
        using var http = await UnscopedPatClientAsync();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http);
        await using var mcp = await McpClient.CreateAsync(transport);

        var tools = await mcp.ListToolsAsync();

        tools.Select(t => t.Name).Should().NotContain(["search_knowledge", "container_list", "container_delete"],
            "each needs a knowledge scope the token wasn't issued with");
    }

    [Theory]
    [InlineData("/api/v1/audit/?take=1")]
    [InlineData("/api/settings/embedding")]
    [InlineData("/api/v1/connections/")]
    public async Task ScopeCheckedRoute_OAuthClientGrantedNoScopes_IsForbidden(string route)
    {
        // An OAuth client's access token carries its client_id, and no scope claim when it was granted none; it is
        // a token all the same, so the administrator's roles don't stand in for scopes.
        using var client = await OAuthClientAsync("");

        using var response = await client.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("POST", "")]
    [InlineData("DELETE", "")]
    [InlineData("GET", "knowledge:read admin:users")]
    [InlineData("POST", "knowledge:read admin:users")]
    [InlineData("DELETE", "knowledge:read admin:users")]
    public async Task PersonalTokenRoute_OAuthClientToken_IsForbidden(string method, string scope)
    {
        // RequirePerson admits an OAuth client's token; the operation must not, or the client could mint its user a
        // personal access token with more scopes than it was granted, and keep it after its own grant is revoked.
        using var client = await OAuthClientAsync(scope);

        using var response = method switch
        {
            "GET" => await client.GetAsync("/api/v1/auth/pats"),
            "POST" => await client.PostAsJsonAsync("/api/v1/auth/pats", new PatCreateRequest($"oauth-{Guid.NewGuid():N}", ["admin:users"])),
            _ => await client.DeleteAsync($"/api/v1/auth/pats/{Guid.NewGuid()}"),
        };

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>A client holding an access token the administrator granted an OAuth client, with the given scopes.</summary>
    private async Task<HttpClient> OAuthClientAsync(string grantedScopes)
    {
        const string redirect = "http://127.0.0.1:8765/callback";
        using var anonymous = fixture.Factory.CreateClient();
        using var registered = await anonymous.PostAsJsonAsync("/oauth/register",
            new { client_name = $"unscoped-{Guid.NewGuid():N}", redirect_uris = new[] { redirect }, application_type = "native" });
        registered.EnsureSuccessStatusCode();
        string clientId = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string code;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            code = await scope.ServiceProvider.GetRequiredService<OAuthAuthCodeService>().CreateAsync(
                AdminUserId(), clientId, Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))), redirect, grantedScopes);
        using var exchanged = await anonymous.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirect,
            ["client_id"] = clientId,
        }));
        exchanged.EnsureSuccessStatusCode();
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await exchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString());
        return client;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private Guid AdminUserId()
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(fixture.AdminToken);
        string? sub = token.Claims.FirstOrDefault(c =>
            c.Type is "sub" or "nameid" or "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
        Guid.TryParse(sub, out Guid id).Should().BeTrue();
        return id;
    }
}
