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

    [Fact]
    public void Parse_CompoundCondition_MoreThanOneAttribute_IsUnparseable()
    {
        // A recognized clause combined with anything else must not be partially honored.
        string compound =
            "((!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:path] StringLike 'readonly/*' AND @Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags:Secret<$key_case_sensitive$>] StringEquals 'no'))";
        AzureAbacConditionParser.Parse(compound).Kind.Should().Be(AbacKind.Unparseable);
    }

    private const string Guard =
        "(!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'}))";

    [Fact]
    public void Parse_PathStringStartsWith_IsPrefix()
    {
        string cond = $"({Guard} OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:path] StringStartsWith 'reports/'))";
        AbacResult r = AzureAbacConditionParser.Parse(cond);
        r.Kind.Should().Be(AbacKind.PathPrefix);
        r.PathPrefix.Should().Be("reports/");
    }

    [Fact]
    public void Parse_PathStringLike_MidWildcard_IsUnparseable()
    {
        string cond = $"({Guard} OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:path] StringLike 'a*b*'))";
        AzureAbacConditionParser.Parse(cond).Kind.Should().Be(AbacKind.Unparseable);
    }

    [Fact]
    public void Parse_PathStringLike_NoWildcard_IsUnparseable()
    {
        // StringLike with no wildcard is an EXACT match in Azure, not a prefix — must not become one.
        string cond = $"({Guard} OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:path] StringLike 'exact.txt'))";
        AzureAbacConditionParser.Parse(cond).Kind.Should().Be(AbacKind.Unparseable);
    }

    [Fact]
    public void Parse_TagStringEqualsIgnoreCase_IsValueCaseInsensitive()
    {
        string cond = $"({Guard} OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags:Project<$key_case_sensitive$>] StringEqualsIgnoreCase 'Cascade'))";
        AbacResult r = AzureAbacConditionParser.Parse(cond);
        r.Kind.Should().Be(AbacKind.Tag);
        r.TagValue.Should().Be("Cascade");
        r.ValueCaseSensitive.Should().BeFalse();
    }

    [Fact]
    public void Parse_NameStringEqualsIgnoreCase_ReturnsName()
    {
        string cond = $"({Guard} OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers:name] StringEqualsIgnoreCase 'Reports'))";
        AbacResult r = AzureAbacConditionParser.Parse(cond);
        r.Kind.Should().Be(AbacKind.ContainerName);
        r.ContainerName.Should().Be("Reports");
    }

    [Fact]
    public void Parse_RecognizedPredicate_WithoutActionGuard_IsUnparseable()
    {
        // A predicate not wrapped in the canonical action guard must not be honored (an inverted or
        // absent guard could make the predicate NOT gate reads).
        string cond = "(@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:path] StringStartsWith 'private/')";
        AzureAbacConditionParser.Parse(cond).Kind.Should().Be(AbacKind.Unparseable);
    }
}
