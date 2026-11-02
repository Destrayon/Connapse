using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Connapse.Core;
using Connapse.Identity.Authentication;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using Connapse.Web.Components.Pages;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>A case's own personal access token, the admin's, named after the case's container.</summary>
public sealed record TokenCase(Guid Id, string Name);

public static partial class Operations
{
    /// <summary>MCP never reaches personal access tokens: the manifest keeps them off it (pats.*: mcpOff).</summary>
    private static readonly IReadOnlyDictionary<string, string> NoTokenTools = new Dictionary<string, string>
    {
        ["mcp"] = "MCP never reaches personal access tokens: they are credentials for the caller's own account",
    };

    /// <summary>
    /// The personal access token operations (CON-72), on REST and the API Tokens page. Each person sees and
    /// revokes only their own tokens, so an under-scoped caller (a Viewer, or a user with no role) is
    /// answered as one with none of the admin's. The page acts only on the tokens it lists, so it has no way
    /// to name one that doesn't exist, or someone else's: those revoke cases are REST's alone.
    /// </summary>
    private static readonly Operation[] TokenOperations =
    [
        // Lists the caller's tokens. Besides the admin's, the case arranges one of the Viewer's, so each surface
        // must answer exactly the caller's own: the admin sees only theirs, and a user with no role sees neither.
        new("pats_list", [Case.Valid, Case.UnderScoped], Writes: false, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                var response = await rest.GetAsync("/api/v1/auth/pats");
                return await WithPayloadAsync(response, body => CaseTokens(c, TokenNames(body)));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<PersonalTokens>();
                if (TokensPageError(page) is { } error)
                    return error;
                return "ok " + CaseTokens(c, page.FindAll("tbody tr").Select(r => r.QuerySelector("td")?.TextContent.Trim()));
            },
            Project: (sp, c) => TokenStateAsync(sp, c))
        {
            AbsentReasons = NoTokenTools,
            ArrangesToken = true,
            Prepare = async (f, c, k) => await ArrangeViewersTokenAsync(f, c),
        },

