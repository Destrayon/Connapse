using System.ComponentModel;
using Connapse.Application;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace Connapse.Web.Mcp;

/// <summary>
/// The step-by-step instructions an agent follows to set Connapse up for a project (CON-363). The text lives in
/// one file, <c>Mcp/Instructions/project-setup.md</c>, embedded in this assembly, so the MCP tool, the MCP prompt
/// and the connapse CLI all serve the same words.
/// </summary>
public static class ProjectSetup
{
    internal const string ResourceName = "Connapse.Web.Mcp.Instructions.project-setup.md";

    private static readonly Lazy<string> Text = new(() =>
    {
        using Stream stream = typeof(ProjectSetup).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    });

    /// <summary>The setup instructions, as markdown.</summary>
    public static string Instructions => Text.Value;
}

public partial class McpTools
{
    [McpServerTool(Name = "project_setup", ReadOnly = true, Idempotent = true),
     Authorize(Policy = "Scope:KnowledgeRead"), McpToolRisk(RiskTier.Read),
     Description("Get step-by-step instructions to set Connapse up for the project you're working in: pick the saved login and set it for the project, choose the CLI or MCP, use a memory space for the project's memories and containers for its sources, record them in CLAUDE.md, and add a hook that turns off Claude Code's built-in memory. Call it when the user asks to set up or connect Connapse for a project, then follow the steps end to end. It changes nothing itself.")]
    public static string ProjectSetupInstructions() => ProjectSetup.Instructions;
}

/// <summary>MCP prompts: user-chosen messages a client offers, such as a slash command in Claude Code.</summary>
[McpServerPromptType]
public static class McpPrompts
{
    [McpServerPrompt(Name = "setup_project"),
     Description("Set Connapse up for this project: login, CLI or MCP, memory space, containers, CLAUDE.md notes and a hook that turns off Claude Code's built-in memory.")]
    public static string SetupProject() => ProjectSetup.Instructions;
}
