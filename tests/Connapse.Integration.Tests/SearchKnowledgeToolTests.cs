using Connapse.Integration.Tests.CrossSurface;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>search_knowledge takes the search operation from DI (#916); an agent sees only the tool's own arguments.</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SearchKnowledgeToolTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task SearchKnowledge_Schema_ListsOnlyTheSearchArguments()
    {
        await using var mcp = await Surfaces.McpAsync(fixture);

        var tool = (await mcp.ListToolsAsync()).Single(t => t.Name == "search_knowledge");

        tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(["query", "containerId", "mode", "topK", "path", "minScore"]);
    }
}
