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
}
