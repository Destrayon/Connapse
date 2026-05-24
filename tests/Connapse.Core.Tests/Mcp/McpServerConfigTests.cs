using Connapse.Web.Mcp;
using FluentAssertions;

namespace Connapse.Core.Tests.Mcp;

/// <summary>
/// Regression net for the MCP ServerInstructions string. Asserts the load-bearing
/// routing-rule phrases survive future copyedits, without locking the exact text.
/// </summary>
[Trait("Category", "Unit")]
public class McpServerConfigTests
{
    [Fact]
    public void McpServerInstructions_IsNonEmptyAndMentionsSearchKnowledge()
    {
        McpServerConfig.McpServerInstructions.Should().NotBeNullOrWhiteSpace();
        McpServerConfig.McpServerInstructions.Should().Contain("search_knowledge");
    }

    [Fact]
    public void McpServerInstructions_ContainsKeyRoutingPhrases()
    {
        // These phrases are the substantive routing directives. Future copyedits
        // are fine as long as these directives survive — see the v2 design doc
        // (docs/superpowers/specs/2026-05-23-mcp-agent-steering-v2-design.md)
        // for why each one matters.
        var instructions = McpServerConfig.McpServerInstructions;

        instructions.Should().Contain("Routing rules");
        instructions.Should().Contain("call `search_knowledge` directly");
        instructions.Should().Contain("Do NOT call `container_list` first");
        instructions.Should().Contain("Never use `list_files` as a substitute");
    }
}
