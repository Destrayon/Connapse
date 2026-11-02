using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using Connapse.Integration.Tests.CrossSurface;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// The invites API (CON-70): an invite's token is its link's secret, so it is answered once, from the
/// create, to an administrator allowed to make that invite, and never again.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class InviteEndpointTests(SharedWebAppFixture fixture)
{
    /// <summary>An administrator who isn't the Owner, so may invite everyone but Admins.</summary>
    private static readonly Principal OtherAdmin = new("invite-admin", "invite-admin@integration-tests.connapse.io", "InviteAdminTest1!", "Admin");

    private static string NewEmail() => $"invitee-{Guid.NewGuid():N}@integration-tests.connapse.io";

    [Fact]
    public async Task Create_AnswersTheTokenOnce_AndTheListNeverShowsIt()
    {
        string email = NewEmail();
        using var rest = await Surfaces.RestAsync(fixture, Principal.Admin);

        var response = await rest.PostAsJsonAsync("/api/v1/invites", new { email, role = "Viewer" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string token = created.RootElement.GetProperty("token").GetString()!;
        token.Should().NotBeNullOrWhiteSpace();
        Guid id = created.RootElement.GetProperty("id").GetGuid();
        using (var scope = fixture.Factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<InviteService>().ValidateInviteAsync(token))!.Id.Should().Be(id, "the token answered is the invite's own");

        var listed = await rest.GetAsync("/api/v1/invites?take=200");
        listed.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await listed.Content.ReadAsStringAsync();
        body.Should().NotContain(token, "the token is answered only by the create");
        using var page = JsonDocument.Parse(body);
        var item = page.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);
        item.GetProperty("email").GetString().Should().Be(email);
        item.EnumerateObject().Select(p => p.Name).Should().NotContain(n => n.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Create_ByAViewer_Returns403AndMakesNoInvite()
    {
        string email = NewEmail();
        using var rest = await Surfaces.RestAsync(fixture, Principal.Viewer);

        var response = await rest.PostAsJsonAsync("/api/v1/invites", new { email, role = "Viewer" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("\"token\"");
        (await InvitesForAsync(email)).Should().Be(0);
    }

    [Fact]
    public async Task Create_AnAdminInviteByAnAdminWhoIsntTheOwner_Returns403WithoutAToken()
    {
        string email = NewEmail();
        using var rest = await Surfaces.RestAsync(fixture, OtherAdmin);

        var response = await rest.PostAsJsonAsync("/api/v1/invites", new { email, role = "Admin" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("\"token\"");
        (await InvitesForAsync(email)).Should().Be(0);
    }

    [Fact]
    public async Task Revoke_ThePendingInvite_RemovesIt()
    {
        string email = NewEmail();
        using var rest = await Surfaces.RestAsync(fixture, Principal.Admin);
        var created = await rest.PostAsJsonAsync("/api/v1/invites", new { email, role = "Editor" });
        using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Guid id = json.RootElement.GetProperty("id").GetGuid();

        var revoked = await rest.DeleteAsync($"/api/v1/invites/{id}");

        revoked.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await InvitesForAsync(email)).Should().Be(0);
        (await rest.DeleteAsync($"/api/v1/invites/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<int> InvitesForAsync(string email)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>().UserInvitations.CountAsync(i => i.Email == email);
    }
}
