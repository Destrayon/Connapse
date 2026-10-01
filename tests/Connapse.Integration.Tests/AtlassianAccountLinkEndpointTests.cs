using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Linking an Atlassian account through the real host, against the in-process Atlassian fake: the
/// confirm step saves only for the user who started the sign-in, an unlink wins over a sign-in that
/// was in flight, and a sign-in Atlassian cannot name stores nothing.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class AtlassianAccountLinkEndpointTests(SharedWebAppFixture fixture)
{
    // Mirrors the production cookie name in CloudIdentityEndpoints; kept in sync by hand.
    private const string ConfirmCookie = "__connapse_atlassian_link";
    private const string VictimEmail = "atlassian-victim@integration-tests.connapse.io";
    private const string VictimPassword = "AtlassianVictimTest1!";

    private FakeAtlassianApi Api => fixture.Atlassian;

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
                    DisplayName = "Atlassian CSRF Victim", CreatedAt = DateTime.UtcNow,
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

    private async Task<AtlassianIdentityRef?> LinkOf(Guid user)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AtlassianIdentityLinkStore>().GetLinkAsync(user);
    }

    private async Task WithLinkAppAsync(Func<IProviderCredentialStore, Task> body)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var credentials = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        Guid admin = AdminUserId();
        try
        {
            await credentials.SaveAtlassianLinkAppAsync("link-client", "link-secret", createdByUserId: null);
            await body(credentials);
        }
        finally
        {
            await credentials.DeleteAsync("atlassian");
            await scope.ServiceProvider.GetRequiredService<AtlassianIdentityLinkStore>().DeleteAsync(admin);
            Api.Map("/me", _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpRequestMessage Confirm(string code)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/cloud/atlassian/confirm");
        request.Headers.Add("Cookie", $"{ConfirmCookie}={code}");
        return request;
    }

    /// <summary>Records a sign-in the admin started, as /atlassian/connect would, and returns its state.</summary>
    private string StartSignIn(Guid user)
    {
        string state = Guid.NewGuid().ToString("N");
        fixture.Factory.Services.GetRequiredService<AtlassianLinkFlow>().AddSignIn(new AtlassianPendingSignIn(
            state, "verifier-" + state, user, DateTime.UtcNow.Add(AtlassianLinkFlow.SignInLifetime), DateTime.UtcNow));
        return state;
    }

    [Fact]
    public async Task Callback_UnknownState_RedirectsExpired()
    {
        using var anon = Client(null);

        var response = await anon.GetAsync("/api/v1/auth/cloud/atlassian/callback?code=x&state=never-issued");

        response.Headers.Location!.OriginalString.Should().Be("/profile/integrations?error=atlassian_link_expired");
        response.Headers.TryGetValues("Set-Cookie", out _).Should().BeFalse("nothing is parked for an unknown state");
    }

    [Fact]
    public async Task Confirm_DifferentUser_Refuses()
    {
        Guid admin = AdminUserId();
        var flow = fixture.Factory.Services.GetRequiredService<AtlassianLinkFlow>();
        // The admin started the sign-in; the colleague's browser completed it and holds the cookie.
        string code = flow.Park(new PendingAtlassianLink(admin, "colleague-account", "Colleague", null, DateTime.UtcNow));

        try
        {
            using var victim = Client(await VictimTokenAsync());
            var refused = await victim.SendAsync(Confirm(code));

            refused.StatusCode.Should().Be(HttpStatusCode.Redirect);
            refused.Headers.Location!.OriginalString.Should().Contain("error=atlassian_link_wrong_user");
            (await LinkOf(admin)).Should().BeNull("the starter's account must not be linked to the colleague's Atlassian account");

            using var adminClient = Client(fixture.AdminToken);
            (await adminClient.SendAsync(Confirm(code))).Headers.Location!.OriginalString
                .Should().Contain("error=atlassian_link_expired", "a claim is spent even when refused");
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AtlassianIdentityLinkStore>().DeleteAsync(admin);
        }
    }

    [Fact]
    public async Task Confirm_AfterUnlinkRace_DiscardsLink()
    {
        Guid admin = AdminUserId();
        var flow = fixture.Factory.Services.GetRequiredService<AtlassianLinkFlow>();
        // Parked a moment before the unlink, as when the user disconnects while Atlassian is redirecting back.
        string code = flow.Park(new PendingAtlassianLink(admin, "acc-race", "Racer", null, DateTime.UtcNow.AddSeconds(-1)));
        using var adminClient = Client(fixture.AdminToken);

        try
        {
            (await adminClient.DeleteAsync("/api/v1/auth/cloud/atlassian")).StatusCode.Should().Be(HttpStatusCode.NotFound);

            var confirmed = await adminClient.SendAsync(Confirm(code));

            confirmed.Headers.Location!.OriginalString.Should().StartWith("/profile/integrations?error=atlassian_link_");
            (await LinkOf(admin)).Should().BeNull("the unlink was the later decision");
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AtlassianIdentityLinkStore>().DeleteAsync(admin);
        }
    }

    [Fact]
    public async Task Callback_MeFails_StoresNothing()
    {
        Guid admin = AdminUserId();
        await WithLinkAppAsync(async credentials =>
        {
            string state = StartSignIn(admin);
            Api.AcceptAuthorizationCode("code-me-fails");
            Api.Map("/me", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
            using var anon = Client(null);

            var response = await anon.GetAsync($"/api/v1/auth/cloud/atlassian/callback?code=code-me-fails&state={state}");

            response.Headers.Location!.OriginalString.Should().Be("/profile/integrations?error=atlassian_link_failed");
            response.Headers.TryGetValues("Set-Cookie", out _).Should().BeFalse("nothing is parked when Atlassian cannot say who signed in");
            (await LinkOf(admin)).Should().BeNull();
        });
    }

    [Fact]
    public async Task Callback_ThenConfirm_LinksMarksTheAppVerifiedAndUnlinks()
    {
        Guid admin = AdminUserId();
        await WithLinkAppAsync(async credentials =>
        {
            string state = StartSignIn(admin);
            Api.AcceptAuthorizationCode("code-good");
            Api.MapJson("/me", new { account_id = "acc-ada", name = "Ada Lovelace", email = "ada@example.com" });
            using var anon = Client(null);

            var callback = await anon.GetAsync($"/api/v1/auth/cloud/atlassian/callback?code=code-good&state={state}");

            callback.Headers.Location!.OriginalString.Should().Be("/api/v1/auth/cloud/atlassian/confirm");
            string cookie = callback.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(ConfirmCookie + "="));
            cookie.Should().Contain("httponly", Exactly.Once());
            string code = cookie.Split(';')[0][(ConfirmCookie.Length + 1)..];
            Api.AuthorizationCodeBodies.Last().Should().Contain($"\"code_verifier\":\"verifier-{state}\"");

            using var adminClient = Client(fixture.AdminToken);
            var confirmed = await adminClient.SendAsync(Confirm(code));

            confirmed.Headers.Location!.OriginalString.Should().Be("/profile/integrations?linked=atlassian");
            (await LinkOf(admin)).Should().Be(new AtlassianIdentityRef("acc-ada", "Ada Lovelace"));
            (await credentials.GetAtlassianLinkAppAsync())!.VerifiedAt.Should().NotBeNull();

            (await adminClient.DeleteAsync("/api/v1/auth/cloud/atlassian")).StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await LinkOf(admin)).Should().BeNull();
        });
    }

    [Fact]
    public async Task Connect_WithoutALinkApp_SaysAtlassianIsNotSetUp()
    {
        using var adminClient = Client(fixture.AdminToken);

        var response = await adminClient.GetAsync("/api/v1/auth/cloud/atlassian/connect");

        response.Headers.Location!.OriginalString.Should().Be("/profile/integrations?error=atlassian_not_configured");
    }
}
