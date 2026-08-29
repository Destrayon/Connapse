using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// What the integrations page promises about a connected AWS identity.
/// </summary>
/// <remarks>
/// Wording, not markup. This page already carries a test pinning it to *not* claiming that linking
/// an identity filters search results, because it once did and that was false. The same care
/// applies to what connecting stores: it records which directory user somebody is and nothing that
/// could act as them, and saying either more or less than that would be the same defect again.
/// </remarks>
[Trait("Category", "Unit")]
public class AwsIdentityLinkCopyTests
{
    private static readonly string Markup =
        File.ReadAllText(Path.Combine(
            PageTestPaths.RepositoryRoot(),
            "src", "Connapse.Web", "Components", "Pages", "ProfileIntegrations.razor"));

    [Fact]
    public void Page_OffersToConnectAnAwsIdentity()
    {
        Markup.Should().Contain("aws/connect",
            "the card has to actually start the flow");
    }

    [Fact]
    public void Page_SaysWhyConnectingMatters()
    {
        Markup.Should().Contain("permission",
            "a user asked to connect an account deserves to know what it buys");
    }

    [Fact]
    public void Page_DoesNotClaimSearchIsAlreadyFiltered()
    {
        // Nothing filters until a resolver is registered. Promising it here would repeat exactly
        // the defect #422 removed from this same page.
        Markup.Should().NotContain("results are narrowed");
        Markup.Should().NotContain("only the documents you can");
    }

    [Fact]
    public void Page_DoesNotClaimAnyTokenIsStored()
    {
        // This assertion is the inverse of the one it replaces, and deliberately so. The intro
        // used to have to admit that connecting AWS stored an encrypted refresh token, because it
        // did. Sign-in goes straight to IAM Identity Center now and the link holds an attested
        // identity rather than a credential, so a page still describing a stored token would be
        // frightening people about something that no longer exists — and would go stale in the
        // direction that makes the product look worse than it is.
        Markup.Should().NotContain("refresh token");
        Markup.Should().Contain("no tokens",
            "the intro should now say plainly that nothing able to act as the user is kept");
    }

    [Fact]
    public void Page_CanBeRead()
    {
        // A source-pinning test that passes when it cannot find its subject is worse than none.
        Markup.Should().NotBeNullOrWhiteSpace();
    }
}
