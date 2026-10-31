using System.Net;
using System.Net.Http.Json;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Integration.Tests.CrossSurface;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// MapIdentityApi's register route (CON-169). Connapse has no self sign-up: an account comes from an
/// invite, or from the owner set up on first run, both in the web UI. So a stranger, or a signed-in
/// person who isn't an administrator, must not make an account through the API either.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class IdentityRegisterTests(SharedWebAppFixture fixture)
{
    private const string Password = "StrangerSignUp1!";

    private static string NewEmail() => $"stranger-{Guid.NewGuid():N}@integration-tests.connapse.io";

    [Fact]
    public async Task Register_ByAStranger_Returns401AndMakesNoAccount()
    {
        string email = NewEmail();
        using var anonymous = fixture.Factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/v1/identity/register", new { email, password = Password });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await AccountExistsAsync(email)).Should().BeFalse("a stranger can't make an account");
        var login = await anonymous.PostAsJsonAsync("/api/v1/identity/login", new { email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "no account was made to sign in to");
    }

    [Fact]
    public async Task Register_ByAStrangerWhoseEmailHasAPendingInvite_Returns401AndLeavesTheInvitePending()
    {
        string email = NewEmail();
        using (var admin = await Surfaces.RestAsync(fixture, Principal.Admin))
            (await admin.PostAsJsonAsync("/api/v1/invites", new { email, role = "Viewer" }))
                .StatusCode.Should().Be(HttpStatusCode.Created);
        using var anonymous = fixture.Factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/v1/identity/register", new { email, password = Password });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await AccountExistsAsync(email)).Should().BeFalse("an invite is accepted through its link, not by naming its email");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();
        (await db.UserInvitations.SingleAsync(i => i.Email == email)).AcceptedAt.Should().BeNull();
    }

    [Fact]
    public async Task Register_ByAViewer_Returns403AndMakesNoAccount()
    {
        string email = NewEmail();
        using var viewer = await Surfaces.RestAsync(fixture, Principal.Viewer);

        var response = await viewer.PostAsJsonAsync("/api/v1/identity/register", new { email, password = Password });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AccountExistsAsync(email)).Should().BeFalse();
    }

    private async Task<bool> AccountExistsAsync(string email)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>().FindByEmailAsync(email) is not null;
    }
}
