using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// GET and DELETE /api/v1/oauth-clients/ (#881): administrators see which OAuth clients have access and
/// revoke one. That only administrators may is the authorization matrix's.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class OAuthClientEndpointsTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task Revoke_RegisteredClient_RemovesItAndItsGrantsAndIsAudited()
    {
        string clientId = await RegisterAsync($"Test MCP {Guid.NewGuid():N}");
        await GrantAsync(clientId);

        var listed = await ClientAsync(clientId);
        listed.GetProperty("registered").GetBoolean().Should().BeTrue();
        listed.GetProperty("activeGrants").GetInt32().Should().Be(1);
        listed.GetProperty("users").GetInt32().Should().Be(1);

        using var revoked = await fixture.AdminClient.DeleteAsync($"/api/v1/oauth-clients/{clientId}");
        revoked.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await revoked.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("clientRemoved").GetBoolean().Should().BeTrue();
        body.GetProperty("refreshTokensRevoked").GetInt32().Should().Be(1);

        (await ListAsync()).Should().NotContain(c => c.GetProperty("clientId").GetString() == clientId);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();
            (await db.RefreshTokens.Where(t => t.ClientId == clientId).AllAsync(t => t.RevokedAt != null)).Should().BeTrue();
        }
        var audit = await fixture.AdminClient.GetFromJsonAsync<JsonElement>($"/api/v1/audit/?action=oauth.client.revoked&resourceId={clientId}");
        audit.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Revoke_TwiceOrUnknown_IsOAuthClientNotFound()
    {
        string clientId = await RegisterAsync($"Test MCP {Guid.NewGuid():N}");
        (await fixture.AdminClient.DeleteAsync($"/api/v1/oauth-clients/{clientId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var again = await fixture.AdminClient.DeleteAsync($"/api/v1/oauth-clients/{clientId}");

        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString().Should().EndWith("#oauth-client-not-found");
    }

    [Fact]
    public async Task List_ClientKnownOnlyByItsGrants_IsListedAsNotRegistered()
    {
        // A client identified by its metadata URL never registers; its refresh tokens are what show it.
        string clientId = $"https://mcp-{Guid.NewGuid():N}.example.com/client.json";
        await GrantAsync(clientId);

        var listed = await ClientAsync(clientId);

        listed.GetProperty("registered").GetBoolean().Should().BeFalse();
        listed.GetProperty("activeGrants").GetInt32().Should().Be(1);
        (await fixture.AdminClient.DeleteAsync($"/api/v1/oauth-clients/{Uri.EscapeDataString(clientId)}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListAsync()).Should().NotContain(c => c.GetProperty("clientId").GetString() == clientId);
    }

    private async Task<string> RegisterAsync(string name)
    {
        using var client = fixture.Factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = name,
            redirect_uris = new[] { "http://127.0.0.1:8765/callback" },
            application_type = "native",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    /// <summary>A live refresh token the admin granted the client, as a completed sign-in leaves.</summary>
    private async Task GrantAsync(string clientId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();
        db.RefreshTokens.Add(new RefreshTokenEntity
        {
            UserId = AdminUserId(),
            TokenHash = Guid.NewGuid().ToString("N"),
            ClientId = clientId,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<JsonElement>> ListAsync() =>
        (await fixture.AdminClient.GetFromJsonAsync<JsonElement>("/api/v1/oauth-clients/")).EnumerateArray().ToList();

    private async Task<JsonElement> ClientAsync(string clientId) =>
        (await ListAsync()).Single(c => c.GetProperty("clientId").GetString() == clientId);

    private Guid AdminUserId()
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(fixture.AdminToken);
        string? sub = token.Claims.FirstOrDefault(c =>
            c.Type is "sub" or "nameid" or "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
        Guid.TryParse(sub, out Guid id).Should().BeTrue();
        return id;
    }
}
