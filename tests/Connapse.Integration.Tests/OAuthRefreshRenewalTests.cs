using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// An MCP client renews its sign-in at /oauth/token and stays signed in across days, including when two sessions
/// sharing one sign-in refresh together; revoking the client, a stolen token or removing the user still ends it
/// at once (CON-360).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class OAuthRefreshRenewalTests(SharedWebAppFixture fixture)
{
    private const string Redirect = "http://127.0.0.1:8765/callback";

    [Fact]
    public async Task Refresh_RenewedRepeatedly_EachNewTokenLastsThirtyDaysUnused()
    {
        using var anonymous = fixture.Factory.CreateClient();
        string clientId = await RegisterAsync(anonymous);
        string refreshToken = (await GrantAsync(anonymous, clientId, AdminUserId())).GetProperty("refresh_token").GetString()!;

        for (int i = 0; i < 3; i++)
        {
            using var response = await RefreshAsync(anonymous, clientId, refreshToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            refreshToken = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refresh_token").GetString()!;
        }

        var row = await RowAsync(refreshToken);
        row.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(30), TimeSpan.FromMinutes(5));
        row.FamilyId.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_SameTokenTwiceAtOnce_BothSessionsStaySignedIn()
    {
        // Two sessions sharing stored credentials refresh the same token when its access token expires.
        using var anonymous = fixture.Factory.CreateClient();
        string clientId = await RegisterAsync(anonymous);
        string refreshToken = (await GrantAsync(anonymous, clientId, AdminUserId())).GetProperty("refresh_token").GetString()!;

        var responses = await Task.WhenAll(
            RefreshAsync(anonymous, clientId, refreshToken),
            RefreshAsync(anonymous, clientId, refreshToken));

        foreach (var response in responses)
        {
            using (response)
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                using var bearer = Bearer(body.GetProperty("access_token").GetString()!);
                (await bearer.GetAsync("/api/v1/auth/links")).StatusCode.Should().Be(HttpStatusCode.OK);
                using var next = await RefreshAsync(anonymous, clientId, body.GetProperty("refresh_token").GetString()!);
                next.StatusCode.Should().Be(HttpStatusCode.OK, "each session keeps renewing its own token");
            }
        }
    }

    [Fact]
    public async Task Refresh_RotatedTokenReusedAfterTheInterval_IsRefusedAndRevokesTheWholeFamily()
    {
        using var anonymous = fixture.Factory.CreateClient();
        string clientId = await RegisterAsync(anonymous);
        string first = (await GrantAsync(anonymous, clientId, AdminUserId())).GetProperty("refresh_token").GetString()!;
        string second = await RefreshedTokenAsync(anonymous, clientId, first);
        string third = await RefreshedTokenAsync(anonymous, clientId, second);
        await BackdateRotationAsync(first, TimeSpan.FromMinutes(2));

        using var replay = await RefreshAsync(anonymous, clientId, first);

        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("invalid_grant");
        using var holder = await RefreshAsync(anonymous, clientId, third);
        holder.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a token replayed after the interval may be stolen, so every token from that sign-in is revoked");
    }

    [Fact]
    public async Task Refresh_RotatedTokenReusedAfterTheClientIsRevoked_IsRefused()
    {
        using var anonymous = fixture.Factory.CreateClient();
        string clientId = await RegisterAsync(anonymous);
        string first = (await GrantAsync(anonymous, clientId, AdminUserId())).GetProperty("refresh_token").GetString()!;
        await RefreshedTokenAsync(anonymous, clientId, first);
        using (var admin = Bearer(fixture.AdminToken))
            (await admin.DeleteAsync($"/api/v1/oauth-clients/{Uri.EscapeDataString(clientId)}")).EnsureSuccessStatusCode();

        using var reuse = await RefreshAsync(anonymous, clientId, first);

        reuse.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the reuse interval never outlives a revoke");
    }

    [Fact]
    public async Task Refresh_RotatedTokenReusedAfterTheFamilyWasRevokedForTheft_IsRefused()
    {
        using var anonymous = fixture.Factory.CreateClient();
        string clientId = await RegisterAsync(anonymous);
        string first = (await GrantAsync(anonymous, clientId, AdminUserId())).GetProperty("refresh_token").GetString()!;
        string second = await RefreshedTokenAsync(anonymous, clientId, first);
        await RefreshedTokenAsync(anonymous, clientId, second);
        await BackdateRotationAsync(first, TimeSpan.FromMinutes(2));
        using (await RefreshAsync(anonymous, clientId, first)) { }

        using var reuse = await RefreshAsync(anonymous, clientId, second);

        reuse.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the second token is still inside the interval, but its family was revoked as stolen");
    }

    [Fact]
    public async Task Refresh_UserRemoved_IsRefused()
    {
        using var anonymous = fixture.Factory.CreateClient();
        string clientId = await RegisterAsync(anonymous);
        Guid userId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
            string email = $"renewal-{Guid.NewGuid():N}@test.connapse.io";
            var user = new ConnapseUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = email, CreatedAt = DateTime.UtcNow };
            (await users.CreateAsync(user, "RenewalTest1!")).Succeeded.Should().BeTrue();
            await users.AddToRoleAsync(user, "Viewer");
            userId = user.Id;
        }
        string refreshToken = (await GrantAsync(anonymous, clientId, userId)).GetProperty("refresh_token").GetString()!;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
            (await users.DeleteAsync((await users.FindByIdAsync(userId.ToString()))!)).Succeeded.Should().BeTrue();
        }

        using var response = await RefreshAsync(anonymous, clientId, refreshToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Migration_AddsANullableFamilyIdColumnToRefreshTokens()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();

        var nullable = await db.Database.SqlQuery<string>(
            $"SELECT is_nullable AS \"Value\" FROM information_schema.columns WHERE table_name = 'refresh_tokens' AND column_name = 'family_id'").ToListAsync();

        nullable.Should().Equal("YES");
    }

    private async Task<RefreshTokenEntity> RowAsync(string refreshToken)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();
        string hash = Hash(refreshToken);
        return await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.TokenHash == hash);
    }

    private async Task BackdateRotationAsync(string refreshToken, TimeSpan by)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();
        string hash = Hash(refreshToken);
        var row = await db.RefreshTokens.SingleAsync(t => t.TokenHash == hash);
        row.RevokedAt = DateTime.UtcNow - by;
        await db.SaveChangesAsync();
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static async Task<string> RefreshedTokenAsync(HttpClient anonymous, string clientId, string refreshToken)
    {
        using var response = await RefreshAsync(anonymous, clientId, refreshToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refresh_token").GetString()!;
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient anonymous, string clientId, string refreshToken) =>
        anonymous.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        }));

    private async Task<JsonElement> GrantAsync(HttpClient anonymous, string clientId, Guid userId)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        string code;
        await using (var services = fixture.Factory.Services.CreateAsyncScope())
            code = await services.ServiceProvider.GetRequiredService<OAuthAuthCodeService>()
                .CreateAsync(userId, clientId, challenge, Redirect, "knowledge:read");
        using var response = await anonymous.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = Redirect,
            ["client_id"] = clientId,
        }));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> RegisterAsync(HttpClient anonymous)
    {
        using var registered = await anonymous.PostAsJsonAsync("/oauth/register",
            new { client_name = $"renewal-{Guid.NewGuid():N}", redirect_uris = new[] { Redirect }, application_type = "native" });
        registered.EnsureSuccessStatusCode();
        return (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
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
