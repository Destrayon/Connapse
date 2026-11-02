using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Connapse.Identity.Data.Entities;
using Connapse.Web.Components.Pages.Admin;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>A case's own user: a Viewer made for it alone, named after the case's container.</summary>
public sealed record UserCase(Guid Id, string Email);

public static partial class Operations
{
    /// <summary>MCP reaches users only through the generic tools Phase 5 adds.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoUserTools = new Dictionary<string, string>
    {
        ["mcp"] = "MCP has no user tools; the generic connapse_api_* tools arrive in Phase 5 of #783",
    };

    /// <summary>
    /// The user operations (CON-14), on REST and the User management page. The page acts only on the
    /// users it lists, so it has no way to name a user that doesn't exist, and it offers only the
    /// roles a user can be given: the invalid and preflight cases are REST's alone.
    /// </summary>
    private static readonly Operation[] UserOperations =
    [
        new("users_list", [Case.Valid, Case.UnderScoped], Writes: false, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                // Oldest first, so the case's user is on the last page: read until it or the end.
                for (int skip = 0; ; skip += 200)
                {
                    var response = await rest.GetAsync($"/api/v1/auth/users?skip={skip}&take=200");
                    if (!response.IsSuccessStatusCode)
                        return await Surfaces.ResultAsync(response);
                    using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (page.RootElement.GetProperty("items").EnumerateArray().Any(u => u.GetProperty("email").GetString() == c.User!.Email))
                        return "ok " + Listed(true);
                    if (!page.RootElement.GetProperty("hasMore").GetBoolean())
                        return "ok " + Listed(false);
                }
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<UserManagement>();
                if (UsersPageError(page) is { } error)
                    return error;
                return "ok " + Listed(UserRow(page, c) is not null);
            },
            Project: (sp, c) => UserStateAsync(sp, c))
        {
            AbsentReasons = NoUserTools,
            ArrangesUser = true,
        },

        // Gives the case's Viewer the Editor role. The invalid case names a user that doesn't exist;
        // preflight asks for the Owner role, which nobody is given.
        new("users_setRoles", [Case.Valid, Case.Invalid, Case.UnderScoped, Case.Preflight], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                string role = k == Case.Preflight ? "Owner" : "Editor";
                return await Surfaces.ResultAsync(await rest.PutAsJsonAsync($"/api/v1/auth/users/{UserTarget(c, k)}/roles", new { roles = new[] { role } }));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                if (k is Case.Invalid or Case.Preflight)
                    return null;
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<UserManagement>();
                if (UsersPageError(page) is { } error)
                    return error;
                await page.InvokeAsync(() => UserRow(page, c)!.QuerySelector("select")!.Change("Editor"));
                await ClickAsync(page, () => UserRow(page, c)!.QuerySelectorAll("button").First(b => b.TextContent.Trim() == "Save"));
                return UsersPageOutcome(page);
            },
            Project: (sp, c) => UserStateAsync(sp, c))
        {
            AbsentReasons = NoUserTools,
            ArrangesUser = true,
        },

        // Deletes the case's Viewer. The invalid case names a user that doesn't exist; preflight asks the
        // admin to delete their own account, which is also the Owner's, and both rules refuse it.
        new("users_delete", [Case.Valid, Case.Invalid, Case.UnderScoped, Case.Preflight], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                string target = k == Case.Preflight ? (await Principals.UserIdAsync(f, Principal.Admin)).ToString() : UserTarget(c, k);
                return await Surfaces.ResultAsync(await rest.DeleteAsync($"/api/v1/auth/users/{target}"));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                if (k is Case.Invalid or Case.Preflight)
                    return null;
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<UserManagement>();
                if (UsersPageError(page) is { } error)
                    return error;
                await ClickAsync(page, () => UserRow(page, c)!.QuerySelectorAll("button").First(b => b.TextContent.Trim() == "Delete"));
                await ClickAsync(page, () => UserRow(page, c)!.QuerySelectorAll("button").First(b => b.TextContent.Trim() == "Confirm"));
                return UsersPageOutcome(page);
            },
            Project: (sp, c) => UserStateAsync(sp, c))
        {
            AbsentReasons = NoUserTools,
            ArrangesUser = true,
        },
    ];

    /// <summary>The user a case acts on: one that doesn't exist for the invalid case, the case's own otherwise.</summary>
    private static string UserTarget(CaseContext c, string @case) => @case == Case.Invalid ? c.Unknown : c.User!.Id.ToString();

    private static IElement? UserRow(IRenderedComponent<UserManagement> page, CaseContext c) =>
        page.FindAll("tbody tr").FirstOrDefault(r => r.QuerySelector("td")?.TextContent.Trim() == c.User!.Email);

    /// <summary>Waits for the page to load its users; the error it showed instead, or null.</summary>
    private static string? UsersPageError(IRenderedComponent<UserManagement> page)
    {
        page.WaitForAssertion(() => page.FindAll("tbody tr, div.alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
        return page.FindAll("div.alert-danger").FirstOrDefault() is { } alert ? Surfaces.ErrorKind(alert.TextContent.Trim()) : null;
    }

    /// <summary>What the page said after an action: its success alert, or its error in the shared words.</summary>
    private static string UsersPageOutcome(IRenderedComponent<UserManagement> page)
    {
        page.WaitForAssertion(() => page.FindAll("div.alert-success, div.alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
        return page.FindAll("div.alert-danger").FirstOrDefault() is { } alert ? Surfaces.ErrorKind(alert.TextContent.Trim()) : "ok";
    }

    /// <summary>Makes the case's own Viewer, named after its container.</summary>
    private static async Task<UserCase> ArrangeUserAsync(SharedWebAppFixture fixture, string name)
    {
        string email = $"{name}@cross-surface.connapse.io";
        using var scope = fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
        var user = new ConnapseUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = name, CreatedAt = DateTime.UtcNow };
        (await users.CreateAsync(user, "CrossSurfaceUser1!")).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, "Viewer")).Succeeded.Should().BeTrue();
        return new UserCase(user.Id, email);
    }

    private static async Task CleanupUserAsync(SharedWebAppFixture fixture, UserCase user)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
        if (await users.FindByIdAsync(user.Id.ToString()) is { } left)
            await users.DeleteAsync(left);
    }

    private static async Task<StateProjection> UserStateAsync(IServiceProvider services, CaseContext c)
    {
        var users = services.GetRequiredService<UserManager<ConnapseUser>>();
        if (c.User is not { } mine || await users.FindByIdAsync(mine.Id.ToString()) is not { } user)
            return new StateProjection(["no user"], c.User is { } gone ? [gone.Id.ToString()] : []);
        var roles = (await users.GetRolesAsync(user)).Order(StringComparer.Ordinal);
        return new StateProjection([$"user roles={string.Join(",", roles)}"], [user.Id.ToString()]);
    }
}
