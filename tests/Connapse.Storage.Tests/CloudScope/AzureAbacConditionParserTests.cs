using Connapse.Storage.CloudScope;
using FluentAssertions;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AzureAbacConditionParserTests
{
    private const string PathCond =
        "((!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'} AND NOT SubOperationMatches{'Blob.List'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:path] StringLike 'readonly/*'))";
    private const string NameCond =
        "((!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers:name] StringEquals 'reports'))";
    private const string TagCond =
        "((!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags:Project<$key_case_sensitive$>] StringEquals 'Cascade'))";

    [Fact]
    public void Parse_Null_IsNone() =>
        AzureAbacConditionParser.Parse(null).Kind.Should().Be(AbacKind.None);

    [Fact]
    public void Parse_Path_ReturnsPrefix_TrailingStarStripped()
    {
        AbacResult r = AzureAbacConditionParser.Parse(PathCond);
        r.Kind.Should().Be(AbacKind.PathPrefix);
        r.PathPrefix.Should().Be("readonly/");
    }

    [Fact]
    public void Parse_ContainerName_ReturnsName()
    {
        AbacResult r = AzureAbacConditionParser.Parse(NameCond);
        r.Kind.Should().Be(AbacKind.ContainerName);
        r.ContainerName.Should().Be("reports");
    }

    [Fact]
    public void Parse_Tag_ReturnsKeyValue()
    {
        AbacResult r = AzureAbacConditionParser.Parse(TagCond);
        r.Kind.Should().Be(AbacKind.Tag);
        r.TagKey.Should().Be("Project");
        r.TagValue.Should().Be("Cascade");
        r.TagKeyCaseSensitive.Should().BeTrue();
    }

    [Fact]
    public void Parse_UnknownExpression_IsUnparseable() =>
        AzureAbacConditionParser.Parse("@Request[...] DateTimeGreaterThan '2024-01-01'")
            .Kind.Should().Be(AbacKind.Unparseable);
}
