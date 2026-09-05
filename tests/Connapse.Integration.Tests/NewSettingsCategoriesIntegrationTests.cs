using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for v0.3.0 settings categories: llm.
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
}
