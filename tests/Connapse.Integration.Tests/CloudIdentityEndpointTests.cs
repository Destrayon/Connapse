using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

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
    public async Task AwsDeviceAuth_NotConfigured_Returns400()
    {
        // AWS SSO is not configured in integration test environment
        var response = await fixture.AdminClient.PostAsync(
            "/api/v1/auth/cloud/aws/device-auth", null);

        // Should return 400 since AWS SSO settings are not configured
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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

    // ── DTOs ──────────────────────────────────────────────────────────

    private record IdentitiesResponse(
        List<CloudIdentityDto> Identities,
        bool AwsSsoConfigured,
        bool AzureAdConfigured);

    private record CloudIdentityDto(
        string Provider,
        string DisplayName,
        DateTime LinkedAt);
}
