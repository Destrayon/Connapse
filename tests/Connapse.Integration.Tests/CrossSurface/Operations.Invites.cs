using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using Connapse.Web.Components.Pages.Admin;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests.CrossSurface;

public static partial class Operations
{
    /// <summary>MCP reaches invites only through the generic tools Phase 5 adds.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoInviteTools = new Dictionary<string, string>
    {
        ["mcp"] = "MCP has no invite tools; the generic connapse_api_* tools arrive in Phase 5 of #783",
    };

    /// <summary>
    /// The invite operations (CON-70), on REST and the User management page's invite card. Every case
    /// invites an address named after its container, so a case sees only its own invites.
    /// </summary>
    private static readonly Operation[] InviteOperations =
    [
        // Lists pending invites; the case's own was made before the action.
        new("invites_list", [Case.Valid, Case.UnderScoped], Writes: false, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                for (int skip = 0; ; skip += 200)
                {
                    var response = await rest.GetAsync($"/api/v1/invites?skip={skip}&take=200");
                    if (!response.IsSuccessStatusCode)
                        return await Surfaces.ResultAsync(response);
                    using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (page.RootElement.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("email").GetString() == InviteEmail(c)))
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
                return "ok " + Listed(InviteRow(page, c) is not null);
            },
            Project: (sp, c) => InviteStateAsync(sp, c))
        {
            AbsentReasons = NoInviteTools,
            Prepare = (f, c, k) => ArrangeInviteAsync(f, c),
        },

        // Invites the case's address as a Viewer. The invalid case's address has no domain; preflight
        // invites the case's own user, who already has an account.
        new("invites_create", [Case.Valid, Case.Invalid, Case.UnderScoped, Case.Preflight], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                var response = await rest.PostAsJsonAsync("/api/v1/invites", new { email = InviteAddress(c, k), role = "Viewer" });
                // The created invite is in the state; its id and one-time token are this surface's alone.
                return response.IsSuccessStatusCode ? "ok" : await Surfaces.ResultAsync(response);
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<UserManagement>();
                if (UsersPageError(page) is { } error)
                    return error;
                await page.InvokeAsync(() => page.Find("#inviteEmail").Change(InviteAddress(c, k)));
                await page.InvokeAsync(() => page.Find("#inviteRole").Change("Viewer"));
                await ClickAsync(page, () => page.FindAll("button").First(b => b.TextContent.Trim() == "Send Invite"));
                return UsersPageOutcome(page);
            },
            Project: (sp, c) => InviteStateAsync(sp, c))
        {
            AbsentReasons = NoInviteTools,
            ArrangesUser = true,
        },

        // Revokes the case's invite. The invalid case names one that doesn't exist.
        new("invites_revoke", [Case.Valid, Case.Invalid, Case.UnderScoped], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                string target = k == Case.Invalid ? c.Unknown : (await InviteIdAsync(f, c)).ToString();
                return await Surfaces.ResultAsync(await rest.DeleteAsync($"/api/v1/invites/{target}"));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                if (k == Case.Invalid)
                    return null;
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<UserManagement>();
                if (UsersPageError(page) is { } error)
                    return error;
                await ClickAsync(page, () => InviteRow(page, c)!.QuerySelectorAll("button").First(b => b.TextContent.Trim() == "Revoke"));
                return UsersPageOutcome(page);
            },
            Project: (sp, c) => InviteStateAsync(sp, c))
        {
            AbsentReasons = NoInviteTools,
            Prepare = (f, c, k) => ArrangeInviteAsync(f, c),
        },
    ];

    /// <summary>The address a case invites: its own, one without a domain for the invalid case, or its user's for preflight.</summary>
    private static string InviteAddress(CaseContext c, string @case) => @case switch
    {
        Case.Invalid => $"invite-{c.ContainerName}",
        Case.Preflight => c.User!.Email,
        _ => InviteEmail(c),
    };

    private static string InviteEmail(CaseContext c) => $"invite-{c.ContainerName}@cross-surface.connapse.io";

    private static IElement? InviteRow(IRenderedComponent<UserManagement> page, CaseContext c) =>
        page.FindAll("tbody tr").FirstOrDefault(r =>
            r.QuerySelector("td")?.TextContent.Trim() == InviteEmail(c)
            && r.QuerySelectorAll("button").Any(b => b.TextContent.Trim() == "Revoke"));

    /// <summary>Invites the case's address as a Viewer, from the admin, as the invite card would.</summary>
    private static async Task ArrangeInviteAsync(SharedWebAppFixture fixture, CaseContext c)
    {
        Guid admin = await Principals.UserIdAsync(fixture, Principal.Admin);
        using var scope = fixture.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<InviteService>().CreateInviteAsync(InviteEmail(c), "Viewer", admin);
    }

    private static async Task<Guid> InviteIdAsync(SharedWebAppFixture fixture, CaseContext c)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();
        return await db.UserInvitations.Where(i => i.Email == InviteEmail(c)).Select(i => i.Id).SingleAsync();
    }

    /// <summary>Every invite a case may have made: any address with its container's name in it.</summary>
    private static async Task<StateProjection> InviteStateAsync(IServiceProvider services, CaseContext c)
    {
        var db = services.GetRequiredService<ConnapseIdentityDbContext>();
        var invites = await db.UserInvitations.AsNoTracking()
            .Where(i => i.Email.Contains(c.ContainerName))
            .OrderBy(i => i.Email)
            .ToListAsync();
        if (invites.Count == 0)
            return new StateProjection(["no invite"], []);
        return new StateProjection(
            invites.Select(i => $"invite {i.Email} role={i.Role} {(i.AcceptedAt is null ? "pending" : "accepted")}").ToList(),
            invites.Select(i => i.Id.ToString()).ToList());
    }

    private static async Task CleanupInvitesAsync(SharedWebAppFixture fixture, CaseContext c)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();
        await db.UserInvitations.Where(i => i.Email.Contains(c.ContainerName)).ExecuteDeleteAsync();
    }
}
