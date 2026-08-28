using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// What the integrations page promises about a connected AWS identity.
/// </summary>
/// <remarks>
/// Wording, not markup. This page already carries a test pinning it to *not* claiming that linking
/// an identity filters search results, because it once did and that was false. The same care
/// applies to the new card: connecting stores a token so Connapse can check permissions later, and
/// saying more than that would be the same defect again.
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
        Markup.Should().Contain("cognito/connect",
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
        // Nothing filters until 5d registers a resolver. Promising it here would repeat exactly
        // the defect #422 removed from this same page.
        Markup.Should().NotContain("results are narrowed");
        Markup.Should().NotContain("only the documents you can");
    }

    [Fact]
    public void Page_CanBeRead()
    {
        // A source-pinning test that passes when it cannot find its subject is worse than none.
        Markup.Should().NotBeNullOrWhiteSpace();
    }
}
