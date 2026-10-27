using System.Text.Json;
using Connapse.Core;
using Connapse.Storage.Connectors;
using Connapse.Storage.Connectors.Atlassian;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>The links a Jira issue asserts in the link layer (CON-34).</summary>
[Trait("Category", "Unit")]
public sealed class JiraLinksTests
{
    private const string Doc = "0b8a4f3e-0000-0000-0000-000000000002";
    private const string Site = "site-1";

    private static DocumentLinks Links(string issueJson, string? epicLinkField = null) =>
        JiraLinks.For(Doc, JiraLinks.Metadata(Site, JsonDocument.Parse(issueJson).RootElement, epicLinkField))!;

    private static IEnumerable<(string Type, string From, string To)> Edges(DocumentLinks links) =>
        links.Links.Select(l => (l.Type, l.FromId, l.ToId));

    [Fact]
    public void For_IssueLinks_PointTheWayTheOutwardPhraseReads()
    {
        DocumentLinks links = Links("""
            {"key":"FLINK-5","fields":{"issuelinks":[
              {"type":{"name":"Blocks","inward":"is blocked by","outward":"blocks"},"outwardIssue":{"key":"FLINK-9"}},
              {"type":{"name":"Blocks","inward":"is blocked by","outward":"blocks"},"inwardIssue":{"key":"FLINK-3"}},
              {"type":{"name":"Relates","inward":"relates to","outward":"relates to"},"outwardIssue":{"key":"OTHER-1"}},
              {"type":{"name":"Duplicate","inward":"is duplicated by","outward":"duplicates"},"inwardIssue":{"key":"FLINK-7"}}
            ]}}
            """);

        links.NodeId.Should().Be("jira:site-1:FLINK-5");
        links.Facts.Should().Contain(new LinkFact("jira:site-1:FLINK-5", LinkFactField.Name, "FLINK-5"));
        Edges(links).Should().Equal(
            ("blocks", "jira:site-1:FLINK-5", "jira:site-1:FLINK-9"),
            ("blocks", "jira:site-1:FLINK-3", "jira:site-1:FLINK-5"),
            ("relates", "jira:site-1:FLINK-5", "jira:site-1:OTHER-1"),
            ("duplicate", "jira:site-1:FLINK-7", "jira:site-1:FLINK-5"));
    }

    [Fact]
    public void For_BothEndsOfALink_AssertOneEdge()
    {
        LinkAssertion fromBlocker = Links("""
            {"key":"FLINK-5","fields":{"issuelinks":[{"type":{"name":"Blocks"},"outwardIssue":{"key":"FLINK-9"}}]}}
            """).Links.Single();
        LinkAssertion fromBlocked = Links("""
            {"key":"FLINK-9","fields":{"issuelinks":[{"type":{"name":"Blocks"},"inwardIssue":{"key":"FLINK-5"}}]}}
            """).Links.Single();

        fromBlocked.Should().Be(fromBlocker);
    }

    [Fact]
    public void For_CloudParent_EpicOrSubtaskParentByItsType()
    {
        DocumentLinks story = Links("""
            {"key":"FLINK-5","fields":{"issuetype":{"subtask":false},
              "parent":{"key":"FLINK-1","fields":{"issuetype":{"name":"Feature","hierarchyLevel":1}}}}}
            """);
        DocumentLinks subtask = Links("""
            {"key":"FLINK-6","fields":{"issuetype":{"subtask":true},
              "parent":{"key":"FLINK-5","fields":{"issuetype":{"name":"Story","hierarchyLevel":0}}}}}
            """);

        Edges(story).Should().Equal(("epic", "jira:site-1:FLINK-5", "jira:site-1:FLINK-1"));
        Edges(subtask).Should().Equal(("parent", "jira:site-1:FLINK-6", "jira:site-1:FLINK-5"));
    }

    [Fact]
    public void For_DataCenterEpicLinkField_LinksTheEpic()
    {
        DocumentLinks links = Links("""{"key":"FLINK-5","fields":{"customfield_1":"FLINK-1"}}""", "customfield_1");

        Edges(links).Should().Equal(("epic", "jira:site-1:FLINK-5", "jira:site-1:FLINK-1"));
    }

    [Fact]
    public void For_FixVersions_LinkToAVersionNodeOfTheProject()
    {
        DocumentLinks links = Links("""{"key":"FLINK-5","fields":{"fixVersions":[{"name":"1.17.0"},{"name":"1.16.2"}]}}""");

        Edges(links).Should().Equal(
            ("fix-version", "jira:site-1:FLINK-5", "jiraversion:site-1:FLINK/1.17.0"),
            ("fix-version", "jira:site-1:FLINK-5", "jiraversion:site-1:FLINK/1.16.2"));
    }

    [Fact]
    public void For_DescriptionConfluenceUrls_LinkThePages()
    {
        // Cloud's document tree (a link mark's href) and Data Center's wiki markup name pages the same ways.
        DocumentLinks cloud = Links("""
            {"key":"FLINK-5","fields":{"description":{"type":"doc","content":[{"type":"text","text":"design",
              "marks":[{"type":"link","attrs":{"href":"https://acme.atlassian.net/wiki/spaces/ENG/pages/123/Design"}}]}]}}}
            """);
        DocumentLinks dataCenter = Links("""
            {"key":"FLINK-5","fields":{"description":"See [FLIP|https://cwiki.apache.org/confluence/display/FLINK/FLIP-1+Fine+Grained] and https://cwiki.apache.org/confluence/pages/viewpage.action?pageId=456"}}
            """);

        Edges(cloud).Should().Equal(("confluence-page", "jira:site-1:FLINK-5", "confluence:site-1:123"));
        Edges(dataCenter).Should().BeEquivalentTo(new[]
        {
            ("confluence-page", "jira:site-1:FLINK-5", "confluence:site-1:FLINK/FLIP-1 Fine Grained"),
            ("confluence-page", "jira:site-1:FLINK-5", "confluence:site-1:456"),
        });
    }

    [Fact]
    public void Metadata_IssueWithNoLinks_ReportsEveryKeyEmpty()
    {
        // Empty rather than absent, so a reindex replaces links the issue has since dropped.
        Dictionary<string, string> metadata = JiraLinks.Metadata(Site, JsonDocument.Parse("""{"key":"FLINK-5","fields":{}}""").RootElement);

        metadata.Should().ContainKeys("jira:links", "jira:parent", "jira:parentKind", "jira:fixVersions", "jira:pages");
        metadata.Where(p => p.Key is not ("jira:site" or "jira:issueKey")).Should().OnlyContain(p => p.Value == "");
        JiraLinks.For(Doc, metadata)!.Links.Should().BeEmpty();
    }

    [Fact]
    public void For_ListingMetadataOnly_IsNotAJiraIssue()
    {
        // The listing carries the key but not the site; links come from the read.
        JiraLinks.For(Doc, new Dictionary<string, string> { ["jira:issueKey"] = "FLINK-5" }).Should().BeNull();
    }

    [Fact]
    public void RecordLinks_DispatchesToJira()
    {
        Dictionary<string, string> metadata = JiraLinks.Metadata(Site, JsonDocument.Parse("""{"key":"FLINK-5","fields":{}}""").RootElement);

        RecordLinks.MayHaveLinks(metadata).Should().BeTrue();
        RecordLinks.For(Doc, metadata)!.NodeId.Should().Be("jira:site-1:FLINK-5");
    }
}
