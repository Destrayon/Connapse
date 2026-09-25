using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Linking a GitHub account through the real host: the confirm step saves only for the user who
/// started the sign-in, an unknown callback saves nothing, and unlinking removes the link.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class GitHubAccountLinkEndpointTests(SharedWebAppFixture fixture)
{
    // Mirrors the production cookie name in CloudIdentityEndpoints; kept in sync by hand.
    private const string ConfirmCookie = "__connapse_github_link";
    private const string VictimEmail = "github-victim@integration-tests.connapse.io";
    private const string VictimPassword = "GitHubVictimTest1!";

    private HttpClient Client(string? bearer)
    {
        var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (bearer is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    private Guid AdminUserId()
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(fixture.AdminToken);
        string? sub = token.Claims.FirstOrDefault(c => c.Type is "sub" or "nameid"
            or "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
        return Guid.Parse(sub!);
    }

    private async Task<string> VictimTokenAsync()
    {
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
            if (await users.FindByEmailAsync(VictimEmail) is null)
            {
                var created = await users.CreateAsync(new ConnapseUser
                {
                    UserName = VictimEmail, Email = VictimEmail, EmailConfirmed = true,
                    DisplayName = "GitHub CSRF Victim", CreatedAt = DateTime.UtcNow,
                }, VictimPassword);
                created.Succeeded.Should().BeTrue(string.Join(", ", created.Errors.Select(e => e.Description)));
            }
        }

        using var anon = fixture.Factory.CreateClient();
        var response = await anon.PostAsJsonAsync("/api/v1/auth/token", new { email = VictimEmail, password = VictimPassword });
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    private async Task<Core.Interfaces.GitHubIdentityRef?> LinkOf(Guid user)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GitHubIdentityLinkStore>().GetLinkAsync(user);
    }

    private static HttpRequestMessage Confirm(string code)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/cloud/github/confirm");
        request.Headers.Add("Cookie", $"{ConfirmCookie}={code}");
        return request;
    }

    [Fact]
    public async Task Confirm_CompletedByADifferentUserThanStartedIt_RefusesAndStoresNothing()
    {
        Guid admin = AdminUserId();
        var flow = fixture.Factory.Services.GetRequiredService<GitHubLinkFlow>();
        // The admin started the sign-in; the colleague's browser completed it and holds the cookie.
        string code = flow.Park(new PendingGitHubLink(admin, 424242, "colleague", DateTime.UtcNow));

        try
        {
            using var victim = Client(await VictimTokenAsync());
            var refused = await victim.SendAsync(Confirm(code));

            refused.StatusCode.Should().Be(HttpStatusCode.Redirect);
            refused.Headers.Location!.OriginalString.Should().Contain("error=github_link_wrong_user");
            (await LinkOf(admin)).Should().BeNull("the starter's account must not be linked to the colleague's GitHub");

            using var adminClient = Client(fixture.AdminToken);
            (await adminClient.SendAsync(Confirm(code))).Headers.Location!.OriginalString
                .Should().Contain("error=github_link_expired", "a claim is spent even when refused");
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<GitHubIdentityLinkStore>().DeleteAsync(admin);
        }
    }

    [Fact]
    public async Task Confirm_ByTheUserWhoStartedIt_LinksAndDeleteUnlinks()
    {
        Guid admin = AdminUserId();
        var flow = fixture.Factory.Services.GetRequiredService<GitHubLinkFlow>();
        string code = flow.Park(new PendingGitHubLink(admin, 583231, "octocat", DateTime.UtcNow));
        using var adminClient = Client(fixture.AdminToken);

        try
        {
            var saved = await adminClient.SendAsync(Confirm(code));
            saved.Headers.Location!.OriginalString.Should().Be("/profile/integrations?linked=github");
            (await LinkOf(admin)).Should().Be(new Core.Interfaces.GitHubIdentityRef(583231, "octocat"));

            (await adminClient.DeleteAsync("/api/v1/auth/cloud/github")).StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await LinkOf(admin)).Should().BeNull();
            (await adminClient.DeleteAsync("/api/v1/auth/cloud/github")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<GitHubIdentityLinkStore>().DeleteAsync(admin);
        }
    }

    [Fact]
    public async Task Callback_StateNobodyStarted_SavesNothing()
    {
        using var anon = Client(null);

        var response = await anon.GetAsync("/api/v1/auth/cloud/github/callback?code=x&state=never-issued");

        response.Headers.Location!.OriginalString.Should().Be("/profile/integrations?error=github_link_expired");
        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeFalse("nothing is parked for an unknown state");
    }

    [Fact]
    public async Task Connect_WithoutAProviderApp_SaysGitHubIsNotSetUp()
    {
        using var adminClient = Client(fixture.AdminToken);

        var response = await adminClient.GetAsync("/api/v1/auth/cloud/github/connect");

        response.Headers.Location!.OriginalString.Should().Be("/profile/integrations?error=github_not_configured");
    }
}
