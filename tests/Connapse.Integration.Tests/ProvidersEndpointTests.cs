using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>Provider setup status over <c>GET /api/v1/providers</c> through the real host.</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class ProvidersEndpointTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task Status_Admin_ListsEachBuiltInProviderWithItsRequirements()
    {
        var response = await fixture.AdminClient.GetAsync("/api/v1/providers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var providers = await response.Content.ReadFromJsonAsync<JsonElement>();
        providers.EnumerateArray().Select(p => p.GetProperty("key").GetString())
            .Should().Equal("aws", "azure", "github", "atlassian");
        foreach (var provider in providers.EnumerateArray())
        {
            provider.GetProperty("overall").ValueKind.Should().Be(JsonValueKind.String, "statuses are named, not numbered");
            provider.GetProperty("requirements").EnumerateArray().Should().NotBeEmpty()
                .And.OnlyContain(r => r.GetProperty("status").ValueKind == JsonValueKind.String);
        }
    }

    [Fact]
    public async Task Status_Anonymous_Unauthorized()
    {
        using var anonymous = fixture.Factory.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/providers");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListCustom_Admin_ReturnsAnArray()
    {
        var response = await fixture.AdminClient.GetAsync("/api/v1/providers/custom");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListCustom_Anonymous_Unauthorized()
    {
        using var anonymous = fixture.Factory.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/providers/custom");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CheckCustom_UnknownProvider_NotFound()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync($"/api/v1/providers/custom/{Guid.NewGuid()}/check", new { });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString()
            .Should().EndWith("custom-provider-not-found");
    }

    [Fact]
    public async Task DeleteCustom_UnknownProvider_NotFound()
    {
        var response = await fixture.AdminClient.DeleteAsync($"/api/v1/providers/custom/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateCustom_NoToken_BadRequestNamingTheToken()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/v1/providers/custom", new { baseUrl = "https://example.com" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString().Should().Be("token");
    }
}
