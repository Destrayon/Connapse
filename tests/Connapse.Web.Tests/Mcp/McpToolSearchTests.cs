using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Application;
using Connapse.Identity.Services;
using Connapse.Web.Mcp;
using Connapse.Web.Services.GenericApi;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;

namespace Connapse.Web.Tests.Mcp;

/// <summary>find_tools and the call tools (CON-346) over a handful of fake tools.</summary>
[Trait("Category", "Unit")]
public class McpToolSearchTests
{
    [McpServerToolType]
    public static class FakeTools
    {
        [McpServerTool(Name = "widget_read"), Authorize, McpToolRisk(RiskTier.Read), Description("Reads widgets from the shelf. More text.")]
        public static string Read(string name, int count = 1) => $"read {name} {count}";

        [McpServerTool(Name = "widget_create"), Authorize(Policy = "Writer"), McpToolRisk(RiskTier.Write), Description("Creates a widget.")]
        public static string Create(string name) => $"created {name}";

        [McpServerTool(Name = "widget_delete"), Authorize, McpToolRisk(RiskTier.Destructive), Description("Deletes a widget.")]
        public static string Delete(string name) => $"deleted {name}";

        [McpServerTool(Name = "key_rotate"), Authorize, McpToolRisk(RiskTier.Credential), Description("Rotates a key.")]
        public static string Rotate() => "rotated";

        [McpServerTool(Name = "widget_fail"), McpToolRisk(RiskTier.Read), Description("Fails.")]
        public static string Fail(bool mcp) => mcp ? throw new McpException("bad widget") : throw new InvalidOperationException("secret detail");

        [McpServerTool(Name = "widget_cancel"), McpToolRisk(RiskTier.Read), Description("Cancels.")]
        public static string Cancel() => throw new OperationCanceledException();

        [McpServerTool(Name = "widget_open"), AllowAnonymous, McpToolRisk(RiskTier.Read), Description("Open to all.")]
        public static string Open() => "open";

        [McpServerTool(Name = "no_risk"), Description("Declares no risk.")]
        public static string NoRisk() => "none";
    }

