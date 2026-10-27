using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
        fixture.Factory.Services.GetService(typeof(Connapse.Storage.Connectors.GitHub.GitHubManifestRequests)).Should().NotBeNull();
    }

    [Fact]
    public async Task Callback_Anonymous_IsRefused()
    {
        using var client = Client(asAdmin: false);

        var response = await client.GetAsync($"{Callback}?code=x&state=y");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Redirect);
        response.Headers.Location?.ToString().Should().NotContain("github_created");
    }

    [Theory]
    [InlineData("PUT", "/api/v1/providers/github/app")]
    [InlineData("DELETE", "/api/v1/providers/github/app")]
    [InlineData("POST", "/api/v1/providers/github/manifest")]
    public async Task AppSetup_Anonymous_IsUnauthorized(string method, string path)
    {
        using var client = Client(asAdmin: false);

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PutApp_NoAppId_IsInvalidOnAppIdBeforeAskingGitHub()
    {
        using var client = Client(asAdmin: true);

        var response = await client.PutAsJsonAsync("/api/v1/providers/github/app", new { privateKeyPem = "key" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("invalid-github-app").And.Contain("appId");
    }

    [Fact]
    public async Task Manifest_OwnAccount_ReturnsGitHubsFormAndTheSetupPage()
    {
        using var client = Client(asAdmin: true);

        var response = await client.PostAsJsonAsync("/api/v1/providers/github/manifest", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var creation = await response.Content.ReadFromJsonAsync<JsonElement>();
        creation.GetProperty("formAction").GetString().Should().StartWith("https://github.com/settings/apps/new?state=");
        creation.GetProperty("manifest").GetString().Should().Contain(Callback);
        creation.GetProperty("setupPageUrl").GetString().Should().EndWith("/admin/providers/github");
    }

    [Fact]
    public async Task Manifest_NotAnOrganisationName_IsInvalidOnOrganisation()
    {
        using var client = Client(asAdmin: true);

        var response = await client.PostAsJsonAsync("/api/v1/providers/github/manifest", new { organisation = "not a name!" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("organisation");
    }
}
