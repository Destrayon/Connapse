using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Connapse.Integration.Tests.Authorization;

/// <summary>
/// A canary for CON-73: the scope handler admits any principal holding the scope, an agent key included,
/// so a route whose only policy is a scope isn't a person's route and must not read as one.
/// </summary>
[Trait("Category", "Unit")]
public class ScopedRouteExpectationTests
{
    private static RouteEndpoint Route(params string[] policies) =>
        new(_ => Task.CompletedTask, RoutePatternFactory.Parse("/planted"), 0,
            new EndpointMetadataCollection(policies.Select(p => (object)new AuthorizeAttribute(p))), "planted");

    [Fact]
    public void Declared_ScopeOnlyRoute_DoesNotReadAsAPerson() =>
        AuthzExpectations.Declared(Route("Scope:AdminUsers")).Should().NotBe("user");
}
