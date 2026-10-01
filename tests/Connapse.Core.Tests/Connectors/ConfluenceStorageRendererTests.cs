using Connapse.Storage.Connectors.Atlassian;
using FluentAssertions;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public class ConfluenceStorageRendererTests
{
    private static readonly IReadOnlyDictionary<string, string> NoUsers = new Dictionary<string, string>();

    private static ConfluenceRenderOutput Render(
        string body,
        IReadOnlyList<ConfluenceComment>? comments = null,
        IReadOnlyDictionary<string, string>? users = null,
        string[]? breadcrumb = null) =>
        ConfluenceStorageRenderer.Render(new ConfluenceRenderInput(
            breadcrumb ?? [], body, comments ?? [], users ?? NoUsers));

    [Fact]
    public void Render_CodeMacro_EmitsFencedBlockWithLanguage()
    {
        string md = Render("""
            <ac:structured-macro ac:name="code"><ac:parameter ac:name="language">csharp</ac:parameter><ac:plain-text-body><![CDATA[if (a < b && c > d) {
              Run();
            }]]></ac:plain-text-body></ac:structured-macro>
            """).Markdown;

        md.Should().Be("```csharp\nif (a < b && c > d) {\n  Run();\n}\n```\n");
    }

    [Fact]
    public void Render_InfoPanel_EmitsLabelledQuote()
    {
        string md = Render("""
            <ac:structured-macro ac:name="note"><ac:rich-text-body><p>Back up first.</p></ac:rich-text-body></ac:structured-macro>
            <ac:structured-macro ac:name="tip"><ac:rich-text-body><p>Use the wizard.</p></ac:rich-text-body></ac:structured-macro>
            """).Markdown;

        md.Should().Be("> Note: Back up first.\n\n> Tip: Use the wizard.\n");
    }

    [Fact]
    public void Render_Expand_InlinesBody()
    {
        string md = Render("""
            <ac:structured-macro ac:name="expand"><ac:parameter ac:name="title">More detail</ac:parameter><ac:rich-text-body><p>Hidden text.</p></ac:rich-text-body></ac:structured-macro>
            """).Markdown;

        md.Should().Be("**More detail**\n\nHidden text.\n");
    }

    [Fact]
    public void Render_Status_EmitsText()
    {
        string md = Render("""
            <p>State: <ac:structured-macro ac:name="status"><ac:parameter ac:name="colour">Green</ac:parameter><ac:parameter ac:name="title">DONE</ac:parameter></ac:structured-macro></p>
            """).Markdown;

        md.Should().Be("State: DONE\n");
    }

    [Fact]
    public void Render_JiraMacro_EmitsKey()
    {
        string md = Render("""
            <p>See <ac:structured-macro ac:name="jira"><ac:parameter ac:name="server">Jira</ac:parameter><ac:parameter ac:name="key">ENG-123</ac:parameter></ac:structured-macro> now.</p>
            """).Markdown;

        md.Should().Be("See ENG-123 now.\n");
    }

    [Theory]
    [InlineData("include")]
    [InlineData("excerpt-include")]
    public void Render_IncludeMacro_EmitsPlaceholderNotContent(string macro)
    {
        string md = Render($"""
            <ac:structured-macro ac:name="{macro}"><ac:parameter ac:name=""><ac:link><ri:page ri:content-title="Shared Intro" /></ac:link></ac:parameter></ac:structured-macro>
            """).Markdown;

        md.Should().Be("[includes: Shared Intro]\n");
    }

    [Theory]
    [InlineData("toc")]
    [InlineData("children")]
    [InlineData("pagetree")]
    public void Render_Toc_Dropped(string macro)
    {
        string md = Render($"""
            <p>Before</p><ac:structured-macro ac:name="{macro}" /><p>After</p>
            """).Markdown;

        md.Should().Be("Before\n\nAfter\n");
    }

    [Fact]
    public void Render_UserMention_ResolvesDisplayName()
    {
        var users = new Dictionary<string, string> { ["557058:abc"] = "Ada Lovelace" };

        string md = Render("""
            <p>Ping <ac:link><ri:user ri:account-id="557058:abc" /></ac:link> please.</p>
            """, users: users).Markdown;

        md.Should().Be("Ping Ada Lovelace please.\n");
    }

    [Fact]
    public void Render_UserMentionUnknown_EmitsGenericUser()
    {
        string md = Render("""
            <p>Ping <ac:link><ri:user ri:account-id="557058:zzz" /></ac:link> please.</p>
            """).Markdown;

        md.Should().Be("Ping a user please.\n");
        md.Should().NotContain("557058");
    }

    [Fact]
    public void Render_PageLink_EmitsTitleAndRecordsLink()
    {
        ConfluenceRenderOutput output = Render("""
            <p><ac:link><ri:page ri:content-title="Laptop setup" ri:space-key="ENG" /></ac:link>
            and <ac:link><ri:page ri:content-title="VPN" /><ac:plain-text-link-body><![CDATA[the VPN guide]]></ac:plain-text-link-body></ac:link>
            and again <ac:link><ri:page ri:content-title="Laptop setup" /></ac:link></p>
            """);

        output.Markdown.Should().Be("Laptop setup and the VPN guide and again Laptop setup\n");
        output.LinkedPageTitles.Should().Equal("Laptop setup", "VPN");
    }

    [Fact]
    public void Render_Attachment_EmitsPlaceholder()
    {
        string md = Render("""
            <p><ac:image><ri:attachment ri:filename="diagram.png" /></ac:image></p>
            <p>Read <ac:link><ri:attachment ri:filename="spec.pdf" /></ac:link></p>
            """).Markdown;

        md.Should().Be("[attachment: diagram.png]\n\nRead [attachment: spec.pdf]\n");
    }

    [Fact]
    public void Render_TaskList_EmitsCheckboxes()
    {
        string md = Render("""
            <ac:task-list>
              <ac:task><ac:task-id>1</ac:task-id><ac:task-status>complete</ac:task-status><ac:task-body>Order laptop</ac:task-body></ac:task>
              <ac:task><ac:task-id>2</ac:task-id><ac:task-status>incomplete</ac:task-status><ac:task-body>Install <strong>IDE</strong></ac:task-body></ac:task>
            </ac:task-list>
            """).Markdown;

        md.Should().Be("- [x] Order laptop\n- [ ] Install **IDE**\n");
    }

    [Fact]
    public void Render_UnknownMacroWithBody_EmitsBody()
    {
        string md = Render("""
            <ac:structured-macro ac:name="fancy-box"><ac:parameter ac:name="color">red</ac:parameter><ac:rich-text-body><p>Inside the box.</p></ac:rich-text-body></ac:structured-macro>
            <ac:structured-macro ac:name="raw"><ac:plain-text-body><![CDATA[plain body]]></ac:plain-text-body></ac:structured-macro>
            """).Markdown;

        md.Should().Be("Inside the box.\n\nplain body\n");
    }

    [Fact]
    public void Render_UnknownMacroNoBody_Dropped()
    {
        string md = Render("""
            <p>Keep</p><ac:structured-macro ac:name="mystery"><ac:parameter ac:name="x">leaky</ac:parameter></ac:structured-macro>
            """).Markdown;

        md.Should().Be("Keep\n");
    }

    [Fact]
    public void Render_Table_EmitsMarkdownTable()
    {
        string md = Render("""
            <table><tbody>
              <tr><th>Name</th><th>Role</th></tr>
              <tr><td>Ada</td><td><p>Eng | Lead</p></td></tr>
              <tr><td>Bob</td></tr>
            </tbody></table>
            """).Markdown;

        md.Should().Be("| Name | Role |\n| --- | --- |\n| Ada | Eng \\| Lead |\n| Bob |  |\n");
    }

    [Fact]
    public void Render_Headings_ShiftedUnderBreadcrumb()
    {
        string md = Render(
            "<h1>One</h1><p>a</p><h5>Five</h5><h6>Six</h6>",
            breadcrumb: ["Engineering", "Onboarding", "Laptop setup"]).Markdown;

        md.Should().Be("# Engineering > Onboarding > Laptop setup\n\n## One\n\na\n\n###### Five\n\n###### Six\n");
    }

    [Fact]
    public void Render_BreadcrumbWithGreaterThan_Escaped()
    {
        string md = Render("<p>x</p>", breadcrumb: ["Space", "A > B", "Page"]).Markdown;

        md.Should().StartWith("# Space > A › B > Page\n");
    }

    [Fact]
    public void Render_Comments_AppendedWithSeparators()
    {
        var comments = new[]
        {
            new ConfluenceComment("Ada Lovelace", new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero), "<p>Looks good.</p>"),
            new ConfluenceComment("Bob", new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), "<h1>Heads up</h1><p>Check <em>this</em>.</p>"),
        };

        string md = Render("<p>Body</p>", comments).Markdown;

        md.Should().Be(
            "Body\n\n## Comments\n\n" +
            "--- Comment by Ada Lovelace, 2026-09-30 ---\nLooks good.\n\n" +
            "--- Comment by Bob, 2026-10-01 ---\n**Heads up**\n\nCheck *this*.\n");
    }

    [Fact]
    public void Render_MalformedXhtml_FallsBackToText()
    {
        ConfluenceRenderOutput output = Render("<p>Hello <strong>world<ac:structured-macro ac:name=\"code\"><div></p>&bogus; <<>");

        output.Markdown.Should().Contain("Hello").And.Contain("world");
    }

    [Fact]
    public void Render_HtmlEntities_AreDecoded()
    {
        string md = Render("<p>A&nbsp;B &mdash; it&rsquo;s &amp; done</p>").Markdown;

        md.Should().Be("A B — it’s & done\n");
    }

    [Fact]
    public void Render_Lists_NestAndNumber()
    {
        string md = Render("<ul><li>One<ul><li>Inner</li></ul></li><li>Two</li></ul><ol><li>First</li><li>Second</li></ol>").Markdown;

        md.Should().Be("- One\n  - Inner\n- Two\n\n1. First\n2. Second\n");
    }

    [Fact]
    public void Render_IsDeterministicWithoutTrailingWhitespace()
    {
        const string body = "<p>a  <br />b</p><p>  </p><h2>T </h2>";

        string first = Render(body).Markdown;

        first.Should().Be(Render(body).Markdown);
        first.Split('\n').Should().OnlyContain(l => l == l.TrimEnd());
    }

    [Fact]
    public void MentionedAccountIds_CollectsDistinctIds()
    {
        IReadOnlySet<string> ids = ConfluenceStorageRenderer.MentionedAccountIds("""
            <p><ac:link><ri:user ri:account-id="a:1" /></ac:link> <ac:link><ri:user ri:account-id="b:2"/></ac:link>
            <ac:link><ri:user ri:account-id="a:1" /></ac:link></p>
            """);

        ids.Should().BeEquivalentTo("a:1", "b:2");
    }
}
