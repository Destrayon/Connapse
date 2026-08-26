using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// Identity providers decide who may sign into Connapse and which cloud identity their search
/// is scoped against. They moved out of Settings, and so out from behind Settings' own gate.
/// </summary>
/// <remarks>
/// Reflection over the compiled component rather than a rendered circuit, matching
/// <see cref="ConnectionsPageAuthorizationTests"/>. It cannot prove a request is refused, but it
/// catches the attribute being dropped or weakened, which is how this protection would
/// realistically be lost.
/// </remarks>
[Trait("Category", "Unit")]
public class ProvidersPageTests
{
    private static readonly Type Page =
        typeof(Connapse.Web.Components.Pages.Providers);

    [Fact]
    public void ProvidersPage_IsRoutable()
    {
        Page.GetCustomAttributes(typeof(RouteAttribute), inherit: true)
            .Cast<RouteAttribute>()
            .Select(r => r.Template)
            .Should().Contain("/admin/providers");
    }

    [Fact]
    public void ProvidersPage_RequiresAnAdministrator()
    {
        var authorize = Page
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .ToList();

        authorize.Should().ContainSingle("the page is reachable directly by URL, not only via the nav");

        authorize[0].Policy.Should().Be("RequireAdmin",
            "these settings decide who can sign in at all — a bare [Authorize] would expose them "
            + "to every signed-in viewer");
    }

    [Fact]
    public void ProvidersPage_GateMatchesTheSettingsPageItLeft()
    {
        var settings = typeof(Connapse.Web.Components.Pages.Settings)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        Page.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single()
            .Policy.Should().Be(settings.Policy,
                "being moved must not have made it easier to reach");
    }

    /// <summary>
    /// The settings-table keys the two tabs save under.
    /// </summary>
    /// <remarks>
    /// The one change here that would fail silently rather than loudly. Settings are stored by
    /// category string in PostgreSQL; renaming a key on the way out of Settings.razor would not
    /// break the build or lose the tab, it would just start writing to a row nothing reads, and
    /// an administrator's configured issuer URL would appear to revert.
    /// </remarks>
    [Theory]
    [InlineData("awssso")]
    [InlineData("azuread")]
    public void ProvidersPage_KeepsTheSettingsKeysItInherited(string category)
    {
        string markup = File.ReadAllText(Path.Combine(
            PageTestPaths.RepositoryRoot(),
            "src", "Connapse.Web", "Components", "Pages", "Providers.razor"));

        markup.Should().Contain($"SaveSettings(\"{category}\"",
            "renaming the category orphans whatever is already stored under the old one");
    }

    /// <summary>
    /// Settings must not keep a tab whose body was removed.
    /// </summary>
    [Trait("Category", "Unit")]
    public class SettingsPageTests
    {
        private static readonly string Markup = File.ReadAllText(Path.Combine(
            PageTestPaths.RepositoryRoot(),
            "src", "Connapse.Web", "Components", "Pages", "Settings.razor"));

        [Theory]
        [InlineData("awssso")]
        [InlineData("azuread")]
        public void SettingsPage_NoLongerOffersTheIdentityProviderTabs(string tab)
        {
            // A leftover button with no matching body is the realistic half-finished move: it
            // compiles, renders, and selects a tab that draws nothing at all.
            Markup.Should().NotContain(tab,
                "the tab moved to /admin/providers");
        }

        [Theory]
        [InlineData("AwsSsoSettingsTab")]
        [InlineData("AzureAdSettingsTab")]
        public void SettingsPage_NoLongerRendersTheIdentityProviderComponents(string component)
        {
            Markup.Should().NotContain(component);
        }
    }

    /// <summary>
    /// The administrator-only entries live in one labelled group in the nav.
    /// </summary>
    /// <remarks>
    /// Read from the source file rather than a rendered layout. The two mistakes worth catching
    /// — an entry escaping the group, and the group escaping its role check — are both visible
    /// in the text, and rendering NavMenu needs an authentication state provider for a component
    /// with no seam otherwise worth testing through.
    /// </remarks>
    [Trait("Category", "Unit")]
    public class AdminNavGroupTests
    {
        private static readonly string Markup = File.ReadAllText(Path.Combine(
            PageTestPaths.RepositoryRoot(),
            "src", "Connapse.Web", "Components", "Layout", "NavMenu.razor"));

        public static TheoryData<string> AdminRoutes() =>
            new() { "connections", "admin/providers", "settings", "admin/users", "admin/agents" };

        [Theory]
        [MemberData(nameof(AdminRoutes))]
        public void EveryAdminEntry_SitsInsideTheAdminGroup(string route)
        {
            int link = Markup.IndexOf($"href=\"{route}\"", StringComparison.Ordinal);
            link.Should().BePositive($"the nav should link to {route}");

            int groupOpen = Markup.IndexOf("<details class=\"nav-group\"", StringComparison.Ordinal);
            int groupClose = Markup.IndexOf("</details>", StringComparison.Ordinal);

            groupOpen.Should().BePositive("the admin entries belong in one labelled group");
            link.Should().BeInRange(groupOpen, groupClose,
                $"{route} is administrator-only and belongs inside the group");
        }

        [Fact]
        public void TheAdminGroup_SitsInsideTheRoleCheck()
        {
            // The group is a presentation device. If it drifted outside AuthorizeView it would
            // still look correct to an administrator while showing a viewer a list of pages
            // every one of which refuses them.
            int roleCheck = Markup.IndexOf(
                "<AuthorizeView Roles=\"Owner,Admin\"", StringComparison.Ordinal);
            int groupOpen = Markup.IndexOf("<details class=\"nav-group\"", StringComparison.Ordinal);

            roleCheck.Should().BePositive();
            groupOpen.Should().BeGreaterThan(roleCheck);
        }

        [Fact]
        public void TheAdminGroup_NeedsNoRenderMode()
        {
            // NavMenu is in the layout and renders statically. A toggle driven by @onclick would
            // silently do nothing — the same failure that took the Connections page down — so the
            // disclosure is a <details> element the browser operates itself.
            Markup.Should().NotContain("@onclick",
                "the layout has no render mode, so a handler here would never run");
        }
    }

    [Fact]
    public void ProvidersPage_KeepsItsOldRoute()
    {
        // Renamed from Identity Providers. Links and bookmarks made under the old name must still
        // land somewhere rather than 404 -- a rename is not a reason to break them.
        Page.GetCustomAttributes(typeof(RouteAttribute), inherit: true)
            .Cast<RouteAttribute>()
            .Select(r => r.Template)
            .Should().Contain("/admin/identity-providers");
    }

    /// <summary>
    /// The page reports; it does not store.
    /// </summary>
    /// <remarks>
    /// The line that keeps this from becoming a service-account concept above Connection. Connection
    /// already <i>is</i> the credential boundary, with encrypted secret storage; a provider that
    /// held credentials would be a second one, and neither Airbyte nor Fivetran has such a layer.
    /// Asserted because the pressure to "just put the key here" will be constant.
    /// </remarks>
    [Fact]
    public void ProvidersPage_DoesNotTouchTheConnectionStore()
    {
        string markup = File.ReadAllText(Path.Combine(
            PageTestPaths.RepositoryRoot(),
            "src", "Connapse.Web", "Components", "Pages", "Providers.razor"));

        markup.Should().NotContain("IConnectionStore",
            "credentials belong on Connections, and a page that reads them will soon write them");
        markup.Should().NotContain("CreateConnectionRequest");
    }

}

/// <summary>Shared path helper for tests that read component source rather than render it.</summary>
internal static class PageTestPaths
{
    public static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
