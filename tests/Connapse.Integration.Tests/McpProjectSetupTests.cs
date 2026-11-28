using Connapse.Web.Mcp;
using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Connapse.Integration.Tests;

/// <summary>
/// The project setup instructions over a real MCP connection (CON-363): the `project_setup` tool an agent calls
/// and the `setup_project` prompt a person picks both return the one embedded text.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class McpProjectSetupTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task ProjectSetupTool_ReturnsTheInstructions()
    {
        await using McpClient client = await McpTestClient.ConnectAsync(fixture.AdminClient, ownsHttpClient: false);

        CallToolResult result = await client.CallToolAsync("project_setup");

        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be(ProjectSetup.Instructions);
    }

    [Fact]
    public async Task SetupProjectPrompt_IsListedAndReturnsTheInstructions()
    {
        await using McpClient client = await McpTestClient.ConnectAsync(fixture.AdminClient, ownsHttpClient: false);

        var prompts = await client.ListPromptsAsync();
        prompts.Select(p => p.Name).Should().Contain("setup_project");

        GetPromptResult prompt = await client.GetPromptAsync("setup_project");

        PromptMessage message = prompt.Messages.Should().ContainSingle().Subject;
        message.Role.Should().Be(Role.User);
        message.Content.Should().BeOfType<TextContentBlock>().Which.Text.Should().Be(ProjectSetup.Instructions);
    }
}
