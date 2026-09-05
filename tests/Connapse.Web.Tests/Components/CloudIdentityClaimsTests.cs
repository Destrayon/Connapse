using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// Pins the product to not claiming a permission filter it does not have.
/// </summary>
/// <remarks>
/// Three places said search was filtered by the user's cloud permissions: the architecture doc, the
/// comment justifying why Sources has no file tree, and the Integrations page a user reads before
/// linking an account. None of it happened — <c>CloudScopeService</c> has no production caller and
/// <c>SearchOptions</c> carries no principal.
/// <para>
/// The Integrations one mattered most: it is user-facing, and it is attached to a button that
/// visibly succeeds. Someone links an account, sees "Connected", and concludes their results are
/// being narrowed.
/// </para>
/// <para>
/// These assertions are deliberately about wording rather than behaviour, because the defect was
/// wording. They should be deleted — not adjusted — when filtering actually lands (#421), and the
/// deletion is the signal that the claim has become true.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public class CloudIdentityClaimsTests
{
    private static string Read(params string[] relativePath) =>
        File.ReadAllText(Path.Combine(
            new[] { PageTestPaths.RepositoryRoot() }.Concat(relativePath).ToArray()));

    private static string Integrations => Read(
        "src", "Connapse.Web", "Components", "Pages", "ProfileIntegrations.razor");

    [Fact]
    public void Integrations_DoesNotPromiseThatLinkingNarrowsSearchResults()
    {
        // The exact sentence that was there. Not a fuzzy match: this test exists to fail if
        // somebody restores the old copy, and a loose pattern would also fire on an honest
        // rewording that happens to use the same verb.
        Integrations.Should().NotContain("narrowed to");
        Integrations.Should().NotContain("what your own cloud permissions allow");
    }

    [Fact]
    public void Integrations_SaysWhatLinkingActuallyDoesOnThisDeployment()
    {
        // A missing false claim is not the same as a true statement. Someone linking an account
        // deserves to be told what it does and does not do, in the place they are deciding.
        //
        // This used to assert one fixed sentence saying filtering was not implemented. That was
        // true when written and became false in the more dangerous direction once per-user
        // permissions shipped: a deployment that filters was telling people it did not. The page
        // now follows the enforcement state, so what is asserted is that all three states are
        // answered rather than that any one sentence is present.
        Integrations.Should().Contain("EnforcementState.Enforcing");
        Integrations.Should().Contain("EnforcementState.EnforcingButUnusable");
        Integrations.Should().Contain("filtered to what you may read");
        Integrations.Should().Contain("does not filter your search results on this deployment");
    }

    [Fact]
    public void Integrations_DoesNotClaimPermissionsAreUnimplemented()
    {
        // The specific stale sentence, and the issue it pointed at. Restoring either would tell an
        // enforcing deployment's users that nothing is protecting them.
        Integrations.Should().NotContain("permissions are not implemented");
        Integrations.Should().NotContain("does not yet filter your search results");
    }

    [Fact]
    public void ArchitectureDoc_DoesNotDescribeEnforcementThatDoesNotRun()
    {
        string doc = Read("docs", "architecture.md");

        doc.Should().NotContain(
            "CloudScopeService` checks cloud identity permissions before allowing access");

        // It also listed four enforcement points by name, none of which exist.
        doc.Should().NotContain(
            "Enforcement applied to: document endpoints, search endpoints, folder endpoints");
    }

    [Fact]
    public void SourcesPage_DoesNotJustifyItsDesignWithACheckThatDoesNotHappen()
    {
        // Hiding the file tree is still right. The reasoning just cannot lean on search being
        // guarded, because it is not.
        Read("src", "Connapse.Web", "Components", "Pages", "Sources.razor")
            .Should().NotContain("search already passes through CloudScopeService");
    }
}
