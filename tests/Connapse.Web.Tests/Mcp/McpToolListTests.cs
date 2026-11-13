using System.Reflection;
using System.Security.Claims;
using Connapse.Identity.Authentication;
using Connapse.Web.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Server;

namespace Connapse.Web.Tests.Mcp;

/// <summary>The compact and full tool lists (CON-346).</summary>
[Trait("Category", "Unit")]
public class McpToolListTests
{
    private static IConfiguration Config(string? toolList = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(toolList is null ? [] : [new(McpToolList.SettingKey, toolList)]).Build();

    private static HttpContext Request(string path) => new DefaultHttpContext { Request = { Path = path } };

    /// <summary>Every tool name in the web assembly that declares a risk.</summary>
    private static IEnumerable<string> RiskTools() => typeof(McpTools).Assembly.GetTypes()
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
        .Where(m => m.IsDefined(typeof(McpToolRiskAttribute)))
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!);

    [Theory]
    [InlineData("/mcp", null, false)]
    [InlineData("/mcp", "compact", false)]
    [InlineData("/mcp", "FULL", true)]
    [InlineData("/mcp/full", null, true)]
    [InlineData("/MCP/Full/x", null, true)]
    [InlineData("/mcp/fuller", null, false)]
    public void IsFull_PathOrSetting(string path, string? setting, bool full) =>
        McpToolList.IsFull(Request(path), Config(setting)).Should().Be(full);

    [Theory]
    [InlineData("full", null, true)]
    [InlineData("compact", null, false)]
    [InlineData("compact", "full", true)]
    [InlineData("FULL", null, false)]
    [InlineData(null, null, false)]
    public void IsFull_TokenFlag_FullUnlessTheServerAlreadyIs(string? claim, string? setting, bool full)
    {
        var http = Request("/mcp");
        if (claim is not null)
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(McpApprovalClaims.ToolList, claim)], "test"));

        McpToolList.IsFull(http, Config(setting)).Should().Be(full);
    }

    [Fact]
    public void IsFull_NoHttpContext_FollowsSetting()
    {
        McpToolList.IsFull(null, Config()).Should().BeFalse();
        McpToolList.IsFull(null, Config("full")).Should().BeTrue();
    }

    [Theory]
    [InlineData("search_knowledge", false, true)]
    [InlineData("find_tools", false, true)]
    [InlineData("call_destructive", false, true)]
    [InlineData("container_delete", false, false)]
    [InlineData("connapse_api_search", false, false)]
    [InlineData("search_knowledge", true, true)]
    [InlineData("container_delete", true, true)]
    [InlineData("connapse_api_search", true, true)]
    [InlineData("find_tools", true, false)]
    [InlineData("call_read", true, false)]
    public void Shows_CompactAndFull(string name, bool full, bool shown) => McpToolList.Shows(name, full).Should().Be(shown);

    [Fact]
    public void CompactInstructions_NameEveryHiddenTool()
    {
        var hidden = RiskTools().Where(n => !McpToolList.Core.Contains(n)).ToList();
        hidden.Should().NotBeEmpty();
        foreach (string name in hidden)
            McpToolList.CompactInstructions.Should().Contain(name, "an agent only searches for what it knows exists");
    }

    [Fact]
    public void Core_AreRealTools() => RiskTools().Should().Contain(McpToolList.Core);

    [Fact]
    public async Task ConfigureSession_CompactGetsCompactInstructions_FullKeepsDefault()
    {
        var compact = new McpServerOptions { ServerInstructions = "default" };
        await McpToolList.ConfigureSession(Request("/mcp"), compact, Config());
        compact.ServerInstructions.Should().Be(McpToolList.CompactInstructions);

        var full = new McpServerOptions { ServerInstructions = "default" };
        await McpToolList.ConfigureSession(Request("/mcp/full"), full, Config());
        full.ServerInstructions.Should().Be("default");
    }
}
