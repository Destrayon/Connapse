using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// A refreshed OAuth access token keeps its client and exactly the scopes the client was granted (#934). It
/// used to be rebuilt from the user's roles alone, so it read as a sign-in with every scope they imply.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class OAuthRefreshScopeTests(SharedWebAppFixture fixture)
{
    private const string Redirect = "http://127.0.0.1:8765/callback";

    [Fact]
    public async Task Refresh_ClientGrantedKnowledgeRead_KeepsJustThatScope()
    {
        using var anonymous = fixture.Factory.CreateClient();
        string clientId = await RegisterAsync(anonymous);
        var granted = await GrantAsync(anonymous, clientId, "knowledge:read");

        var refreshed = await TokenAsync(anonymous, new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = granted.GetProperty("refresh_token").GetString()!,
            ["client_id"] = clientId,
        });

        using var client = Bearer(refreshed.GetProperty("access_token").GetString()!);
        (await client.GetAsync("/api/v1/auth/links")).StatusCode.Should().Be(HttpStatusCode.OK, "the token still has knowledge:read");
        (await client.GetAsync("/api/v1/audit/?take=1")).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the administrator granted the client knowledge:read, not admin:audit");
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(refreshed.GetProperty("access_token").GetString()).Claims.ToList();
        claims.Where(c => c.Type == "scope").Select(c => c.Value).Should().Equal("knowledge:read");
        claims.Should().Contain(c => c.Type == "client_id" && c.Value == clientId);
    }

    [Fact]
    public async Task Refresh_ClientsTokenStoredWithoutItsScope_IsRefused()
    {
        // A client's refresh token from before the scope was stored can't say what it was granted, so it is
        // refused and the client signs in again.
        using var anonymous = fixture.Factory.CreateClient();
        string clientId = await RegisterAsync(anonymous);
        string refreshToken;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var pair = await scope.ServiceProvider.GetRequiredService<ITokenService>().GenerateTokenPairAsync(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, AdminUserId().ToString())], AdminUserId());
            refreshToken = pair.RefreshToken;
            var db = scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
            var row = await db.RefreshTokens.SingleAsync(t => t.TokenHash == hash);
            row.ClientId = clientId;
            await db.SaveChangesAsync();
        }

        using var response = await anonymous.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    [Fact]
    public async Task OAuthRefresh_SignInsRefreshToken_IsRefusedAndStillWorksWhereItBelongs()
    {
        // A sign-in's refresh token belongs to /api/v1/auth/token/refresh. Sent to /oauth/token with a client_id
        // it would be rotated and bound to that client with no scope, which no refresh accepts afterwards.
        using var anonymous = fixture.Factory.CreateClient();
        string clientId = await RegisterAsync(anonymous);
        string refreshToken;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            refreshToken = (await scope.ServiceProvider.GetRequiredService<ITokenService>().GenerateTokenPairAsync(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, AdminUserId().ToString())], AdminUserId())).RefreshToken;

        using var oauth = await anonymous.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        }));

        oauth.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await oauth.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("invalid_grant");
        using var signIn = await anonymous.PostAsJsonAsync("/api/v1/auth/token/refresh", new { refreshToken });
        signIn.StatusCode.Should().Be(HttpStatusCode.OK, "the refused request must not have rotated the sign-in's token");
    }

    [Fact]
    public async Task Migration_AddsANullableScopeColumnToRefreshTokens()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();

        var nullable = await db.Database.SqlQuery<string>(
            $"SELECT is_nullable AS \"Value\" FROM information_schema.columns WHERE table_name = 'refresh_tokens' AND column_name = 'scope'").ToListAsync();

        nullable.Should().Equal("YES");
    }

    private async Task<JsonElement> GrantAsync(HttpClient anonymous, string clientId, string scope)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        string code;
        await using (var services = fixture.Factory.Services.CreateAsyncScope())
            code = await services.ServiceProvider.GetRequiredService<OAuthAuthCodeService>()
                .CreateAsync(AdminUserId(), clientId, challenge, Redirect, scope);
        return await TokenAsync(anonymous, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = Redirect,
            ["client_id"] = clientId,
        });
    }

    private static async Task<string> RegisterAsync(HttpClient anonymous)
    {
        using var registered = await anonymous.PostAsJsonAsync("/oauth/register",
            new { client_name = $"refresh-{Guid.NewGuid():N}", redirect_uris = new[] { Redirect }, application_type = "native" });
        registered.EnsureSuccessStatusCode();
        return (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    private static async Task<JsonElement> TokenAsync(HttpClient client, Dictionary<string, string> form)
    {
        using var response = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(form));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private HttpClient Bearer(string token)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
