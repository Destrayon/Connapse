using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for CloudIdentity endpoints (/api/v1/auth/cloud/).
/// Tests basic endpoint availability and auth requirements.
/// External OAuth flows (Azure, AWS) require mocked providers and are not covered here.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CloudIdentityEndpointTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task ListIdentities_Authenticated_Returns200WithEmptyList()
    {
        // Act
        var response = await fixture.AdminClient.GetAsync("/api/v1/auth/cloud/identities");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<IdentitiesResponse>(JsonOptions);
        result.Should().NotBeNull();
        result!.Identities.Should().NotBeNull();
        result.Identities.Should().BeEmpty("admin user has no linked cloud identities");
    }

    [Fact]
    public async Task ListIdentities_Unauthenticated_Returns401()
    {
        // Arrange
        using var anonClient = fixture.Factory.CreateClient();

        // Act
        var response = await anonClient.GetAsync("/api/v1/auth/cloud/identities");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AzureConnect_NotConfigured_Returns400()
    {
        // Azure AD is not configured in integration test environment
        var response = await fixture.AdminClient.GetAsync("/api/v1/auth/cloud/azure/connect");

        // Should return 400 since Azure AD settings are not configured
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("azure_ad_not_configured");
    }

    [Fact]
    public async Task DisconnectIdentity_NoIdentity_Returns404()
    {
        // Act: Try to disconnect AWS identity that doesn't exist
        var response = await fixture.AdminClient.DeleteAsync(
            "/api/v1/auth/cloud/AWS");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DisconnectAws_WithASamlLink_DeletesTheLinkAndReturns204()
    {
        // The route used to delete from the cloud-identity table, which never holds a SAML link,
        // so a user with a real link got 404 and stayed eligible for AWS-derived search scopes.
        Guid admin = AdminUserId();
        string directoryUserId = $"dir-{Guid.NewGuid():N}";

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<AwsIdentityLinkStore>();
            await store.SaveAsync(admin, directoryUserId, "admin-person", "admin@example.com");
        }

        try
        {
            var response = await fixture.AdminClient.DeleteAsync("/api/v1/auth/cloud/AWS");

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await LinkedDirectoryUserIdAsync(admin)).Should().BeNull("the link was deleted");
        }
        finally
        {
            // Leave the shared admin as the other tests expect to find them: unlinked.
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<AwsIdentityLinkStore>();
            await store.DeleteAsync(admin);
        }
    }

    /// <summary>The admin's own user id, read from the token the fixture signed in with.</summary>
    private Guid AdminUserId()
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(fixture.AdminToken);
        string? sub = token.Claims.FirstOrDefault(c =>
            c.Type is "sub" or "nameid"
                   or "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

        Guid.TryParse(sub, out Guid id).Should().BeTrue("the token identifies the signed-in user");
        return id;
    }

    private async Task<string?> LinkedDirectoryUserIdAsync(Guid userId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<AwsIdentityLinkStore>();
        return await store.GetDirectoryUserIdAsync(userId);
    }

    // ── DTOs ──────────────────────────────────────────────────────────

    private record IdentitiesResponse(
        List<CloudIdentityDto> Identities,
        bool AzureAdConfigured);

    private record CloudIdentityDto(
        string Provider,
        string DisplayName,
        DateTime LinkedAt);
}
