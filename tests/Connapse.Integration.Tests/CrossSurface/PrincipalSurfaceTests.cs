using System.Net;
using System.Net.Http.Json;
using Bunit;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Connapse.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>
/// Self-tests for #792's principals: each one reaches each surface as itself, so an
/// under-scoped case really asks the surface about that user and not the admin.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class PrincipalSurfaceTests(SharedWebAppFixture fixture)
{
    public static TheoryData<string> AllPrincipals => [Principal.Admin.Name, Principal.Viewer.Name, Principal.NoRole.Name];

    [Theory]
    [MemberData(nameof(AllPrincipals))]
    public async Task Rest_SignsInAsThePrincipal(string name)
    {
        var principal = ByName(name);
        using var rest = await Surfaces.RestAsync(fixture, principal);

        var me = await rest.GetFromJsonAsync<MeBody>("/api/v1/auth/me");

        me!.Email.Should().Be(principal.Email);
        if (principal.Role is null)
            me.Roles.Should().BeEmpty();
        else
            me.Roles.Should().Contain(principal.Role);
    }

    [Theory]
    [InlineData("admin", null)]
    [InlineData("viewer", "error:forbidden")]
    [InlineData("no-role", "error:forbidden")]
    public async Task Mcp_AdmitsOnlyItsRoles(string name, string? refusal)
    {
        // The refusal must come from connecting: a principal admitted to /mcp but refused a later
        // call (listing tools) is a different behaviour and fails here.
        McpClient? mcp = null;
        string? connect = null;
        try
        {
            mcp = await Surfaces.McpAsync(fixture, ByName(name));
        }
        catch (Exception ex) when (Surfaces.McpRefusal(ex) is { } refused)
        {
            connect = refused;
        }

        connect.Should().Be(refusal);
        if (mcp is not null)
            await using (mcp)
                (await mcp.ListToolsAsync()).Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllPrincipals))]
    public async Task Ui_AuditsAsThePrincipal(string name)
    {
        var principal = ByName(name);
        string resourceId = Guid.NewGuid().ToString();
        using var ui = await Surfaces.UiAsync(fixture, principal);
        var audit = ui.Context.Services.GetRequiredService<IAuditLogger>();

        var outcome = await SideEffects.CaptureAsync(fixture,
            async () =>
            {
                await audit.LogAsync("harness.ui_principal", "harness", resourceId);
                return "ok";
            },
            _ => Task.FromResult(new StateProjection([], [resourceId])));

        // The row must name this principal: {userN} alone would also accept another non-admin.
        outcome.Audit.Should().ContainSingle();
        Guid expected = await Principals.UserIdAsync(fixture, principal);
        using var scope = fixture.Factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>().AuditLogs.AsNoTracking()
            .SingleAsync(a => a.ResourceId == resourceId);
        row.UserId.Should().Be(expected, $"the row names {principal}, as a circuit signed in as them would");
    }

    /// <summary>
    /// The token carries the role, not just the database: a Viewer-guarded read succeeds for the
    /// admin and the Viewer and is refused to a user with no role.
    /// </summary>
    [Theory]
    [InlineData("admin", HttpStatusCode.OK)]
    [InlineData("viewer", HttpStatusCode.OK)]
    [InlineData("no-role", HttpStatusCode.Forbidden)]
    public async Task Rest_AppliesThePrincipalsRole(string name, HttpStatusCode expected)
    {
        using var rest = await Surfaces.RestAsync(fixture, ByName(name));

        var response = await rest.GetAsync("/api/containers?skip=0&take=1");

        // Exactly the refusal: a 500 from a broken list must not pass as "not allowed".
        response.StatusCode.Should().Be(expected);
    }

    /// <summary>A write the role doesn't allow is refused with 403 and leaves nothing behind.</summary>
    [Theory]
    [InlineData("admin", HttpStatusCode.Created)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData("no-role", HttpStatusCode.Forbidden)]
    public async Task Rest_GuardsAWriteByThePrincipalsRole(string name, HttpStatusCode expected)
    {
        string container = $"principal-{Guid.NewGuid():N}"[..24];
        using var rest = await Surfaces.RestAsync(fixture, ByName(name));

        var response = await rest.PostAsJsonAsync("/api/containers", new { name = container });

        response.StatusCode.Should().Be(expected);
        using var scope = fixture.Factory.Services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<IContainerStore>().GetByNameAsync(container);
        (created is not null).Should().Be(expected == HttpStatusCode.Created);
        if (created is not null)
            await fixture.AdminClient.DeleteAsync($"/api/containers/{created.Id}");
    }

    /// <summary>
    /// The page renders with the principal's roles: Home offers New Container to anyone who isn't a
    /// Viewer (canEdit = !IsInRole("Viewer")), so the admin and the no-role user see it and the
    /// Viewer doesn't. The no-role case is a known gap #792's comparison lists.
    /// </summary>
    [Theory]
    [InlineData("admin", true)]
    [InlineData("viewer", false)]
    [InlineData("no-role", true)]
    public async Task Ui_RendersWithThePrincipalsRoles(string name, bool offersCreate)
    {
        using var ui = await Surfaces.UiAsync(fixture, ByName(name));
        var page = ui.Context.Render<Connapse.Web.Components.Pages.Home>();
        page.WaitForAssertion(() => page.FindAll(".container-grid, .empty-state, .alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));

        page.FindAll("button.btn-primary").Any(b => b.TextContent.Contains("Container", StringComparison.Ordinal)).Should().Be(offersCreate);
    }

    internal static Principal ByName(string name) =>
        new[] { Principal.Admin, Principal.Viewer, Principal.NoRole }.Single(p => p.Name == name);

    private sealed record MeBody(string Email, List<string> Roles);
}
