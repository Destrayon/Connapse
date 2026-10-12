using System.Text;
using System.Text.Json;
using Connapse.Storage.Connectors.Atlassian;
using FluentAssertions;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public class AdfRendererTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4000 }).RootElement;

    private static string Doc(string content) => $$"""{"type":"doc","version":1,"content":[{{content}}]}""";

    private static string P(string text) =>
        $$"""{"type":"paragraph","content":[{"type":"text","text":"{{text}}"}]}""";

    private static string RenderDoc(string content, Func<string, string?>? names = null) =>
        AdfRenderer.Render(Parse(Doc(content)), names).Trim();

    [Fact]
    public void Render_Headings_Levels()
    {
        string md = RenderDoc("""
            {"type":"heading","attrs":{"level":1},"content":[{"type":"text","text":"One"}]},
            {"type":"heading","attrs":{"level":3},"content":[{"type":"text","text":"Three"}]},
            {"type":"heading","attrs":{"level":6},"content":[{"type":"text","text":"Six"}]},
            {"type":"paragraph","content":[{"type":"text","text":"Body"}]}
            """);

        md.Should().Be("# One\n\n### Three\n\n###### Six\n\nBody");
    }

    [Fact]
    public void Render_NestedLists()
    {
        string md = RenderDoc("""
            {"type":"bulletList","content":[
              {"type":"listItem","content":[
                {"type":"paragraph","content":[{"type":"text","text":"a"}]},
                {"type":"orderedList","attrs":{"order":3},"content":[
                  {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"b"}]}]},
                  {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"c"}]}]}]}]},
              {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"d"}]}]}]}
            """);

        md.Should().Be("- a\n  3. b\n  4. c\n- d");
    }

    [Fact]
    public void Render_Table_EscapesPipes()
    {
        string md = RenderDoc("""
            {"type":"table","content":[
              {"type":"tableRow","content":[
                {"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"Name"}]}]},
                {"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"Value"}]}]}]},
              {"type":"tableRow","content":[
                {"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"a|b"}]}]},
                {"type":"tableCell","content":[
                  {"type":"paragraph","content":[{"type":"text","text":"x"}]},
                  {"type":"paragraph","content":[{"type":"text","text":"y"}]}]}]},
              {"type":"tableRow","content":[
                {"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"only"}]}]}]}]}
            """);

        md.Should().Be("| Name | Value |\n| --- | --- |\n| a\\|b | x<br>y |\n| only |  |");
    }

    [Fact]
    public void Render_CodeBlock_LanguageAndLongerFence()
    {
        string md = RenderDoc("""
            {"type":"codeBlock","attrs":{"language":"md"},"content":[{"type":"text","text":"a ``` b\nc"}]},
            {"type":"codeBlock","content":[{"type":"text","text":"plain"}]}
            """);

        md.Should().Be("````md\na ``` b\nc\n````\n\n```\nplain\n```");
    }

    [Fact]
    public void Render_Panel_LabelledQuote()
    {
        var adf = Parse("""
            {"type":"doc","content":[{"type":"panel","attrs":{"panelType":"warning"},
            "content":[{"type":"paragraph","content":[{"type":"text","text":"Rotate keys"}]}]}]}
            """);

        AdfRenderer.Render(adf).Trim().Should().Be("> Warning: Rotate keys");
    }

    [Theory]
    [InlineData("info", "Info")]
    [InlineData("note", "Note")]
    [InlineData("success", "Success")]
    [InlineData("error", "Error")]
    [InlineData("custom", "Panel")]
    public void Render_Panel_TypeLabels(string type, string label)
    {
        RenderDoc($$"""{"type":"panel","attrs":{"panelType":"{{type}}"},"content":[{{P("x")}}]}""")
            .Should().Be($"> {label}: x");
    }

    [Fact]
    public void Render_Expand_TitleThenBody()
    {
        string md = RenderDoc($$"""
            {"type":"expand","attrs":{"title":"Details"},"content":[{{P("Hidden text")}},
              {"type":"nestedExpand","attrs":{"title":"Inner"},"content":[{{P("Deep")}}]}]}
            """);

        md.Should().Be("Details\n\nHidden text\n\nInner\n\nDeep");
    }

    [Fact]
    public void Render_Mention_DisplayText()
    {
        RenderDoc("""
            {"type":"paragraph","content":[{"type":"text","text":"cc "},
            {"type":"mention","attrs":{"id":"abc","text":"@Jane Doe"}},
            {"type":"text","text":" and "},{"type":"mention","attrs":{"id":"x","text":"@@Bob"}}]}
            """).Should().Be("cc @Jane Doe and @Bob");
    }

    [Fact]
    public void Render_InlineCard_Url()
    {
        RenderDoc("""
            {"type":"paragraph","content":[{"type":"text","text":"see "},
            {"type":"inlineCard","attrs":{"url":"https://example.com/a"}}]},
            {"type":"blockCard","attrs":{"url":"https://example.com/b"}}
            """).Should().Be("see https://example.com/a\n\nhttps://example.com/b");
    }

    [Fact]
    public void Render_StatusEmojiDate_Text()
    {
        RenderDoc("""
            {"type":"paragraph","content":[
              {"type":"status","attrs":{"text":"In Progress","color":"blue"}},{"type":"text","text":" "},
              {"type":"emoji","attrs":{"shortName":":smile:","text":"X"}},{"type":"text","text":" "},
              {"type":"emoji","attrs":{"shortName":":tada:"}},{"type":"text","text":" "},
              {"type":"date","attrs":{"timestamp":"1582152559000"}}]}
            """).Should().Be("In Progress X :tada: 2020-02-19");
    }

    [Fact]
    public void Render_Date_NumericTimestampAndGarbage()
    {
        RenderDoc("""{"type":"paragraph","content":[{"type":"date","attrs":{"timestamp":1582152559000}},{"type":"text","text":"|"},{"type":"date","attrs":{"timestamp":"junk"}},{"type":"date","attrs":{"timestamp":9e99}}]}""")
            .Should().Be("2020-02-19|");
    }

    [Fact]
    public void Render_Media_NamedWhenResolvable()
    {
        const string media = """
            {"type":"mediaSingle","content":[{"type":"media","attrs":{"type":"file","id":"uuid-1","collection":"c"}}]},
            {"type":"mediaSingle","content":[{"type":"media","attrs":{"type":"file","id":"uuid-2"}}]},
            {"type":"mediaGroup","content":[{"type":"media","attrs":{"id":"uuid-1","alt":"shot.png"}}]}
            """;

        RenderDoc(media, id => id == "uuid-1" ? "diagram.png" : null)
            .Should().Be("[attachment: diagram.png]\n\n[attachment: shot.png]");
        RenderDoc(media).Should().Be("[attachment: shot.png]");
    }

    [Fact]
    public void Render_TaskList_Checkboxes()
    {
        RenderDoc("""
            {"type":"taskList","attrs":{"localId":"1"},"content":[
              {"type":"taskItem","attrs":{"state":"DONE"},"content":[{"type":"text","text":"shipped"}]},
              {"type":"taskItem","attrs":{"state":"TODO"},"content":[{"type":"text","text":"review"}]}]},
            {"type":"decisionList","content":[
              {"type":"decisionItem","attrs":{"state":"DECIDED"},"content":[{"type":"text","text":"use ADF"}]}]}
            """).Should().Be("- [x] shipped\n- [ ] review\n\n- use ADF");
    }

    [Fact]
    public void Render_Marks_LinkOnlyHttp()
    {
        RenderDoc("""
            {"type":"paragraph","content":[
              {"type":"text","text":"bold","marks":[{"type":"strong"}]},{"type":"text","text":" "},
              {"type":"text","text":"it","marks":[{"type":"em"}]},{"type":"text","text":" "},
              {"type":"text","text":"gone","marks":[{"type":"strike"}]},{"type":"text","text":" "},
              {"type":"text","text":"x","marks":[{"type":"code"}]},{"type":"text","text":" "},
              {"type":"text","text":"site","marks":[{"type":"link","attrs":{"href":"https://example.com/a b"}}]},{"type":"text","text":" "},
              {"type":"text","text":"evil","marks":[{"type":"link","attrs":{"href":"javascript:alert(1)"}}]},{"type":"text","text":" "},
              {"type":"text","text":"all","marks":[{"type":"link","attrs":{"href":"http://e.com"}},{"type":"em"},{"type":"strong"},{"type":"code"}]}]}
            """).Should().Be("**bold** *it* ~~gone~~ `x` [site](https://example.com/a%20b) evil [***`all`***](http://e.com)");
    }

    [Fact]
    public void Render_UnknownNode_ChildText()
    {
        RenderDoc($$"""
            {"type":"futureWidget","content":[{{P("kept")}},{"type":"mysteryInline","content":[{"type":"text","text":"also"}]}]},
            {"type":"layoutSection","content":[{"type":"layoutColumn","content":[{{P("col")}}]}]}
            """).Should().Be("kept\n\nalso\n\ncol");
    }

    [Fact]
    public void Render_DepthBomb_DoesNotOverflow()
    {
        var sb = new StringBuilder("""{"type":"doc","content":[""");
        for (int i = 0; i < 500; i++)
            sb.Append("""{"type":"blockquote","content":[""");
        sb.Append(P("core"));
        for (int i = 0; i < 500; i++)
            sb.Append("]}");
        sb.Append("]}");

        string md = AdfRenderer.Render(Parse(sb.ToString()));

        md.Should().Contain("core");
    }

    [Fact]
    public void Render_NullOrEmpty_Empty()
    {
        AdfRenderer.Render(null).Should().BeEmpty();
        AdfRenderer.Render(Parse("null")).Should().BeEmpty();
        AdfRenderer.Render(Parse("\"text\"")).Should().BeEmpty();
        AdfRenderer.Render(Parse("[1,2]")).Should().BeEmpty();
        AdfRenderer.Render(Parse("""{"type":"doc","content":[]}""")).Should().BeEmpty();
        AdfRenderer.Render(Parse("""{"type":"doc"}""")).Should().BeEmpty();
    }

    [Fact]
    public void Render_WrongJsonKinds_DoNotThrow()
    {
        string[] inputs =
        [
            """{"type":"doc","content":"oops"}""",
            """{"type":"doc","content":[1,"two",null,[3],{"type":5}]}""",
            """{"type":"doc","content":[{"type":"heading","attrs":"x","content":{"a":1}}]}""",
            """{"type":"doc","content":[{"type":"heading","attrs":{"level":"9"},"content":[{"type":"text","text":7}]}]}""",
            """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"a","marks":"bad"},{"type":"text","text":"b","marks":[1,{"type":"link","attrs":[]}]}]}]}""",
            """{"type":"doc","content":[{"type":"table","content":[1,{"type":"tableRow","content":"x"},{"type":"tableRow","content":[{"type":"tableCell","content":7}]}]}]}""",
            """{"type":"doc","content":[{"type":"orderedList","attrs":{"order":"x"},"content":[{"type":"listItem","content":null}]}]}""",
            """{"type":"doc","content":[{"type":"media","attrs":{"id":5,"alt":[]}},{"type":"date","attrs":{"timestamp":{}}},{"type":"mention","attrs":null}]}""",
        ];

        foreach (string json in inputs)
        {
            Action act = () => AdfRenderer.Render(Parse(json), _ => throw new InvalidOperationException("boom"));
            act.Should().NotThrow(json);
        }
    }

    [Fact]
    public void Render_WrongKindLevel_FallsBackToOne()
    {
        RenderDoc("""{"type":"heading","attrs":{"level":"9"},"content":[{"type":"text","text":"ok"}]}""")
            .Should().Be("# ok");
    }

    [Fact]
    public void Render_LineStartStructure_IsEscaped()
    {
        RenderDoc(P("# not a heading")).Should().Be("\\# not a heading");
    }

    [Fact]
    public void Render_Blockquote_QuotesEveryLine()
    {
        RenderDoc($$"""{"type":"blockquote","content":[{{P("one")}},{{P("two")}}]},{"type":"rule"}""")
            .Should().Be("> one\n>\n> two\n\n---");
    }

    [Theory]
    [InlineData("inlineCard", "# x")]
    [InlineData("inlineCard", "```")]
    [InlineData("inlineCard", "javascript:alert(1)")]
    [InlineData("blockCard", "# x")]
    [InlineData("blockCard", "```")]
    [InlineData("blockCard", "javascript:alert(1)")]
    public void Render_HostileCardUrl_EmitsNothing(string card, string url)
    {
        string md = RenderDoc($$$"""{{{P("before")}}},{"type":"{{{card}}}","attrs":{"url":"{{{url}}}"}},{{{P("after")}}}""");

        md.Should().NotContain("#").And.NotContain("```").And.NotContain("javascript");
        md.Should().Contain("before").And.Contain("after");
    }

    [Theory]
    [InlineData("- item")]
    [InlineData("+ item")]
    [InlineData("* item")]
    [InlineData("12. item")]
    [InlineData("3) item")]
    [InlineData("***")]
    [InlineData("___")]
    [InlineData("| a |")]
    public void Render_LineStartListRuleTable_IsEscaped(string text)
    {
        RenderDoc(P(text)).Should().Be("\\" + text);
    }
}