    private static readonly ClaimsPrincipal Reader = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "r")], "test"));
    private static readonly ClaimsPrincipal Writer = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "w"), new Claim(ClaimTypes.Role, "Writer")], "test"));

    private readonly ServiceProvider _services;
    private readonly McpServerPrimitiveCollection<McpServerTool> _tools = [];

    public McpToolSearchTests()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddAuthorization(o => o.AddPolicy("Writer", p => p.RequireRole("Writer")));
        collection.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        collection.AddSingleton<EndpointDataSource>(new DefaultEndpointDataSource());
        collection.AddSingleton<ApiCatalog>();
        collection.AddScoped<ApiDispatcher>();
        collection.AddSingleton(Substitute.For<IMcpApprovalStore>());
        collection.AddScoped<McpApprovalGate>();
        collection.AddScoped<McpToolSearch>();
        _services = collection.BuildServiceProvider();

        foreach (var method in typeof(FakeTools).GetMethods().Where(m => m.IsDefined(typeof(McpServerToolAttribute), false)))
            _tools.Add(McpServerTool.Create(method, target: null, new McpServerToolCreateOptions { Services = _services }));
        foreach (var method in typeof(McpSearchTools).GetMethods().Where(m => m.IsDefined(typeof(McpServerToolAttribute), false)))
            _tools.Add(McpServerTool.Create(method, target: null, new McpServerToolCreateOptions { Services = _services }));
    }

    private RequestContext<CallToolRequestParams> Context(ClaimsPrincipal user)
    {
        var server = Substitute.For<McpServer>();
        server.ServerOptions.Returns(new McpServerOptions { ToolCollection = _tools });
        return new RequestContext<CallToolRequestParams>(server, new JsonRpcRequest { Method = "tools/call" }, new CallToolRequestParams { Name = "x" })
        {
            Services = _services,
            User = user,
        };
    }

    private McpToolSearch Search => _services.GetRequiredService<McpToolSearch>();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private Task<CallToolResult> Call(ClaimsPrincipal user, string callTool, string tool, string? arguments = null) =>
        Search.CallAsync(Context(user), callTool, tool, arguments is null ? null : Json(arguments), CancellationToken.None);

    [Theory]
    [InlineData(RiskTier.Read, "call_read")]
    [InlineData(RiskTier.Write, "call_write")]
    [InlineData(RiskTier.Destructive, "call_destructive")]
    [InlineData(RiskTier.Credential, "call_destructive")]
    public void CallToolFor_EachRisk_NamesItsCallTool(RiskTier risk, string expected) => McpToolSearch.CallToolFor(risk).Should().Be(expected);

    [Fact]
    public async Task FindAsync_NoQuery_ListsAllowedRiskToolsOnly()
    {
        var found = await Search.FindAsync(Context(Reader), null, CancellationToken.None);

        found.Select(e => e.Name).Should().BeEquivalentTo(
            ["widget_read", "widget_delete", "key_rotate", "widget_fail", "widget_cancel", "widget_open"],
            "the reader lacks the Writer policy, and tools without a risk (search, call, no_risk) aren't findable");
    }

    [Fact]
    public async Task FindAsync_Writer_SeesWriterTool()
    {
        var found = await Search.FindAsync(Context(Writer), "create", CancellationToken.None);
        found[0].Name.Should().Be("widget_create");
    }

    [Fact]
    public async Task FindAsync_ExactName_RanksFirst()
    {
        var found = await Search.FindAsync(Context(Reader), "widget_delete", CancellationToken.None);
        found[0].Name.Should().Be("widget_delete");
    }

    [Fact]
    public async Task FindAsync_WordsScoreNameAboveArgumentsAboveDescription()
    {
        var found = await Search.FindAsync(Context(Reader), "the shelf count", CancellationToken.None);
        found.Select(e => e.Name).Should().Equal("widget_read");

        (await Search.FindAsync(Context(Reader), "zzz", CancellationToken.None)).Should().BeEmpty();
        (await Search.FindAsync(Context(Reader), "a to of", CancellationToken.None)).Should().HaveCount(6, "stop words leave no query, which lists every tool");
    }

    [Fact]
    public async Task Describe_ShowsTopThreeInFullAndRestAsLines()
    {
        var found = await Search.FindAsync(Context(Reader), null, CancellationToken.None);
        string text = Search.Describe(found, null, 5, Reader);

        text.Should().Contain("## key_rotate — Credential, run with call_destructive (a person must approve it first)");
        text.Should().Contain("arguments schema: {");
        text.Should().Contain("Also matching");
        text.Should().Contain("## widget_delete — Destructive, run with call_destructive");
        text.Should().Contain("- widget_fail [Read, call_read]: Fails.");
        text.Split('\n').Count(l => l.StartsWith("## ", StringComparison.Ordinal)).Should().Be(McpToolSearch.Detailed);
        text.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal)).Should().Be(2, "limit 5 leaves two after the three in full");
    }

    [Fact]
    public void Describe_NoApprovalNeeded_OmitsApprovalNote()
    {
        var reader = new ClaimsPrincipal(new ClaimsIdentity([new Claim(Connapse.Identity.Authentication.McpApprovalClaims.RequiresApproval, "false")], "test"));
        var entry = McpToolSearch.Entry(_tools.First(t => t.ProtocolTool.Name == "widget_delete"))!;
        Search.Describe([entry], "x", 10, reader).Should().NotContain("approve");
        Search.Describe([entry], "x", 10, null).Should().NotContain("approve");
    }

    [Fact]
    public void Describe_FirstSentenceOnly_ForLines()
    {
        var entries = Enumerable.Repeat(McpToolSearch.Entry(_tools.First(t => t.ProtocolTool.Name == "widget_read"))!, 4).ToList();
        Search.Describe(entries, "x", 10, Reader).Should().Contain("Reads widgets from the shelf. More text.\n").And.Contain(": Reads widgets from the shelf.").And.NotContain(": Reads widgets from the shelf. More text.");
    }

    [Fact]
    public void Describe_NothingFound_SaysWhatToTry()
    {
        Search.Describe([], "zzz", 10, Reader).Should().Contain("No tool matches 'zzz'").And.Contain("find_tools with no query");
        Search.Describe([], " ", 10, Reader).Should().Be("This token may use no tools.");
    }

    [Fact]
    public async Task CallAsync_RightCallTool_RunsTool()
    {
        var result = await Call(Reader, "call_read", "widget_read", """{"name":"a","count":2}""");
        result.IsError.Should().NotBe(true);
        Text(result).Should().Be("read a 2");
    }

    [Fact]
    public async Task CallAsync_ArgumentsAsJsonString_Parsed()
    {
        var result = await Search.CallAsync(Context(Reader), "call_read", " widget_read ", JsonSerializer.SerializeToElement("""{"name":"a"}"""), CancellationToken.None);
        Text(result).Should().Be("read a 1");
    }

    [Theory]
    [InlineData("call_read", "widget_delete", "'widget_delete' is a Destructive tool; run it with call_destructive, not call_read.")]
    [InlineData("call_write", "widget_delete", "run it with call_destructive, not call_write")]
    [InlineData("call_read", "key_rotate", "run it with call_destructive")]
    [InlineData("call_destructive", "widget_read", "'widget_read' is a Read tool; run it with call_read")]
    public async Task CallAsync_WrongCallTool_Refused(string callTool, string tool, string message)
    {
        var result = await Call(Reader, callTool, tool, """{"name":"a"}""");
        result.IsError.Should().BeTrue();
        Text(result).Should().Contain(message);
    }

    [Fact]
    public async Task CallAsync_WriterPolicy_EnforcedForCaller()
    {
        Text(await Call(Reader, "call_write", "widget_create", """{"name":"a"}""")).Should().Contain("This token may not use 'widget_create'.");
        Text(await Call(Writer, "call_write", "widget_create", """{"name":"a"}""")).Should().Be("created a");
    }

    [Fact]
    public async Task CallAsync_Anonymous_DeniedAuthorizedToolButAllowedOpenOne()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        Text(await Call(anonymous, "call_read", "widget_read", """{"name":"a"}""")).Should().Contain("may not use");
        Text(await Call(anonymous, "call_read", "widget_open")).Should().Be("open");
    }

    [Theory]
    [InlineData("find_tools")]
    [InlineData("call_destructive")]
    [InlineData("connapse_api_write")]
    public async Task CallAsync_SearchCallOrGenericTool_Refused(string tool) =>
        Text(await Call(Reader, "call_read", tool)).Should().Contain("isn't run through a call tool");

    [Fact]
    public async Task CallAsync_UnknownName_SuggestsClosest()
    {
        Text(await Call(Reader, "call_read", "widget_reed")).Should().Contain("no tool named 'widget_reed'").And.Contain("widget_");
        Text(await Call(Reader, "call_read", "qqq")).Should().EndWith("Use the exact name find_tools gives.");
    }

    [Fact]
    public async Task CallAsync_ToolWithoutRisk_Unknown() =>
        Text(await Call(Reader, "call_read", "no_risk")).Should().Contain("no tool named 'no_risk'");

    [Theory]
    [InlineData("[1]")]
    [InlineData("\"not json\"")]
    [InlineData("\"[1]\"")]
    public async Task CallAsync_ArgumentsNotObject_Refused(string arguments) =>
        Text(await Call(Reader, "call_read", "widget_read", arguments)).Should().Contain("arguments must be a JSON object");

    [Fact]
    public async Task CallAsync_BadArguments_ListsProblemsAndSchema()
    {
        string text = Text(await Call(Reader, "call_read", "widget_read", """{"count":"x"}"""));
        text.Should().Contain("name: required").And.Contain("count: expected integer").And.Contain("Its arguments schema:");
    }

    [Fact]
    public async Task CallAsync_NullArguments_TreatedAsEmpty() =>
        Text(await Call(Reader, "call_read", "widget_read", "null")).Should().Contain("name: required");

    [Fact]
    public async Task CallAsync_ToolThrowsMcpException_ShowsMessage() =>
        Text(await Call(Reader, "call_read", "widget_fail", """{"mcp":true}""")).Should().Contain("'widget_fail' failed").And.Contain("bad widget");

    [Fact]
    public async Task CallAsync_ToolThrowsOther_HidesMessage()
    {
        var result = await Call(Reader, "call_read", "widget_fail", """{"mcp":false}""");
        Text(result).Should().NotContain("secret detail");
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task CallAsync_Cancelled_Propagates()
    {
        var act = () => Call(Reader, "call_read", "widget_cancel");
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Entry_ApiOperation_ShapesParametersAndBody()
    {
        var endpoint = new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/x"), 0, null, null);
        var operation = new OperationAttribute("things.update") { Scope = "things:write", Risk = RiskTier.Write };
        ApiRoute Route(string method, string path, bool idRequired, bool bodyRequired) => new(
            "things.update", method, path, "things:write", RiskTier.Write, "Updates a thing", ["things"],
            [new ApiParameter("id", "path", idRequired, JsonNode.Parse("""{"type":"string"}"""), "The thing"), new ApiParameter("dry", "query", false, null, null)],
            JsonNode.Parse("""{"type":"object","properties":{"title":{"type":"string"}}}"""), bodyRequired, "application/json");

        var entry = McpToolSearch.Entry([new ApiCatalogEntry(Route("PUT", "/things/{id}", true, true), endpoint, operation),
            new ApiCatalogEntry(Route("PATCH", "/things/{id}", true, true), endpoint, operation)]);

        entry.Name.Should().Be("things.update");
        entry.CallTool.Should().Be("call_write");
        entry.Description.Should().Contain("PUT /things/{id} — Updates a thing").And.Contain("Needs the things:write scope.");
        var schema = JsonNode.Parse(entry.InputSchema.GetRawText())!;
        schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("body");
        schema["properties"]!["parameters"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("id");
        schema["properties"]!["parameters"]!["properties"]!["id"]!["description"]!.GetValue<string>().Should().Be("path: The thing");
        schema["properties"]!["body"]!["properties"]!["title"].Should().NotBeNull();

        var optional = McpToolSearch.Entry([new ApiCatalogEntry(Route("POST", "/things", false, false), endpoint, operation)]);
        JsonNode.Parse(optional.InputSchema.GetRawText())!["required"].Should().BeNull();
    }

    [Fact]
    public void Entry_ReadRiskOnPostRoute_RunsThroughCallWrite()
    {
        var endpoint = new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/x"), 0, null, null);
        var operation = new OperationAttribute("things.query") { Scope = "things:read", Risk = RiskTier.Read };
        var post = new ApiRoute("things.query", "POST", "/things/query", "things:read", RiskTier.Read, null, [], [], null, false, null);
        var get = post with { Method = "GET" };

        McpToolSearch.Entry([new ApiCatalogEntry(post, endpoint, operation)]).CallTool.Should().Be("call_write");
        McpToolSearch.Entry([new ApiCatalogEntry(get, endpoint, operation)]).CallTool.Should().Be("call_read");
    }
}
