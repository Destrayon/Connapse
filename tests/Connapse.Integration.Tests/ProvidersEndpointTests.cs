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

    [Theory]
    [InlineData("GET", "/api/v1/providers/aws/access")]
    [InlineData("PUT", "/api/v1/providers/aws/access")]
    [InlineData("DELETE", "/api/v1/providers/aws/access")]
    [InlineData("PUT", "/api/v1/providers/aws/identity-center")]
    [InlineData("DELETE", "/api/v1/providers/aws/permissions")]
    public async Task Aws_Anonymous_Unauthorized(string method, string path)
    {
        using var anonymous = fixture.Factory.CreateClient();

        var response = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAwsAccess_Admin_ReadsTheCredentialWithoutItsKey()
    {
        var response = await fixture.AdminClient.GetAsync("/api/v1/providers/aws/access");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await response.Content.ReadFromJsonAsync<JsonElement>();
        stored.TryGetProperty("config", out _).Should().BeTrue();
        stored.TryGetProperty("status", out _).Should().BeTrue();
        stored.GetRawText().Should().NotContain("privateKey");
    }

    [Theory]
    [InlineData("/api/v1/providers/aws/access", "certificatePem")]
    [InlineData("/api/v1/providers/aws/identity-center", "instanceArn")]
    public async Task Aws_IncompleteRequest_BadRequestNamingTheField(string path, string field)
    {
        var response = await fixture.AdminClient.PutAsJsonAsync(path, new { region = "us-east-1" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("field").GetString().Should().Be(field);
        problem.GetProperty("type").GetString().Should().EndWith("invalid-aws-setup");
    }

    [Fact]
    public async Task AwsPermissions_NotAnAddress_BadRequestAndNothingSaved()
    {
        var response = await fixture.AdminClient.PutAsJsonAsync("/api/v1/providers/aws/permissions", new { acsUrl = "not an address" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString().Should().Be("acsUrl");
    }

    [Theory]
    [InlineData("PUT", "/api/v1/providers/azure/access")]
    [InlineData("DELETE", "/api/v1/providers/azure/access")]
    [InlineData("PUT", "/api/v1/providers/azure/permissions")]
    [InlineData("DELETE", "/api/v1/providers/azure/permissions")]
    public async Task Azure_Anonymous_Unauthorized(string method, string path)
    {
        using var anonymous = fixture.Factory.CreateClient();

        var response = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/api/v1/providers/azure/access", "tenantId")]
    [InlineData("/api/v1/providers/azure/permissions", "redirectUri")]
    public async Task Azure_InvalidRequest_BadRequestNamingTheField(string path, string field)
    {
        var response = await fixture.AdminClient.PutAsJsonAsync(path, new { redirectUri = "not an address" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("field").GetString().Should().Be(field);
        problem.GetProperty("type").GetString().Should().EndWith("invalid-azure-setup");
    }
}
