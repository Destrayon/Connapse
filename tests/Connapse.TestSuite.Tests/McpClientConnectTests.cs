using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connapse.TestSuite.Tests;

/// <summary>
/// CON-218: an MCP client made with the SDK's defaults gives its discover probe 5 s, and under load the
/// shared test host can take longer, so the connect fails. Tests connect through McpTestClient, which
/// turns that limit off; a direct McpClient.CreateAsync anywhere else brings the flake back.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class McpClientConnectTests
{
    private const string Helper = "tests/Connapse.Integration.Tests/McpTestClient.cs";

    [Fact]
    public void TestCode_ConnectsToMcp_OnlyThroughTheHelper()
    {
        var files = TestSources.Files();
        var direct = files.Where(f => f.RelativePath != Helper)
            .SelectMany(f => DirectConnects(f.Root).Select(call => $"{f.RelativePath}: {FixedDelayTests.Member(call)}"));

        direct.Should().BeEmpty("connect with McpTestClient.ConnectAsync, which lifts the SDK's 5 s discover-probe limit");
        DirectConnects(files.Single(f => f.RelativePath == Helper).Root).Should().ContainSingle("the helper is the one place that connects");
    }

    [Fact]
    public void DirectConnects_FindsCreateAsyncOnMcpClientOnly()
    {
        const string source = """
            class C
            {
                async Task M()
                {
                    await McpClient.CreateAsync(transport);
                    await ModelContextProtocol.Client.McpClient.CreateAsync(transport, options);
                    await McpTestClient.ConnectAsync(http);
                    await Other.CreateAsync(transport);
                }
            }
            """;

        DirectConnects(CSharpSyntaxTree.ParseText(source).GetRoot()).Should().HaveCount(2);
    }

    internal static IEnumerable<InvocationExpressionSyntax> DirectConnects(SyntaxNode root) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
            call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CreateAsync", Expression: var target }
            && (target.ToString() == "McpClient" || target.ToString().EndsWith(".McpClient", StringComparison.Ordinal)));
}
