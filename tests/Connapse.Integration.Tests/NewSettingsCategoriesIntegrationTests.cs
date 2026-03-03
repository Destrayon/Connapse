using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for v0.3.0 settings categories: llm, awssso, azuread.
/// Verifies GET/PUT roundtrip and live reload for new categories.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class NewSettingsCategoriesIntegrationTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // ── LLM Settings ─────────────────────────────────────────────────

    [Fact]
    public async Task GetSettings_Llm_ReturnsDefaults()
    {
        var response = await fixture.AdminClient.GetAsync("/api/settings/llm");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var settings = await response.Content.ReadFromJsonAsync<LlmSettings>(JsonOptions);
        settings.Should().NotBeNull();
        settings!.Provider.Should().NotBeNullOrWhiteSpace();
        settings.Model.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task UpdateSettings_Llm_PersistsValues()
    {
        // Get current to restore later
        var getResponse = await fixture.AdminClient.GetAsync("/api/settings/llm");
        var original = await getResponse.Content.ReadFromJsonAsync<LlmSettings>(JsonOptions);

        // Update
        var updated = original! with { Model = "gpt-4o-test", Provider = "OpenAI", ApiKey = "sk-test-key" };
        var putResponse = await fixture.AdminClient.PutAsJsonAsync("/api/settings/llm", updated);
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await Task.Delay(500);

        // Verify
        var verifyResponse = await fixture.AdminClient.GetAsync("/api/settings/llm");
        var verified = await verifyResponse.Content.ReadFromJsonAsync<LlmSettings>(JsonOptions);
        verified!.Model.Should().Be("gpt-4o-test");
        verified.Provider.Should().Be("OpenAI");

        // Restore
        await fixture.AdminClient.PutAsJsonAsync("/api/settings/llm", original);
    }

    // ── AWS SSO Settings ─────────────────────────────────────────────

    [Fact]
    public async Task GetSettings_AwsSso_ReturnsDefaults()
    {
        var response = await fixture.AdminClient.GetAsync("/api/settings/awssso");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var settings = await response.Content.ReadFromJsonAsync<AwsSsoSettings>(JsonOptions);
        settings.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateSettings_AwsSso_PersistsValues()
    {
        var getResponse = await fixture.AdminClient.GetAsync("/api/settings/awssso");
        var original = await getResponse.Content.ReadFromJsonAsync<AwsSsoSettings>(JsonOptions);

        var updated = new AwsSsoSettings
        {
            IssuerUrl = "https://test-issuer.awsapps.com/start",
            Region = "us-west-2"
        };
        var putResponse = await fixture.AdminClient.PutAsJsonAsync("/api/settings/awssso", updated);
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await Task.Delay(500);

        var verifyResponse = await fixture.AdminClient.GetAsync("/api/settings/awssso");
        var verified = await verifyResponse.Content.ReadFromJsonAsync<AwsSsoSettings>(JsonOptions);
        verified!.IssuerUrl.Should().Be("https://test-issuer.awsapps.com/start");
        verified.Region.Should().Be("us-west-2");

        // Restore
        await fixture.AdminClient.PutAsJsonAsync("/api/settings/awssso", original);
    }

    // ── Azure AD Settings ────────────────────────────────────────────

    [Fact]
    public async Task GetSettings_AzureAd_ReturnsDefaults()
    {
        var response = await fixture.AdminClient.GetAsync("/api/settings/azuread");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var settings = await response.Content.ReadFromJsonAsync<AzureAdSettings>(JsonOptions);
        settings.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateSettings_AzureAd_PersistsValues()
    {
        var getResponse = await fixture.AdminClient.GetAsync("/api/settings/azuread");
        var original = await getResponse.Content.ReadFromJsonAsync<AzureAdSettings>(JsonOptions);

        var updated = new AzureAdSettings
        {
            ClientId = "test-client-id",
            TenantId = "test-tenant-id",
            ClientSecret = "test-secret"
        };
        var putResponse = await fixture.AdminClient.PutAsJsonAsync("/api/settings/azuread", updated);
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await Task.Delay(500);

        var verifyResponse = await fixture.AdminClient.GetAsync("/api/settings/azuread");
        var verified = await verifyResponse.Content.ReadFromJsonAsync<AzureAdSettings>(JsonOptions);
        verified!.ClientId.Should().Be("test-client-id");
        verified.TenantId.Should().Be("test-tenant-id");

        // Restore
        await fixture.AdminClient.PutAsJsonAsync("/api/settings/azuread", original);
    }
}
