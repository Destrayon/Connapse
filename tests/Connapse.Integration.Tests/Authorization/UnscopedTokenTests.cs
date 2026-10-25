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
    public async Task ScopeCheckedRoute_AdministratorsTokenWithNoScopes_IsForbidden(string route)
    {
        using var client = await UnscopedPatClientAsync();

        using var response = await client.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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

    [Fact]
    public async Task ScopeCheckedRoute_OAuthClientGrantedNoScopes_IsForbidden()
    {
        // An OAuth client's access token carries its client_id, and no scope claim when it was granted none; it is
        // a token all the same, so the administrator's roles don't stand in for scopes.
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
                AdminUserId(), clientId, Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))), redirect, "");
        using var exchanged = await anonymous.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirect,
            ["client_id"] = clientId,
        }));
        exchanged.EnsureSuccessStatusCode();
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await exchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString());

        using var response = await client.GetAsync("/api/v1/audit/?take=1");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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
