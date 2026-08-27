using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Search;

/// <summary>
/// The rule applied to whatever a resolver hands back, before anything acts on it.
/// </summary>
[Trait("Category", "Unit")]
public class ScopeResolutionGuardTests
{
    [Fact]
    public void Guard_WhenNoUserAndResolverReturnedGrants_Refuses()
    {
        // A resolver that produces grants for a caller with no user has answered a question nobody
        // asked. Refused here rather than trusting every implementation to remember, because the
        // surfaces that legitimately have no user -- MCP, personal access tokens -- are exactly the
        // ones where believing it would be a hole rather than a bug.
        var resolved = SearchScopes.Of(["s3://acme/team/"]);

        ScopeResolution.Guard(resolved, userId: null)
            .Should().BeSameAs(SearchScopes.NoPrincipal);
    }

    [Fact]
    public void Guard_WhenNoUserAndDeploymentDoesNotFilter_LeavesItAlone()
    {
        // Not filtering is not a permission decision, so a caller with no user is no worse off
        // than any other. Forcing a denial here would break every existing installation.
        ScopeResolution.Guard(SearchScopes.Unrestricted, userId: null)
            .Should().BeSameAs(SearchScopes.Unrestricted);
    }

    [Fact]
    public void Guard_WhenUserIsPresent_PassesTheAnswerThrough()
    {
        var resolved = SearchScopes.Of(["s3://acme/team/"]);

        ScopeResolution.Guard(resolved, userId: Guid.NewGuid())
            .Should().BeSameAs(resolved);
    }

    [Fact]
    public void Guard_WhenNoUserAndResolverDenied_KeepsTheReason()
    {
        // Already a denial, and its reason is more specific than NoPrincipal would be.
        ScopeResolution.Guard(SearchScopes.None, userId: null)
            .Should().BeSameAs(SearchScopes.None);
    }
}
