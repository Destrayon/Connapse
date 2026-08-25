using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// Connections is the credential and filesystem-root boundary: a connection names a cloud
/// identity or a directory on somebody's machine, and every source built on it inherits that
/// reach. Moving it out of Settings moved it out from behind Settings' own gate, so the page
/// has to carry one itself.
/// </summary>
/// <remarks>
/// Reflection over the compiled component rather than a rendered circuit. It cannot prove a
/// request is refused — an anonymous GET being redirected to /login shows that, and does not
/// distinguish an editor from an administrator — but it does catch the attribute being dropped
/// or weakened, which is the way this protection would realistically be lost.
/// </remarks>
public class ConnectionsPageAuthorizationTests
{
    private static readonly Type Page =
        typeof(Connapse.Web.Components.Pages.Connections);

    [Fact]
    public void ConnectionsPage_IsRoutable()
    {
        Page.GetCustomAttributes(typeof(RouteAttribute), inherit: true)
            .Cast<RouteAttribute>()
            .Select(r => r.Template)
            .Should().Contain("/connections");
    }

    [Fact]
    public void ConnectionsPage_RequiresAnAdministrator()
    {
        var authorize = Page
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .ToList();

        authorize.Should().ContainSingle("the page is reachable directly by URL, not only via a link");

        authorize[0].Policy.Should().Be("RequireAdmin",
            "a bare [Authorize] would let any signed-in viewer create a connection");
    }

    [Fact]
    public void ConnectionsPage_GateMatchesTheSettingsPageItLeft()
    {
        // It was previously protected by Settings' own attribute. Whatever else changes, it must
        // not have become easier to reach by being moved.
        var settings = typeof(Connapse.Web.Components.Pages.Settings)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        var connections = Page
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        connections.Policy.Should().Be(settings.Policy);
    }

    /// <summary>
    /// The Sources page links to Connections and offers a button to create one. Both belong to
    /// administrators, and both used to point at /settings, where the page no longer lives.
    /// </summary>
    /// <remarks>
    /// Read from the source file rather than a rendered component. Markup this simple has no
    /// seam worth testing through, and the two mistakes worth catching — a link left pointing at
    /// the old location, and a control escaping its isAdmin block — are both visible in the text.
    /// </remarks>
    public class SourcesPageConnectionLinkTests
    {
        private static readonly string Markup = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "Connapse.Web", "Components", "Pages", "Sources.razor"));

        [Fact]
        public void SourcesPage_LinksToTheConnectionsPage()
        {
            Markup.Should().Contain("href=\"/connections\"");
            Markup.Should().NotContain("href=\"/settings\"",
                "Connections moved out of Settings, so a link there lands on a page without it");
        }

        [Fact]
        public void SourcesPage_OffersConnectionsOnlyToAdministrators()
        {
            // Every mention sits inside an isAdmin block. Checked by counting rather than by
            // parsing: a Viewer offered a button to a page they cannot open is a dead end, and
            // one offered a "New source" button has no connection to attach it to anyway.
            int connectionsLinks = Markup.Split("href=\"/connections\"").Length - 1;

            connectionsLinks.Should().Be(2, "the header button and the empty-state link");

            foreach (string fragment in Markup.Split("href=\"/connections\"")[..^1])
            {
                fragment.Should().Contain("isAdmin",
                    "a link to an administrator-only page must not be shown to everyone");
            }
        }

        private static string RepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
                dir = dir.Parent;

            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }
}
