using Connapse.Core;
using Connapse.Storage.CloudScope;
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

    [Theory]
    [InlineData(
        "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default/containers/docs",
        "azblob://acct/docs/")]
    [InlineData(
        "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct",
        "azblob://acct/")]
    [InlineData(
        "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default",
        "azblob://acct/")]
    [InlineData("/subscriptions/s/resourceGroups/rg", "azblob://")]
    [InlineData("/subscriptions/s", "azblob://")]
    [InlineData("/providers/Microsoft.Management/managementGroups/mg", "azblob://")]
    public void ToAzblobPrefix_MapsScopeToPrefix(string armScope, string expected) =>
        AzureRbacScopeTranslator.ToAzblobPrefix(armScope).Should().Be(expected);

    [Fact]
    public void ToAzblobPrefix_IsCaseInsensitiveOnResourceProviderSegments() =>
        AzureRbacScopeTranslator.ToAzblobPrefix(
            "/subscriptions/s/resourceGroups/rg/providers/microsoft.storage/STORAGEACCOUNTS/Acct/blobservices/default/CONTAINERS/Docs")
            .Should().Be("azblob://Acct/Docs/");
}
