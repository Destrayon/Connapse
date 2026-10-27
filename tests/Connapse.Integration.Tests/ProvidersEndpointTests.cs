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
}
