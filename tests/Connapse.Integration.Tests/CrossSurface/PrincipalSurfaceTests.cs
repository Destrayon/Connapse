using System.Net.Http.Json;
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

    internal static Principal ByName(string name) =>
        new[] { Principal.Admin, Principal.Viewer, Principal.NoRole }.Single(p => p.Name == name);

    private sealed record MeBody(string Email, List<string> Roles);
}
