using System.Text;
using Connapse.Ingestion.Parsers;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Parsers;

[Trait("Category", "Unit")]
public class HtmlParserTests
{
    private readonly HtmlParser _parser = new();

    private Task<Core.Interfaces.ParsedDocument> ParseAsync(string html, string fileName = "page.html") =>
        ParseAsync(Encoding.UTF8.GetBytes(html), fileName);

    private async Task<Core.Interfaces.ParsedDocument> ParseAsync(byte[] bytes, string fileName = "page.html")
    {
        using var stream = new MemoryStream(bytes);
        return await _parser.ParseAsync(stream, fileName);
    }

    /// <summary>A news-style page: navigation, a sidebar and a footer around a long article.</summary>
    private static string ArticlePage()
    {
        var paragraphs = string.Concat(Enumerable.Range(1, 8).Select(i =>
            $"<p>Paragraph {i} of the harbour report. The tide tables for the northern channel changed this " +
            "season, and the pilots who guide container ships through it now board two hours earlier. " +
            "Dredging finished in March, which deepened the approach by a metre and a half.</p>"));
        return $$"""
            <!DOCTYPE html>
            <html><head><title>Harbour pilots adjust to new tides</title>
            <script>var tracking = "do-not-index-script";</script>
            <style>.nav { color: red; }</style></head>
            <body>
              <nav><ul><li><a href="/">Home</a></li><li><a href="/sport">Sport</a></li><li><a href="/weather">Weather</a></li></ul></nav>
              <aside class="sidebar"><h3>Most read</h3><ul><li><a href="/x">Celebrity chef opens bakery</a></li></ul></aside>
              <article>
                <h1>Harbour pilots adjust to new tides</h1>
                {{paragraphs}}
                <h2>What changes for ferries</h2>
                <p>Ferries keep their timetable. <a href="https://example.com/timetable">The published timetable</a> stays valid until autumn, the port authority said.</p>
                <img src="chart.png" alt="tide chart">
              </article>
              <footer class="site-footer"><p>Subscribe to our newsletter. Copyright 2026 Example News.</p></footer>
            </body></html>
            """;
    }

    [Fact]
    public void SupportedExtensions_AreHtmlAndHtm()
    {
        _parser.SupportedExtensions.Should().BeEquivalentTo([".html", ".htm"]);
    }

    [Fact]
    public async Task ParseAsync_ArticlePage_KeepsTheArticleAndDropsTheChrome()
    {
        var result = await ParseAsync(ArticlePage());

        result.Metadata["ContentExtraction"].Should().Be("Article");
        result.Content.Should().Contain("Paragraph 8 of the harbour report");
        result.Content.Should().Contain("## What changes for ferries");
        result.Content.Should().NotContain("Celebrity chef");
        result.Content.Should().NotContain("Subscribe to our newsletter");
        result.Content.Should().NotContain("do-not-index-script");
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_ArticlePage_StartsWithTheTitle()
    {
        var result = await ParseAsync(ArticlePage());

        result.Metadata["Title"].Should().Be("Harbour pilots adjust to new tides");
        result.Content.Should().StartWith("# Harbour pilots adjust to new tides");
    }

    [Fact]
    public async Task ParseAsync_Links_KeepTheirTextButNotTheirTargets()
    {
        var result = await ParseAsync(ArticlePage());

        result.Content.Should().Contain("The published timetable stays valid");
        result.Content.Should().NotContain("example.com");
    }

    [Fact]
    public async Task ParseAsync_Images_AreDropped()
    {
        var result = await ParseAsync(ArticlePage());

        result.Content.Should().NotContain("chart.png");
        result.Content.Should().NotContain("![");
    }

    [Fact]
    public async Task ParseAsync_ShortPageWithNoArticle_IsConvertedWhole()
    {
        var result = await ParseAsync("""
            <html><head><title>Door codes</title><script>secret()</script></head>
            <body><h1>Door codes</h1><ul><li>Loading dock: 4471</li><li>Server room: 9902</li></ul></body></html>
            """);

        result.Metadata["ContentExtraction"].Should().Be("FullPage");
        result.Content.Should().Contain("# Door codes");
        result.Content.Should().Contain("- Loading dock: 4471");
        result.Content.Should().Contain("- Server room: 9902");
        result.Content.Should().NotContain("secret()");
    }

    [Fact]
    public async Task ParseAsync_Table_BecomesAMarkdownTableWithItsHeader()
    {
        var result = await ParseAsync("""
            <html><body><h1>Rates</h1>
            <table><thead><tr><th>Region</th><th>Rate</th></tr></thead>
            <tbody><tr><td>North</td><td>4.5%</td></tr><tr><td>South</td><td>3.9%</td></tr></tbody></table>
            </body></html>
            """);

        result.Content.Should().MatchRegex(@"\|\s*Region\s*\|\s*Rate\s*\|");
        result.Content.Should().MatchRegex(@"\|\s*North\s*\|\s*4\.5%\s*\|");
        result.Content.Should().MatchRegex(@"\|\s*-+\s*\|");
    }

    [Fact]
    public async Task ParseAsync_TextThatLooksLikeAHeading_IsEscaped()
    {
        var result = await ParseAsync("<html><body><p># not a heading</p><p>Body text.</p></body></html>");

        result.Content.Split('\n').Should().NotContain(l => l.StartsWith("# not a heading"));
        result.Content.Should().Contain("not a heading");
    }

    [Fact]
    public async Task ParseAsync_DeclaredWindows1252_DecodesAccents()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bytes = Encoding.GetEncoding(1252).GetBytes(
            "<html><head><meta charset=\"windows-1252\"><title>Menu</title></head><body><p>Café crème brûlée</p></body></html>");

        var result = await ParseAsync(bytes);

        result.Content.Should().Contain("Café crème brûlée");
        result.Metadata["Encoding"].Should().Be("windows-1252");
    }

