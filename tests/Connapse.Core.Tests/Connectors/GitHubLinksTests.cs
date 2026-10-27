using Connapse.Core;
using Connapse.Storage.Connectors.GitHub;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>The links a GitHub record's metadata asserts in the link layer (CON-33).</summary>
[Trait("Category", "Unit")]
public sealed class GitHubLinksTests
{
    private const string Doc = "0b8a4f3e-0000-0000-0000-000000000001";

    private static Dictionary<string, string> Record(string type, int number, params (string Key, string Value)[] extra)
    {
        Dictionary<string, string> metadata = new()
        {
            ["github:repo"] = "Octocat/Hello",
            ["github:type"] = type,
            ["github:number"] = number.ToString(),
        };
        foreach ((string key, string value) in extra)
            metadata["github:" + key] = value;
        return metadata;
    }

    [Fact]
    public void For_PullRequest_StandsForItsNodeAndLinksWhatItCloses()
    {
        DocumentLinks links = GitHubLinks.For(Doc, Record("pull_request", 7, ("closes", "3,4")))!;

        links.DocumentId.Should().Be(Doc);
        links.NodeId.Should().Be("gh:octocat/hello#7");
        links.Facts.Should().Contain(new LinkFact("gh:octocat/hello#7", LinkFactField.Name, "octocat/hello#7"));
        links.Facts.Should().Contain(new LinkFact("gh:octocat/hello#7", LinkFactField.Kind, "pull_request"));
        links.Links.Select(l => (l.Type, l.FromId, l.ToId)).Should().Equal(
            ("closes", "gh:octocat/hello#7", "gh:octocat/hello#3"),
            ("closes", "gh:octocat/hello#7", "gh:octocat/hello#4"));
    }

    [Fact]
    public void For_SubIssue_LinksToItsParent()
    {
        DocumentLinks links = GitHubLinks.For(Doc, Record("issue", 12, ("parent", "10"), ("children", "13")))!;

        // The child asserts the link; the parent's own list would only repeat it.
        links.Links.Should().ContainSingle()
            .Which.Should().Be(new LinkAssertion("parent|gh:octocat/hello#12|gh:octocat/hello#10",
                "gh:octocat/hello#12", "gh:octocat/hello#10", "parent"));
    }

    [Fact]
    public void For_Mentions_AreNotLinks()
    {
        DocumentLinks links = GitHubLinks.For(Doc, Record("issue", 5, ("references", "9,11")))!;

        links.Links.Should().BeEmpty();
    }

    [Fact]
    public void For_SameLinkFromTwoDocuments_GetsTheSameEdgeId()
    {
        DocumentLinks a = GitHubLinks.For(Doc, Record("pull_request", 7, ("closes", "3")))!;
        DocumentLinks b = GitHubLinks.For("other", Record("pull_request", 7, ("closes", "3")))!;

        a.Links[0].EdgeId.Should().Be(b.Links[0].EdgeId);
    }

    [Theory]
    [InlineData("github:repo")]
    [InlineData("github:number")]
    public void For_RecordMissingWhatNamesIt_IsNull(string missing)
    {
        Dictionary<string, string> metadata = Record("issue", 5, ("parent", "3"));
        metadata.Remove(missing);

        GitHubLinks.For(Doc, metadata).Should().BeNull();
    }

    [Fact]
    public void For_DocumentThatIsNotGitHub_IsNull() =>
        GitHubLinks.For(Doc, new Dictionary<string, string> { ["Title"] = "notes" }).Should().BeNull();

    [Fact]
    public void For_MalformedNumbers_AreSkippedNotGuessed()
    {
        DocumentLinks links = GitHubLinks.For(Doc, Record("pull_request", 7, ("closes", "3,x,-1,0,,7"), ("parent", "y")))!;

        // 7 is the record itself.
        links.Links.Select(l => l.ToId).Should().Equal("gh:octocat/hello#3");
    }

    [Fact]
    public void For_IssueClaimingToClose_AssertsNothing()
    {
        // Only a pull request closes; an issue's metadata saying so is not trusted.
        GitHubLinks.For(Doc, Record("issue", 5, ("closes", "3")))!.Links.Should().BeEmpty();
    }

    [Fact]
    public void MayHaveLinks_IsTrueOnlyForGitHubMetadata()
    {
        GitHubLinks.MayHaveLinks(Record("issue", 5)).Should().BeTrue();
        GitHubLinks.MayHaveLinks(new Dictionary<string, string> { ["Title"] = "x" }).Should().BeFalse();
    }
}
