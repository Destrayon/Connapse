using System.Text.Json;
using Connapse.Web.Mcp;
using FluentAssertions;

namespace Connapse.Web.Tests.Mcp;

/// <summary>The argument check the call tools run before a tool (CON-346): each way arguments can break a schema.</summary>
[Trait("Category", "Unit")]
public class McpToolArgumentsTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "a name" },
            "count": { "type": "integer", "default": "ten" },
            "ratio": { "type": "number" },
            "flag": { "type": ["boolean", "null"] },
            "mode": { "type": "string", "enum": ["Fast", "Slow"] },
            "ids": { "type": "array", "items": { "type": "integer" } },
            "options": { "type": "object", "properties": { "depth": { "type": "integer" } }, "required": ["depth"] },
            "anything": true,
            "untyped": { "default": null },
            "odd": { "type": "uuid" }
          },
          "required": ["name"]
        }
        """).RootElement;

    private static IReadOnlyList<string> Check(string arguments) =>
        McpToolArguments.Check(Schema, JsonDocument.Parse(arguments).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));

    [Fact]
    public void Check_ValidArguments_NoErrors()
    {
        Check("""{"name":"a","count":3,"ratio":0.5,"flag":null,"mode":"Fast","ids":[1,2],"options":{"depth":2},"anything":[{}],"untyped":"x","odd":5}""")
            .Should().BeEmpty();
    }

    [Fact]
    public void Check_MissingRequired_NamesIt() => Check("{}").Should().Equal("name: required");

    [Fact]
    public void Check_UnknownArgument_NamesIt() => Check("""{"name":"a","nmae":"b"}""").Should().Equal("nmae: not a parameter of this tool");

    [Theory]
    [InlineData("""{"name":5}""", "name: expected string, got the number 5")]
    [InlineData("""{"name":"a","count":1.5}""", "count: expected integer, got the number 1.5")]
    [InlineData("""{"name":"a","count":"3"}""", "count: expected integer, got a string")]
    [InlineData("""{"name":"a","ratio":true}""", "ratio: expected number, got a boolean")]
    [InlineData("""{"name":"a","flag":"yes"}""", "flag: expected boolean or null, got a string")]
    [InlineData("""{"name":"a","ids":{}}""", "ids: expected array, got an object")]
    [InlineData("""{"name":"a","options":[]}""", "options: expected object, got an array")]
    [InlineData("""{"name":null}""", "name: expected string, got null")]
    public void Check_WrongType_NamesFieldAndTypes(string arguments, string error) => Check(arguments).Should().Equal(error);

    [Fact]
    public void Check_IntegerWithZeroFraction_Accepted() => Check("""{"name":"a","count":2.0}""").Should().BeEmpty();

    [Fact]
    public void Check_ValueOutsideEnum_ListsAllowed() => Check("""{"name":"a","mode":"fast"}""").Should().Equal("mode: must be one of \"Fast\", \"Slow\"");

    [Fact]
    public void Check_BadArrayItem_NamesIndex() => Check("""{"name":"a","ids":[1,"x",3]}""").Should().Equal("ids[1]: expected integer, got a string");

    [Fact]
    public void Check_NestedObject_ChecksRequiredAndTypes()
    {
        Check("""{"name":"a","options":{}}""").Should().Equal("options.depth: required");
        Check("""{"name":"a","options":{"depth":"deep","extra":1}}""").Should().Equal("options.depth: expected integer, got a string");
    }

    [Fact]
    public void Check_SchemaWithoutProperties_RejectsEveryArgument()
    {
        var empty = JsonDocument.Parse("""{"type":"object"}""").RootElement;
        McpToolArguments.Check(empty, new Dictionary<string, JsonElement> { ["x"] = JsonDocument.Parse("1").RootElement })
            .Should().Equal("x: not a parameter of this tool");
        McpToolArguments.Check(empty, new Dictionary<string, JsonElement>()).Should().BeEmpty();
    }

    [Fact]
    public void Check_MalformedRequiredAndType_Ignored()
    {
        var odd = JsonDocument.Parse("""{"properties":{"a":{"type":7}},"required":"a"}""").RootElement;
        McpToolArguments.Check(odd, new Dictionary<string, JsonElement> { ["a"] = JsonDocument.Parse("1").RootElement })
            .Should().BeEmpty();
        McpToolArguments.Check(odd, new Dictionary<string, JsonElement>()).Should().BeEmpty();
    }
}
