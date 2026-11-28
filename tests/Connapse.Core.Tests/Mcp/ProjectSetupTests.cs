using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using Connapse.Web.Mcp;
using FluentAssertions;
using ModelContextProtocol.Server;

namespace Connapse.Core.Tests.Mcp;

/// <summary>
/// The project setup instructions (CON-363): one file in the repo, served unchanged by the `project_setup` tool and
/// the `setup_project` prompt, and covering every step the ticket asks an agent to take.
/// </summary>
[Trait("Category", "Unit")]
public class ProjectSetupTests
{
    [Fact]
    public void Instructions_MatchTheFileInTheRepo()
    {
        string onDisk = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Connapse.Web", "Mcp", "Instructions", "project-setup.md"));

        ProjectSetup.Instructions.Should().Be(onDisk);
    }

    [Fact]
    public void ProjectSetupTool_ReturnsTheInstructions()
    {
        McpTools.ProjectSetupInstructions().Should().Be(ProjectSetup.Instructions);
    }

    [Fact]
    public void SetupProjectPrompt_ReturnsTheInstructions()
    {
        McpPrompts.SetupProject().Should().Be(ProjectSetup.Instructions);
    }

    [Fact]
    public void ProjectSetupTool_IsAReadOnlyToolNamedProjectSetup()
    {
        McpServerToolAttribute tool = typeof(McpTools).GetMethod(nameof(McpTools.ProjectSetupInstructions))!
            .GetCustomAttribute<McpServerToolAttribute>()!;

        tool.Name.Should().Be("project_setup");
        tool.ReadOnly.Should().BeTrue();
        typeof(McpTools).GetMethod(nameof(McpTools.ProjectSetupInstructions))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description.Should().Contain("CLAUDE.md");
    }

    [Fact]
    public void SetupProjectPrompt_IsAPromptNamedSetupProject()
    {
        typeof(McpPrompts).IsDefined(typeof(McpServerPromptTypeAttribute)).Should().BeTrue("WithPromptsFromAssembly finds prompts by this attribute");
        typeof(McpPrompts).GetMethod(nameof(McpPrompts.SetupProject))!
            .GetCustomAttribute<McpServerPromptAttribute>()!.Name.Should().Be("setup_project");
    }

    [Fact]
    public void Instructions_AskExactlyTheTwoQuestions()
    {
        string text = ProjectSetup.Instructions;

        text.Should().Contain("Ask the user exactly two questions");
        text.Should().Contain("**Which login?**");
        text.Should().Contain("\"Sign in a new login\"");
        text.Should().Contain("**CLI or MCP?**");
    }

    [Theory]
    [InlineData("connapse auth list")]
    [InlineData("connapse org current --profile")]
    [InlineData("connapse auth login --profile")]
    [InlineData("\"CONNAPSE_PROFILE\"")]
    [InlineData("\"CONNAPSE_ORG\"")]
    [InlineData(".claude/settings.json")]
    [InlineData("connapse setup claude-code")]
    [InlineData("GET /api/v1/auth/me")]
    [InlineData("memory_space_create")]
    [InlineData("\"kind\":\"personal\"")]
    [InlineData("container_create")]
    [InlineData("unaltered sources")]
    [InlineData("## Connapse")]
    [InlineData("docs/connapse.md")]
    [InlineData("\"autoMemoryEnabled\": false")]
    [InlineData("\"PreToolUse\"")]
    [InlineData("\"SessionStart\"")]
    public void Instructions_CoverEverySetupStep(string phrase)
    {
        ProjectSetup.Instructions.Should().Contain(phrase);
    }

    /// <summary>
    /// A machine that already blocks the built-in memory denies any tool call whose text names its folder. The
    /// instructions, and the hook script the agent writes from them, must never spell that path out, or the
    /// agent following them is blocked on its own hook.
    /// </summary>
    [Fact]
    public void Instructions_NeverSpellOutTheBuiltInMemoryPath()
    {
        Regex blocked = new(@"\.claude/projects/[^/\s""']+/memory(/|\b)|\.claude/[^\s""']*MEMORY\.md", RegexOptions.IgnoreCase);

        blocked.IsMatch(ProjectSetup.Instructions.Replace('\\', '/')).Should().BeFalse();
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root (marked by Connapse.slnx).");
    }
}
