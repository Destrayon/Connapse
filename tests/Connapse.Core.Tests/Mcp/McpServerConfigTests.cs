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
}
