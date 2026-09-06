using Connapse.Core;
using FluentAssertions;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AzureRbacScopeTranslatorTests
{
    [Fact]
    public void RbacScopes_Failed_IsEmptyAndFailed()
    {
        AzureRbacScopes f = AzureRbacScopes.Failed();
        f.Outcome.Should().Be(RbacOutcome.Failed);
        f.ReadablePrefixes.Should().BeEmpty();
        f.TagConditioned.Should().BeEmpty();
    }

    [Fact]
    public void RbacScopes_Resolved_CarriesPrefixesAndTags()
    {
        AzureRbacScopes r = AzureRbacScopes.Resolved(
            [new AzureScope("azblob://acct/c/")],
            [new AzureTagCondition("azblob://acct/c/", "Project", "Cascade", true)]);
        r.Outcome.Should().Be(RbacOutcome.Resolved);
        r.ReadablePrefixes.Should().ContainSingle().Which.Prefix.Should().Be("azblob://acct/c/");
        r.TagConditioned.Should().ContainSingle();
    }
}
