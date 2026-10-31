using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// MapIdentityApi's register route (CON-172, the main-branch port of CON-169). Connapse has no self
/// sign-up: an account comes from an invite, or from the owner set up on first run, both in the web UI.
/// So a stranger, or a signed-in person who isn't an administrator, must not make an account through
/// the API either.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class IdentityRegisterTests(SharedWebAppFixture fixture) : IAsyncLifetime
{
    private const string Password = "StrangerSignUp1!";
    private const string ViewerEmail = "viewer@register-tests.connapse.io";
    private const string ViewerPassword = "ViewerTest1!";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string NewEmail() => $"stranger-{Guid.NewGuid():N}@integration-tests.connapse.io";

    public async Task InitializeAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
        if (await userManager.FindByEmailAsync(ViewerEmail) is not null)
            return;

        var viewer = new ConnapseUser
        {
            UserName = ViewerEmail,
            Email = ViewerEmail,
            EmailConfirmed = true,
            DisplayName = ViewerEmail,
            CreatedAt = DateTime.UtcNow,
        };
        (await userManager.CreateAsync(viewer, ViewerPassword)).Succeeded.Should().BeTrue();
        await userManager.AddToRoleAsync(viewer, "Viewer");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Register_ByAStranger_Returns401AndMakesNoAccount()
    {
        string email = NewEmail();
        using var anonymous = fixture.Factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/v1/identity/register", new { email, password = Password });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await AccountExistsAsync(email)).Should().BeFalse("a stranger can't make an account");
    }

    [Fact]
    public async Task Register_ByAStrangerWhoseEmailHasAPendingInvite_Returns401AndLeavesTheInvitePending()
    {
        string email = NewEmail();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var admin = await scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>()
                .FindByEmailAsync(SharedWebAppFixture.AdminEmail);
            await scope.ServiceProvider.GetRequiredService<InviteService>().CreateInviteAsync(email, "Viewer", admin!.Id);
        }
        using var anonymous = fixture.Factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/v1/identity/register", new { email, password = Password });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await AccountExistsAsync(email)).Should().BeFalse("an invite is accepted through its link, not by naming its email");
        await using var check = fixture.Factory.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();
        (await db.UserInvitations.SingleAsync(i => i.Email == email)).AcceptedAt.Should().BeNull();
    }

    [Fact]
    public async Task Register_ByAViewer_Returns403AndMakesNoAccount()
    {
        string email = NewEmail();
        using var viewer = await ClientForAsync(ViewerEmail, ViewerPassword);

        var response = await viewer.PostAsJsonAsync("/api/v1/identity/register", new { email, password = Password });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AccountExistsAsync(email)).Should().BeFalse();
    }

    [Fact]
    public async Task Register_ByAnAdmin_Returns200AndMakesTheAccount()
    {
        string email = NewEmail();
        using var admin = await ClientForAsync(SharedWebAppFixture.AdminEmail, SharedWebAppFixture.AdminPassword);

        var response = await admin.PostAsJsonAsync("/api/v1/identity/register", new { email, password = Password });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AccountExistsAsync(email)).Should().BeTrue("an administrator can still make an account");
    }

    private async Task<HttpClient> ClientForAsync(string email, string password)
    {
        using var anonymous = fixture.Factory.CreateClient();
        var tokenResponse = await anonymous.PostAsJsonAsync("/api/v1/auth/token", new { email, password });
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);

        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private async Task<bool> AccountExistsAsync(string email)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>().FindByEmailAsync(email) is not null;
    }
}