    [Fact]
    public async Task ParseAsync_Utf8WithoutDeclaration_DecodesAccents()
    {
        var result = await ParseAsync("<html><body><p>Naïve façade — 東京</p></body></html>");

        result.Content.Should().Contain("Naïve façade — 東京");
    }

    [Fact]
    public async Task ParseAsync_DeeplyNestedPage_IsReadAsPlainTextWithoutCrashing()
    {
        // A recursion this deep in Readability or the Markdown converter would overflow the stack,
        // which ends the worker process rather than failing one document.
        const int depth = 100_000;
        var html = new StringBuilder("<html><body>");
        for (int i = 0; i < depth; i++)
            html.Append("<div>");
        html.Append("<p>Buried sentence.</p>");
        for (int i = 0; i < depth; i++)
            html.Append("</div>");
        html.Append("</body></html>");

        var result = await ParseAsync(html.ToString());

        result.Metadata["ContentExtraction"].Should().Be("PlainText");
        result.Content.Should().Contain("Buried sentence.");
        result.Warnings.Should().ContainSingle().Which.Should().Contain("levels deep");
    }

    [Fact]
    public void NestsDeeperThan_StrayEndTags_DoNotHideDepth()
    {
        // "</span>" closes nothing a parser has open, so each "<div>" still nests one level deeper.
        string html = string.Concat(Enumerable.Repeat("<div></span>", 50));

        HtmlMarkup.NestsDeeperThan(html, 40).Should().BeTrue();
    }

    [Theory]
    [InlineData("<div/>")]
    [InlineData("<noscript><div>")]
    [InlineData("<template><div>")]
    public void NestsDeeperThan_MarkupAParserNests_CountsAsDepth(string repeated)
    {
        // "/>" on an HTML element is ignored, and noscript and template content is parsed as
        // elements, so each repetition really is one level deeper.
        HtmlMarkup.NestsDeeperThan(string.Concat(Enumerable.Repeat(repeated, 50)), 40).Should().BeTrue();
    }

    [Fact]
    public void NestsDeeperThan_SelfClosingSvgShapes_AreNotDepth()
    {
        string html = "<svg>" + string.Concat(Enumerable.Repeat("<path d=\"M0 0\"/>", 1000)) + "</svg><p>ok</p>";

        HtmlMarkup.NestsDeeperThan(html, 40).Should().BeFalse();
    }

    [Fact]
    public async Task ParseAsync_ThousandsOfSelfClosingDivs_AreReadAsPlainText()
    {
        var result = await ParseAsync(string.Concat(Enumerable.Repeat("<div/>", 100_000)) + "<p>Still here.</p>");

        result.Metadata["ContentExtraction"].Should().Be("PlainText");
        result.Content.Should().Contain("Still here.");
    }

    [Fact]
    public async Task ParseAsync_CommentedOutCharset_DoesNotOutrankTheRealOne()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            "<html><head><!-- <meta charset=\"windows-1252\"> --><meta charset=\"utf-8\"></head><body><p>Café</p></body></html>");

        var result = await ParseAsync(bytes);

        result.Metadata["Encoding"].Should().Be("utf-8");
        result.Content.Should().Contain("Café");
    }

    [Fact]
    public void NestsDeeperThan_UnclosedParagraphsAndListItems_AreNotDepth()
    {
        // Hand-written HTML leaves these open; the next one closes the last.
        string html = "<ul>" + string.Concat(Enumerable.Repeat("<li><p>item", 1000)) + "</ul>";

        HtmlMarkup.NestsDeeperThan(html, 40).Should().BeFalse();
    }

    [Fact]
    public void NestsDeeperThan_TagsInsideScripts_AreNotMarkup()
    {
        string html = "<script>" + string.Concat(Enumerable.Repeat("<div>", 1000)) + "</script><p>ok</p>";

        HtmlMarkup.NestsDeeperThan(html, 40).Should().BeFalse();
    }

    [Fact]
    public void PlainText_DropsScriptsAndDecodesEntities()
    {
        string text = HtmlMarkup.PlainText("<head><title>T</title><script>alert(1)</script></head><div>Fish &amp; chips</div><div># 1 seller</div>");

        text.Should().Be("Fish & chips\n\n\\# 1 seller");
    }

    [Fact]
    public async Task ParseAsync_PageWithNoText_WarnsAndReturnsEmpty()
    {
        var result = await ParseAsync("<html><head><script>x()</script></head><body><img src=a.png></body></html>");

        result.Content.Should().BeEmpty();
        result.Warnings.Should().Contain("Document contains no readable text content");
    }

    [Fact]
    public async Task ParseAsync_Fragment_WithoutHtmlOrBody_IsParsed()
    {
        var result = await ParseAsync("<h2>Notes</h2><p>Just a fragment.</p>", "fragment.htm");

        result.Content.Should().Contain("## Notes");
        result.Content.Should().Contain("Just a fragment.");
    }
}
