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

    [Fact]
    public void DirectConnects_FindsAliasedAndStaticallyImportedCalls()
    {
        const string source = """
            using Client = ModelContextProtocol.Client.McpClient;
            using static ModelContextProtocol.Client.McpClient;
            class C
            {
                async Task M()
                {
                    await Client.CreateAsync(transport);
                    await CreateAsync(transport);
                }
            }
            """;

        DirectConnects(CSharpSyntaxTree.ParseText(source).GetRoot()).Should().HaveCount(2);
    }

    [Fact]
    public void DirectConnects_IgnoresBareCreateAsyncWithoutTheStaticImport()
    {
        const string source = """
            using Other = Some.Factory;
            class C { async Task M() { await CreateAsync(x); await Other.CreateAsync(x); } }
            """;

        DirectConnects(CSharpSyntaxTree.ParseText(source).GetRoot()).Should().BeEmpty();
    }

    /// <summary>
    /// CreateAsync called on McpClient, on an alias of it (using Client = ...McpClient), or bare under
    /// using static ...McpClient.
    /// </summary>
    internal static IEnumerable<InvocationExpressionSyntax> DirectConnects(SyntaxNode root)
    {
        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(u => u.Name is not null && IsMcpClient(u.Name.ToString())).ToList();
        var names = usings.Where(u => u.Alias is not null).Select(u => u.Alias!.Name.Identifier.ValueText).Append("McpClient").ToHashSet(StringComparer.Ordinal);
        bool staticImport = usings.Any(u => u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword));

        return root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => call.Expression switch
        {
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CreateAsync", Expression: var target } =>
                names.Contains(target.ToString()) || IsMcpClient(target.ToString()),
            IdentifierNameSyntax { Identifier.ValueText: "CreateAsync" } => staticImport,
            _ => false,
        });
    }

    private static bool IsMcpClient(string name) =>
        name == "McpClient" || name.EndsWith(".McpClient", StringComparison.Ordinal);
}
