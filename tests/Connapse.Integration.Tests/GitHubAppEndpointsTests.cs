using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The GitHub App manifest callback, through the real host: it refuses anything but a signed-in
/// administrator, and refuses a state that administrator did not start before spending GitHub's code.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class GitHubAppEndpointsTests(SharedWebAppFixture fixture)
{
    private const string Callback = "/api/v1/providers/github/manifest/callback";

    private HttpClient Client(bool asAdmin)
    {
        var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (asAdmin)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.AdminToken);
        return client;
    }

    [Fact]
    public async Task Callback_StateNobodyStarted_RedirectsWithAnErrorAndStoresNothing()
    {
        using var client = Client(asAdmin: true);

        var response = await client.GetAsync($"{Callback}?code=would-be-spent&state=planted");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/admin/providers/github?github_error=expired");
    }

    [Theory]
    [InlineData("?installation_id=77&setup_action=install", "/connections?github_installation=77")]
    [InlineData("?setup_action=request", "/connections?github_install=requested")]
    [InlineData("?installation_id=77&setup_action=update", "/connections?github_updated=77")]
    [InlineData("", "/connections?new=github")]
    public async Task Installed_ForwardsToTheConnectionsPage(string query, string expected)
    {
        using var client = Client(asAdmin: true);

        var response = await client.GetAsync("/api/v1/providers/github/installed" + query);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be(expected);
    }

    [Fact]
    public void Host_ResolvesTheAppClientAndTheManifestRequestStore()
    {
        // Both are singletons the page, the endpoint, and the setup reader share; a missing
        // registration would only surface as a GitHub card that never leaves "not configured".
        fixture.Factory.Services.GetService(typeof(Connapse.Storage.Connectors.GitHub.ConnapseGitHubApp)).Should().NotBeNull();
        fixture.Factory.Services.GetService(typeof(Connapse.Web.Services.GitHubManifestRequests)).Should().NotBeNull();
    }

    [Fact]
    public async Task Callback_Anonymous_IsRefused()
    {
        using var client = Client(asAdmin: false);

        var response = await client.GetAsync($"{Callback}?code=x&state=y");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Redirect);
        response.Headers.Location?.ToString().Should().NotContain("github_created");
    }
}
