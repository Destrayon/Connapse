using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Identity.Data.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Linking a Connapse account to a custom provider: the signed link token, confirmation for the signed-in
/// user only, and unlinking. The fixture's fake middle man keeps the links.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class CustomLinkEndpointsTests(SharedWebAppFixture fixture) : IAsyncLifetime
{
    private const string OtherEmail = "custom-link-other@integration-tests.connapse.io";
    private const string OtherPassword = "CustomLinkOther1!";

    private FakeMiddleMan Remote => fixture.CustomSource;

    public Task InitializeAsync()
    {
        Remote.SupportsLinking = true;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Remote.SupportsLinking = false;
        Remote.Links.Clear();
        Remote.PendingLinks.Clear();
        return Task.CompletedTask;
    }

    private async Task<(CustomProvider Provider, string? Secret)> ProviderAsync(bool withSecret = true)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ICustomProviderStore store = scope.ServiceProvider.GetRequiredService<ICustomProviderStore>();
        CustomProvider provider = await store.CreateAsync(new SaveCustomProviderRequest(
            $"link-{Guid.NewGuid():N}"[..20], FakeMiddleMan.BaseUrl, FakeMiddleMan.Token, 100, true, FakeMiddleMan.LinkUrl));
        string? secret = withSecret ? await store.GenerateLinkSecretAsync(provider.Id) : null;
        return (provider, secret);
    }

    private async Task<string> UserIdAsync(string email)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        UserManager<ConnapseUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
        return (await users.FindByEmailAsync(email))!.Id.ToString();
    }

    private HttpClient NoRedirects(string accessToken)
    {
        HttpClient client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private async Task<HttpClient> OtherUserClientAsync()
    {
        using (IServiceScope scope = fixture.Factory.Services.CreateScope())
        {
            UserManager<ConnapseUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
            if (await users.FindByEmailAsync(OtherEmail) is null)
            {
                ConnapseUser user = new()
                {
                    UserName = OtherEmail,
                    Email = OtherEmail,
                    EmailConfirmed = true,
                    DisplayName = OtherEmail,
                    CreatedAt = DateTime.UtcNow,
                };
                (await users.CreateAsync(user, OtherPassword)).Succeeded.Should().BeTrue();
                await users.AddToRoleAsync(user, "Viewer");
            }
        }

        using HttpClient anon = fixture.Factory.CreateClient();
        HttpResponseMessage response = await anon.PostAsJsonAsync("/api/v1/auth/token", new { email = OtherEmail, password = OtherPassword });
        response.EnsureSuccessStatusCode();
        JsonElement token = await response.Content.ReadFromJsonAsync<JsonElement>();
        return NoRedirects(token.GetProperty("accessToken").GetString()!);
    }

    [Fact]
    public async Task Connect_PostsATokenSignedForThisUserToTheLinkingPage()
    {
        (CustomProvider provider, string? secret) = await ProviderAsync();
        using HttpClient admin = NoRedirects(fixture.AdminToken);

        HttpResponseMessage response = await admin.GetAsync($"/api/v1/auth/custom/{provider.Id}/connect");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        string html = await response.Content.ReadAsStringAsync();
        Regex.Match(html, "action=\"([^\"]+)\"").Groups[1].Value.Should().Be(FakeMiddleMan.LinkUrl);
        string token = Regex.Match(html, "name=\"token\" value=\"([^\"]+)\"").Groups[1].Value;

        TokenValidationResult result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "http://localhost",
            ValidAudience = provider.Id.ToString(),
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret!)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        });
        result.IsValid.Should().BeTrue(result.Exception?.Message);
        result.Claims["sub"].Should().Be(await UserIdAsync(SharedWebAppFixture.AdminEmail));
        result.Claims["return_url"].Should().Be($"http://localhost/api/v1/auth/custom/{provider.Id}/return");
        result.Claims.Should().ContainKey("jti");
        result.SecurityToken.ValidTo.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Connect_ProviderWithoutALinkSecret_SendsTheUserBackWithAnError()
    {
        (CustomProvider provider, _) = await ProviderAsync(withSecret: false);
        using HttpClient admin = NoRedirects(fixture.AdminToken);

        HttpResponseMessage response = await admin.GetAsync($"/api/v1/auth/custom/{provider.Id}/connect");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/profile/integrations?error=custom_link_unavailable");
    }

    [Fact]
    public async Task Return_CodeIssuedForTheSignedInUser_Links()
    {
        (CustomProvider provider, _) = await ProviderAsync();
        string adminId = await UserIdAsync(SharedWebAppFixture.AdminEmail);
        Remote.PendingLinks["code-ok"] = (adminId, "alice@acme");
        using HttpClient admin = NoRedirects(fixture.AdminToken);

        HttpResponseMessage response = await admin.GetAsync($"/api/v1/auth/custom/{provider.Id}/return?code=code-ok");

        response.Headers.Location!.ToString().Should().Be("/profile/integrations?linked=custom");
        Remote.Links.Should().Contain(adminId, "alice@acme");
    }

    [Fact]
    public async Task Return_CodePresentedByADifferentSignedInUser_IsRefused()
    {
        (CustomProvider provider, _) = await ProviderAsync();
        string adminId = await UserIdAsync(SharedWebAppFixture.AdminEmail);
        // The admin started the link; a colleague's browser finished the sign-in and lands on the return page.
        Remote.PendingLinks["code-forwarded"] = (adminId, "colleague@acme");
        using HttpClient other = await OtherUserClientAsync();

        HttpResponseMessage response = await other.GetAsync($"/api/v1/auth/custom/{provider.Id}/return?code=code-forwarded");

        response.Headers.Location!.ToString().Should().Be("/profile/integrations?error=custom_link_rejected");
        Remote.Links.Should().BeEmpty();
        Remote.PendingLinks.Should().NotContainKey("code-forwarded", "a refused code is spent");
    }

    [Fact]
    public async Task Return_WithoutASession_IsRefusedBeforeTheMiddleManIsAsked()
    {
        (CustomProvider provider, _) = await ProviderAsync();
        Remote.PendingLinks["code-anon"] = (await UserIdAsync(SharedWebAppFixture.AdminEmail), "alice@acme");
        using HttpClient anon = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await anon.GetAsync($"/api/v1/auth/custom/{provider.Id}/return?code=code-anon");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Redirect);
        Remote.PendingLinks.Should().ContainKey("code-anon");
    }

    [Fact]
    public async Task Unlink_RemovesTheSignedInUsersLinkFromTheMiddleMan()
    {
        (CustomProvider provider, _) = await ProviderAsync();
        string adminId = await UserIdAsync(SharedWebAppFixture.AdminEmail);
        Remote.Links[adminId] = "alice@acme";
        using HttpClient admin = NoRedirects(fixture.AdminToken);

        HttpResponseMessage response = await admin.DeleteAsync($"/api/v1/auth/custom/{provider.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Remote.Links.Should().NotContainKey(adminId);
    }

    [Fact]
    public async Task Unlink_ProviderThatDoesNotExist_Returns404()
    {
        using HttpClient admin = NoRedirects(fixture.AdminToken);

        HttpResponseMessage response = await admin.DeleteAsync($"/api/v1/auth/custom/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
