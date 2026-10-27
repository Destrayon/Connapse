using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Mcp;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Mcp;

[Trait("Category", "Unit")]
public class McpToolsCodeTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();

    private static readonly CodeDefinition Post = new(
        "Acme.Billing.Ledger.Post", "method", "csharp", "/src/Ledger.cs", 7, 12, "public void Post(Invoice invoice)", "doc-1");

    private static readonly CodeFileMatch Caller = new(
        "/src/Invoices.cs", "doc-2", 2.91234, "calls it", [new CodeLine(9, "_ledger.Post(invoice);")]);

    private readonly ICodeTools _code = Substitute.For<ICodeTools>();
    private readonly IServiceProvider _services;

    public McpToolsCodeTests()
    {
        var containers = Substitute.For<IContainerStore>();
        containers.GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new Container(ContainerId.ToString(), "repo", null, DateTime.UtcNow, DateTime.UtcNow));

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(containers);
        services.GetService(typeof(ISourceStore)).Returns(Substitute.For<ISourceStore>());
        services.GetService(typeof(ICodeTools)).Returns(_code);
        _services = services;
    }

    [Fact]
    public async Task CodeFindSymbol_ReturnsDefinitionsAsJsonUnderTheNotice()
    {
        _code.FindSymbolAsync(Arg.Any<CodeScope>(), "Ledger.Post", CodeMatch.Words, true, 5, Arg.Any<CancellationToken>())
            .Returns(new CodeSymbolResult([Post], [new CodeUse("Acme.AuditLog.Write", "method", "/src/AuditLog.cs", 4, "calls", 0.9)], [Caller]));

        string result = await McpTools.CodeFindSymbol(_services, ContainerId.ToString(), "Ledger.Post", "Words", follow: true, limit: 5);

        using JsonDocument json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("notice").GetString().Should().Be(McpTools.CodeNotice);
        JsonElement definition = json.RootElement.GetProperty("definitions")[0];
        definition.GetProperty("symbol").GetString().Should().Be("Acme.Billing.Ledger.Post");
        definition.GetProperty("path").GetString().Should().Be("src/Ledger.cs");
        definition.GetProperty("startLine").GetInt32().Should().Be(7);
        definition.GetProperty("endLine").GetInt32().Should().Be(12);
        json.RootElement.GetProperty("uses")[0].GetProperty("symbol").GetString().Should().Be("Acme.AuditLog.Write");
        JsonElement file = json.RootElement.GetProperty("files")[0];
        file.GetProperty("score").GetDouble().Should().Be(2.912);
        file.GetProperty("lines")[0].GetProperty("text").GetString().Should().Be("_ledger.Post(invoice);");
        await _code.Received(1).FindSymbolAsync(
            Arg.Is<CodeScope>(s => s.OwnerId == ContainerId), "Ledger.Post", CodeMatch.Words, true, 5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CodeFindSymbol_WithoutFollow_LeavesUsesOut()
    {
        _code.FindSymbolAsync(Arg.Any<CodeScope>(), Arg.Any<string>(), CodeMatch.Exact, false, 10, Arg.Any<CancellationToken>())
            .Returns(new CodeSymbolResult([Post], [], []));

        string result = await McpTools.CodeFindSymbol(_services, ContainerId.ToString(), "Post");

        using JsonDocument json = JsonDocument.Parse(result);
        json.RootElement.TryGetProperty("uses", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("regex")]
    [InlineData("fuzzy")]
    public async Task CodeFindSymbol_UnknownMatch_IsAnErrorAndAsksNothing(string match)
    {
        string result = await McpTools.CodeFindSymbol(_services, ContainerId.ToString(), "Post", match);

        result.Should().StartWith("Error: match must be");
        _code.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task CodeReferences_ParsesKindAndReturnsFiles()
    {
        _code.ReferencesAsync(Arg.Any<CodeScope>(), "Ledger.Post", CodeReferenceKind.Calls, 20, Arg.Any<CancellationToken>())
            .Returns(new CodeReferencesResult([Post], [Caller]));

        string result = await McpTools.CodeReferences(_services, ContainerId.ToString(), "Ledger.Post", "calls");

        using JsonDocument json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("files")[0].GetProperty("path").GetString().Should().Be("src/Invoices.cs");
        json.RootElement.GetProperty("files")[0].GetProperty("reason").GetString().Should().Be("calls it");
    }

    [Fact]
    public async Task CodeImpact_ReturnsAffectedSymbolsAndTests()
    {
        _code.ImpactAsync(Arg.Any<CodeScope>(), "Ledger.Post", 2, 30, Arg.Any<CancellationToken>())
            .Returns(new CodeImpactResult(
                [Post],
                [new CodeImpacted("Acme.Tests.LedgerTests.Posts", "method", "/tests/LedgerTests.cs", 1, 0.8, IsTest: true)],
                [Caller],
                ["tests/LedgerTests.cs"]));

        string result = await McpTools.CodeImpact(_services, ContainerId.ToString(), "Ledger.Post");

        using JsonDocument json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("affected")[0].GetProperty("isTest").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("tests")[0].GetString().Should().Be("tests/LedgerTests.cs");
    }

    [Fact]
    public async Task CodeRepoMap_PassesThePathAndReturnsRankedFiles()
    {
        _code.RepoMapAsync(Arg.Any<CodeScope>(), "src/billing", 30, Arg.Any<CancellationToken>())
            .Returns(new CodeRepoMap(
                [new CodeMapFile("/src/billing/Ledger.cs", "doc-1", 1.1, [new CodeMapSymbol("Acme.Billing.Ledger", "class", 3, null, 2)])],
                TotalFiles: 4));

        string result = await McpTools.CodeRepoMap(_services, ContainerId.ToString(), "src/billing");

        using JsonDocument json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("totalFiles").GetInt32().Should().Be(4);
        json.RootElement.GetProperty("files")[0].GetProperty("symbols")[0].GetProperty("referencedBy").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task EveryTool_Denied_ShowsNothing()
    {
        _code.FindSymbolAsync(default!, default!, default, default, default, default).ReturnsForAnyArgs(CodeSymbolResult.DeniedResult);
        _code.ReferencesAsync(default!, default!, default, default, default).ReturnsForAnyArgs(CodeReferencesResult.DeniedResult);
        _code.ImpactAsync(default!, default!, default, default, default).ReturnsForAnyArgs(CodeImpactResult.DeniedResult);
        _code.RepoMapAsync(default!, default, default, default).ReturnsForAnyArgs(CodeRepoMap.DeniedResult);
        string id = ContainerId.ToString();

        string[] results =
        [
            await McpTools.CodeFindSymbol(_services, id, "Post"),
            await McpTools.CodeReferences(_services, id, "Post"),
            await McpTools.CodeImpact(_services, id, "Post"),
            await McpTools.CodeRepoMap(_services, id),
        ];

        results.Should().AllBe(McpTools.CodeDeniedError);
    }

    [Fact]
    public async Task EveryTool_UnknownContainer_IsAnErrorAndAsksNothing()
    {
        string id = Guid.NewGuid().ToString();

        string[] results =
        [
            await McpTools.CodeFindSymbol(_services, id, "Post"),
            await McpTools.CodeReferences(_services, id, "Post"),
            await McpTools.CodeImpact(_services, id, "Post"),
            await McpTools.CodeRepoMap(_services, id),
        ];

        results.Should().AllSatisfy(r => r.Should().Be($"Error: Container '{id}' not found."));
        _code.ReceivedCalls().Should().BeEmpty();
    }
}
