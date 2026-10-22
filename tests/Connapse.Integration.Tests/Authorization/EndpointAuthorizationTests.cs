using FluentAssertions;

namespace Connapse.Integration.Tests.Authorization;

/// <summary>
/// Proof test for #793 (P6, P9): every route has a committed authorization expectation, and its
/// metadata declares what the expectation says. Today's gaps are in known-authz-violations.json;
/// a ticket that closes one deletes its entry.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class EndpointAuthorizationTests(SharedWebAppFixture fixture)
{
    [Fact]
    public void EveryEndpoint_HasExplicitAuthorizationExpectation()
    {
        var endpoints = AuthzExpectations.Endpoints(fixture);
        var table = AuthzExpectations.Table.Value.Routes;

        endpoints.Keys.Except(table.Keys).Should().BeEmpty(
            "every route needs an entry in authorization-expectations.json saying who may call it");
        table.Keys.Except(endpoints.Keys).Should().BeEmpty(
            "an entry for a route that no longer exists should be deleted");
        table.Where(t => !AuthzExpectations.Levels.Contains(t.Value.Requires) || !AuthzExpectations.Risks.Contains(t.Value.Risk))
            .Select(t => t.Key).Should().BeEmpty("requires and risk must use the table's words");

        var violations = endpoints
            .Select(e => (e.Key, Expected: table[e.Key].Requires, Declared: AuthzExpectations.Declared(e.Value)))
            .Where(e => e.Expected != e.Declared)
            .Select(e => $"declared {e.Key}: expects {e.Expected}, declares {e.Declared}");

        AuthzExpectations.ShouldMatchKnown(violations, "declared ");
    }
}
