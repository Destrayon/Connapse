using FluentAssertions;

namespace Connapse.Architecture.Tests;

/// <summary>
/// Proof tests for epic #783, problem P4: pages and handlers reach data directly instead of through
/// operations. Each list holds today's violations; a migration ticket starts by deleting its entries.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class SurfaceRulesTests
{
    [Fact]
    public void Components_DoNotInjectStores_ExceptAllowList()
    {
        ExactMatchList.AssertMatches(
            "component-store-injections.json",
            Rules.ComponentStoreInjections(StructureScan.WebAssembly, ComponentSources()),
            "Pages call operations, not stores: move the data access into an operation and inject that.");
    }

    [Fact]
    public void SurfaceHandlers_DoNotTakeStores_ExceptAllowList()
    {
        ExactMatchList.AssertMatches(
            "handler-store-dependencies.json",
            Rules.HandlerStoreDependencies(RepoPaths.WebCSharpFiles()),
            "Endpoint and MCP handlers call operations, not stores.");
    }

    [Fact]
    public void McpTools_DependOnlyOnOperations()
    {
        StructureScan.McpTools(StructureScan.WebAssembly).Should().HaveCountGreaterThanOrEqualTo(12, "the scan must see the tools it judges");
        Rules.McpToolDependencies(StructureScan.WebAssembly).Should().BeEmpty(
            "an MCP tool takes the operation the API calls plus its arguments and transport plumbing, nothing else from DI");
    }

    /// <summary>Every .razor file and component code-behind under Components.</summary>
    internal static IEnumerable<(string Path, string Source)> ComponentSources() =>
        Directory.EnumerateFiles(Path.Combine(RepoPaths.WebProject, "Components"), "*.razor*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".razor.cs", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(f => (RepoPaths.RelativeToWeb(f), File.ReadAllText(f)));
}
