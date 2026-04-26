using Connapse.Ingestion.Chunking;
using FluentAssertions;
using Markdig;
using Markdig.Syntax;

namespace Connapse.Ingestion.Tests.Chunking;

[Trait("Category", "Unit")]
public class MarkdownSectionWalkerTests
{
    private static MarkdownDocument Parse(string md)
    {
        MarkdownPipeline pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseYamlFrontMatter()
            .Build();
        return Markdown.Parse(md, pipeline);
    }

    [Fact]
    public void Walk_NoHeadings_ReturnsSinglePreambleSection()
    {
        string md = "Just a paragraph.\n\nAnother paragraph.";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(1);
        sections[0].HeaderPath.Should().BeEmpty();
        sections[0].SpanStart.Should().Be(0);
        sections[0].SpanEnd.Should().Be(md.Length);
    }

    [Fact]
    public void Walk_SimpleHeadings_ReturnsOneSectionPerHeading()
    {
        string md = "# A\n\nbody a\n\n# B\n\nbody b";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(2);
        sections[0].HeaderPath.Should().Be("A");
        sections[1].HeaderPath.Should().Be("B");
    }

    [Fact]
    public void Walk_NestedHeadings_BuildsBreadcrumbPath()
    {
        string md = "# H1\n\n## H2\n\n### H3\n\nbody";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(3);
        sections[2].HeaderPath.Should().Be("H1 > H2 > H3");
        sections[2].LevelMap["H1"].Should().Be("H1");
        sections[2].LevelMap["H2"].Should().Be("H2");
        sections[2].LevelMap["H3"].Should().Be("H3");
    }

    [Fact]
    public void Walk_LevelSkip_PopsCorrectly()
    {
        // H1 → H3 → H2 should drop H3 entirely when H2 reappears.
        string md = "# A\n\n### C\n\n## B\n\nbody";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(3);
        sections[0].HeaderPath.Should().Be("A");
        sections[1].HeaderPath.Should().Be("A > C");
        sections[2].HeaderPath.Should().Be("A > B");
        sections[2].LevelMap.Should().NotContainKey("H3");
    }

    [Fact]
    public void Walk_FencedCodeBlockWithHashes_NotTreatedAsHeadings()
    {
        // The `# Heading` inside the code fence is body text, not a heading.
        string md = "# Real\n\n```\n# Not a heading\n```\n\n# Another";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(2);
        sections[0].HeaderPath.Should().Be("Real");
        sections[1].HeaderPath.Should().Be("Another");
    }

    [Fact]
    public void Walk_SetextHeadings_ParsedAsHeadings()
    {
        string md = "Title\n=====\n\nbody\n\nSub\n---\n\nmore";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(2);
        sections[0].HeaderPath.Should().Be("Title");
        sections[1].HeaderPath.Should().Be("Title > Sub");
    }

    [Fact]
    public void Walk_HasMarkdownStructure_TrueWhenHeadingsExist()
    {
        string md = "# A\n\nbody";
        MarkdownSectionWalker.HasMarkdownStructure(Parse(md)).Should().BeTrue();
    }

    [Fact]
    public void Walk_HasMarkdownStructure_TrueWhenFencedCodeExists()
    {
        string md = "Just text.\n\n```\ncode\n```\n";
        MarkdownSectionWalker.HasMarkdownStructure(Parse(md)).Should().BeTrue();
    }

    [Fact]
    public void Walk_HasMarkdownStructure_FalseForPlainProse()
    {
        string md = "Just a paragraph.\n\nAnother paragraph.";
        MarkdownSectionWalker.HasMarkdownStructure(Parse(md)).Should().BeFalse();
    }

    [Fact]
    public void Walk_FormattedHeading_ExtractsRawText()
    {
        // Critical regression: previously returned "Markdig.Syntax.Inlines.EmphasisInline"
        // instead of "Bold Header" because Inline.ToString() returns the type name for
        // container inlines.
        string md = "# **Bold Header**\n\nbody";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(1);
        sections[0].HeaderPath.Should().Be("Bold Header");
    }

    [Fact]
    public void Walk_HeadingWithLink_ExtractsLinkText()
    {
        string md = "# See [the docs](https://example.com)\n\nbody";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(1);
        sections[0].HeaderPath.Should().Be("See the docs");
    }

    [Fact]
    public void Walk_HeadingWithMixedInlines_ExtractsAllText()
    {
        string md = "# *em* and **bold** and `code`\n\nbody";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(1);
        sections[0].HeaderPath.Should().Be("em and bold and code");
    }

    [Fact]
    public void Walk_PreambleThenHeading_EmitsBothSections()
    {
        string md = "intro paragraph\n\n# A\n\nbody a";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(2);
        sections[0].HeaderPath.Should().BeEmpty();
        sections[0].Depth.Should().Be(0);
        sections[1].HeaderPath.Should().Be("A");
    }

    [Fact]
    public void Walk_EmptyContent_ReturnsEmptyList()
    {
        var sections = MarkdownSectionWalker.Walk("", Parse(""));
        sections.Should().BeEmpty();
    }

    [Fact]
    public void Walk_ConsecutiveHeadingsNoBody_EmitsZeroLengthSections()
    {
        string md = "# A\n\n# B\n\n# C";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(3);
        sections.Should().AllSatisfy(s => s.SpanEnd.Should().BeGreaterThanOrEqualTo(s.SpanStart));
        sections[2].SpanEnd.Should().Be(sections[2].SpanStart);
    }
}
