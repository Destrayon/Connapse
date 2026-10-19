using System.Net.Http.Json;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

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
        string? result = null;
        try
        {
            await using var mcp = await Surfaces.McpAsync(fixture, ByName(name));
            (await mcp.ListToolsAsync()).Should().NotBeEmpty();
        }
        catch (Exception ex) when (Surfaces.McpRefusal(ex) is { } refused)
        {
            result = refused;
        }

        result.Should().Be(refusal);
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

        string actor = principal == Principal.Admin ? "{admin}" : (await Principals.UserIdAsync(fixture, principal)).ToString();
        outcome.Audit.Should().ContainSingle().Which.Should().MatchRegex(principal == Principal.Admin
            ? @"actor=\{admin\}"
            : @"actor=\{user\d+\}", $"the row names {actor}, not the admin");
    }

    internal static Principal ByName(string name) =>
        new[] { Principal.Admin, Principal.Viewer, Principal.NoRole }.Single(p => p.Name == name);

    private sealed record MeBody(string Email, List<string> Roles);
}
