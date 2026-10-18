namespace Connapse.Architecture.Tests;

/// <summary>
/// Proof tests for epic #783, problem P4: pages and handlers reach data directly instead of through
/// operations. Each list holds today's violations; a migration ticket starts by deleting its entries.
/// </summary>
[Trait("Category", "Unit")]
public class SurfaceRulesTests
{
    [Fact]
    public void Components_DoNotInjectStores_ExceptAllowList()
    {
        ExactMatchList.AssertMatches(
            "component-store-injections.json",
            Rules.ComponentStoreInjections(StructureScan.WebAssembly),
            "Pages call operations, not stores: move the data access into an operation and inject that.");
    }

    [Fact]
    public void SurfaceHandlers_DoNotTakeStores_ExceptAllowList()
    {
        ExactMatchList.AssertMatches(
            "handler-store-dependencies.json",
            Rules.HandlerStoreDependencies(StructureScan.WebAssembly, Rules.WebSurfaceNamespaces),
            "Endpoint and MCP handlers call operations, not stores.");
    }
}
