using Connapse.Core;
using Connapse.Storage.Connectors;
using Connapse.Storage.Connectors.Atlassian;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>The links a Confluence page asserts in the link layer (CON-34).</summary>
[Trait("Category", "Unit")]
public sealed class ConfluenceLinksTests
{
    private const string Doc = "0b8a4f3e-0000-0000-0000-000000000003";
    private const string Site = "site-1";

    private static DocumentLinks Links(string body, string? parent = null, params string[] labels) =>
        ConfluenceLinks.For(Doc, ConfluenceLinks.Metadata(Site, "flink", "100", "FLIP-1 Design", parent, labels, body))!;

    private static IEnumerable<(string Type, string To)> Edges(DocumentLinks links) =>
        links.Links.Select(l => (l.Type, l.ToId));

    [Fact]
    public void For_Page_StandsForItsIdAndItsTitle()
    {
        DocumentLinks links = Links("<p>x</p>");

        links.NodeId.Should().Be("confluence:site-1:100");
        links.AlsoStandsFor.Should().Equal("confluence:site-1:FLINK/FLIP-1 Design");
        links.Facts.Should().Contain(new LinkFact("confluence:site-1:100", LinkFactField.Name, "FLIP-1 Design"));
    }

    [Fact]
    public void For_ParentAndLabels_LinkTheTreeAndALabelNode()
    {
        DocumentLinks links = Links("<p>x</p>", parent: "42", "FLIP", "Accepted");

        Edges(links).Should().Equal(
            ("parent", "confluence:site-1:42"),
            ("labelled", "confluencelabel:site-1:flip"),
            ("labelled", "confluencelabel:site-1:accepted"));
    }

    [Fact]
    public void For_PageLinks_ByTitleInThisOrAnotherSpaceAndByUrl()
    {
        DocumentLinks links = Links("""
            <ac:link><ri:page ri:content-title="Roadmap &amp; Plans" /></ac:link>
            <ac:link><ri:page ri:space-key="OPS" ri:content-title="Runbook" /></ac:link>
            <a href="https://acme.atlassian.net/wiki/spaces/FLINK/pages/77/Other">other</a>
            <ac:link><ri:page ri:content-title="FLIP-1 Design" /></ac:link>
            """);

        Edges(links).Should().Equal(
            ("links-to", "confluence:site-1:FLINK/Roadmap & Plans"),
            ("links-to", "confluence:site-1:OPS/Runbook"),
            ("links-to", "confluence:site-1:77"));
    }

    [Fact]
    public void For_JiraMacrosAndBrowseUrls_LinkTheIssues()
    {
        DocumentLinks links = Links("""
            <ac:structured-macro ac:name="jira" ac:schema-version="1"><ac:parameter ac:name="server">ASF JIRA</ac:parameter><ac:parameter ac:name="key">FLINK-123</ac:parameter></ac:structured-macro>
            <a href="https://issues.apache.org/jira/browse/FLINK-456">FLINK-456</a>
            <ac:structured-macro ac:name="code"><ac:parameter ac:name="key">FLINK-999</ac:parameter></ac:structured-macro>
            <p>Mentioned in passing: FLINK-789.</p>
            """);

        Edges(links).Should().Equal(
            ("jira-issue", "jira:site-1:FLINK-123"),
            ("jira-issue", "jira:site-1:FLINK-456"));
    }

    [Fact]
    public void For_JiraIssueLinkingThePage_MeetsItAtItsTitleNode()
    {
        // Data Center's /display/ URLs name the page by title, which the page also stands for.
        Dictionary<string, string> issue = JiraLinks.Metadata(Site, System.Text.Json.JsonDocument.Parse("""
            {"key":"FLINK-5","fields":{"description":"https://cwiki.apache.org/confluence/display/FLINK/FLIP-1+Design"}}
            """).RootElement);

        JiraLinks.For("i", issue)!.Links.Single().ToId.Should().BeOneOf(Links("<p/>").AlsoStandsFor!);
    }

    [Fact]
    public void RecordLinks_DispatchesToConfluence()
    {
        Dictionary<string, string> metadata = ConfluenceLinks.Metadata(Site, "FLINK", "100", "T", null, [], "");

        RecordLinks.MayHaveLinks(metadata).Should().BeTrue();
        RecordLinks.For(Doc, metadata)!.NodeId.Should().Be("confluence:site-1:100");
    }
}