        // Makes a token named after the case that reads knowledge. The invalid case's name is blank.
        new("pats_create", [Case.Valid, Case.Invalid, Case.UnderScoped], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                var response = await rest.PostAsJsonAsync("/api/v1/auth/pats", new { name = NewTokenName(c, k), scopes = new[] { "knowledge:read" } });
                if (!response.IsSuccessStatusCode)
                    return await Surfaces.ResultAsync(response);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return await PatTokenAsync(f, c, k, body.RootElement.GetProperty("token").GetString());
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<PersonalTokens>();
                if (TokensPageError(page) is { } error)
                    return error;
                await ClickAsync(page, () => page.FindAll("button").First(b => b.TextContent.Trim() == "New Token"));
                await page.InvokeAsync(() => page.Find("input[placeholder='e.g. my-script']").Change(NewTokenName(c, k)));
                await ClickAsync(page, () => page.FindAll("button").First(b => b.TextContent.Trim() == "Create Token"));
                string outcome = TokensPageOutcome(page);
                return outcome != "ok" ? outcome : await PatTokenAsync(f, c, k, page.Find("#new-pat-token").GetAttribute("value"));
            },
            Project: (sp, c) => TokenStateAsync(sp, c))
        {
            AbsentReasons = NoTokenTools,
            ArrangesToken = true,
        },

        // Revokes the case's token. The invalid case names one that doesn't exist; the under-scoped caller
        // isn't the token's owner, so it's not found for them either.
        new("pats_revoke", [Case.Valid, Case.Invalid, Case.UnderScoped], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                string token = k == Case.Invalid ? c.Unknown : c.Token!.Id.ToString();
                return await Surfaces.ResultAsync(await rest.DeleteAsync($"/api/v1/auth/pats/{token}"));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                if (k != Case.Valid)
                    return null;
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<PersonalTokens>();
                if (TokensPageError(page) is { } error)
                    return error;
                await ClickAsync(page, () => TokenButton(page, c, "Revoke"));
                await ClickAsync(page, () => TokenButton(page, c, "Confirm Revoke"));
                return TokensPageOutcome(page);
            },
            Project: (sp, c) => TokenStateAsync(sp, c))
        {
            AbsentReasons = new Dictionary<string, string>(NoTokenTools)
            {
                ["ui"] = "the API Tokens page revokes only the caller's own tokens it lists, so it can't name one that doesn't exist or isn't theirs",
            },
            ArrangesToken = true,
        },
    ];

    private static string TokenName(string containerName) => $"pat-{containerName}";

    private static string ViewersTokenName(CaseContext c) => $"{c.Token!.Name}-viewers";

    /// <summary>Which of the case's tokens a list holds, in the surfaces' shared words: whose each is, in order.</summary>
    private static string CaseTokens(CaseContext c, IEnumerable<string?> names) =>
        "tokens=[" + string.Join(",", names.Where(n => n?.Contains(c.ContainerName, StringComparison.Ordinal) == true)
            .Select(n => n == c.Token!.Name ? "admin's" : n == ViewersTokenName(c) ? "viewer's" : "other").Order(StringComparer.Ordinal)) + "]";

    /// <summary>Gives the Viewer a token named after the case, which no one else's list may show.</summary>
    private static async Task ArrangeViewersTokenAsync(SharedWebAppFixture fixture, CaseContext c)
    {
        Guid viewer = await Principals.UserIdAsync(fixture, Principal.Viewer);
        using var scope = fixture.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PatService>().CreateAsync(viewer, new PatCreateRequest(ViewersTokenName(c), ["knowledge:read"]));
    }

    private static string NewTokenName(CaseContext c, string @case) => @case == Case.Invalid ? "   " : $"{c.Token!.Name}-{@case[..2]}";

    /// <summary>
    /// What the token a surface handed back is: the new one's, if it carries that token's stored prefix and
    /// signs in to REST; unusable otherwise. Its value is this surface's alone, so only that is compared.
    /// </summary>
    private static async Task<string> PatTokenAsync(SharedWebAppFixture fixture, CaseContext c, string @case, string? token)
    {
        string? prefix;
        using (var scope = fixture.Factory.Services.CreateScope())
            prefix = await scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>().PersonalAccessTokens.AsNoTracking()
                .Where(t => t.Name == NewTokenName(c, @case)).Select(t => t.TokenPrefix).SingleOrDefaultAsync();
        if (token is null || prefix is null || !token.StartsWith(prefix, StringComparison.Ordinal))
            return "ok token=unusable";

        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationOptions.HeaderName, token);
        using var response = await client.GetAsync("/api/v1/auth/me");
        return response.IsSuccessStatusCode ? "ok token=the-new-token" : "ok token=unusable";
    }

    private static List<string?> TokenNames(string body) =>
        JsonDocument.Parse(body).RootElement.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();

    private static IElement? TokenRow(IRenderedComponent<PersonalTokens> page, string name) =>
        page.FindAll("tbody tr").FirstOrDefault(r => r.QuerySelector("td")?.TextContent.Trim() == name);

    private static IElement TokenButton(IRenderedComponent<PersonalTokens> page, CaseContext c, string text) =>
        TokenRow(page, c.Token!.Name)!.QuerySelectorAll("button").First(b => b.TextContent.Trim() == text);

    /// <summary>Waits for the page to load its tokens; the error it showed instead, or null.</summary>
    private static string? TokensPageError(IRenderedComponent<PersonalTokens> page)
    {
        page.WaitForAssertion(() => page.FindAll("tbody tr, div.card-body.text-muted, div.alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
        return page.FindAll("div.alert-danger").FirstOrDefault() is { } alert ? Surfaces.ErrorKind(alert.TextContent.Trim()) : null;
    }

    /// <summary>What the page said after an action: its success alert, or its error in the shared words.</summary>
    private static string TokensPageOutcome(IRenderedComponent<PersonalTokens> page)
    {
        page.WaitForAssertion(() => page.FindAll("div.alert-success, div.alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
        return page.FindAll("div.alert-danger").FirstOrDefault() is { } alert ? Surfaces.ErrorKind(alert.TextContent.Trim()) : "ok";
    }

    /// <summary>Makes the case's own token, the admin's, that reads knowledge.</summary>
    private static async Task<TokenCase> ArrangeTokenAsync(SharedWebAppFixture fixture, string containerName)
    {
        Guid admin = await Principals.UserIdAsync(fixture, Principal.Admin);
        using var scope = fixture.Factory.Services.CreateScope();
        var token = await scope.ServiceProvider.GetRequiredService<PatService>()
            .CreateAsync(admin, new PatCreateRequest(TokenName(containerName), ["knowledge:read"]));
        return new TokenCase(token.Id, token.Name);
    }

    /// <summary>
    /// Every token a case may have made, revoked ones included: any name with its container's name in it,
    /// with whose it is. Ids are listed in name order, so each surface's are given the same tokens.
    /// </summary>
    private static async Task<StateProjection> TokenStateAsync(IServiceProvider services, CaseContext c)
    {
        var db = services.GetRequiredService<ConnapseIdentityDbContext>();
        var tokens = await db.PersonalAccessTokens.AsNoTracking()
            .Where(t => t.Name.Contains(c.ContainerName))
            .OrderBy(t => t.Name)
            .Join(db.Users, t => t.UserId, u => u.Id, (t, u) => new { t.Id, t.Name, Owner = u.Email, t.Scopes, t.RequiresApproval, t.ExpiresAt, t.RevokedAt })
            .ToListAsync();
        var state = tokens.Select(t => $"token {t.Name} owner={t.Owner} scopes={t.Scopes} approval={t.RequiresApproval} "
            + $"expires={(t.ExpiresAt is null ? "never" : "set")} {(t.RevokedAt is null ? "active" : "revoked")}").ToList();
        var ids = tokens.Select(t => t.Id.ToString()).ToList();
        if (c.Token is { } own)
            ids.Add(own.Id.ToString());
        return new StateProjection(state.Count == 0 ? ["no token"] : state, ids.Distinct().ToList());
    }

    private static async Task CleanupTokensAsync(SharedWebAppFixture fixture, CaseContext c)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();
        await db.PersonalAccessTokens.Where(t => t.Name.Contains(c.ContainerName)).ExecuteDeleteAsync();
    }
}
