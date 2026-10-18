using FluentAssertions;

namespace Connapse.Architecture.Tests;

/// <summary>
/// Proof test for epic #783, problem P4: pages and handlers resolve services at runtime, which hides
/// their real dependencies from every other rule. Covers injected locators and inline calls.
/// </summary>
[Trait("Category", "Unit")]
public class ServiceLocationTests
{
    [Fact]
    public void Surfaces_DoNotUseServiceLocation_ExceptAllowList()
    {
        var injected = Rules.ServiceLocatorDependencies(StructureScan.WebAssembly, Rules.WebSurfaceNamespaces);
        var inline = SurfaceSourceFiles()
            .SelectMany(f => Rules.ServiceLocationCalls(RepoPaths.RelativeToWeb(f), File.ReadAllText(f)));

        ExactMatchList.AssertMatches(
            "service-location.json",
            injected.Concat(inline),
            "Take the dependency through DI (an operation), not IServiceProvider or IServiceScopeFactory.");
    }

    [Fact]
    public void SurfaceSourceFiles_AreFound()
    {
        // Guards the source scans above against passing because they read nothing.
        SurfaceSourceFiles().Should().Contain(f => f.EndsWith("SourcesEndpoints.cs"))
            .And.Contain(f => f.EndsWith("McpTools.cs"))
            .And.Contain(f => f.EndsWith("Sources.razor"));
    }

    internal static IEnumerable<string> SurfaceSourceFiles()
    {
        string web = RepoPaths.WebProject;
        return Directory.EnumerateFiles(Path.Combine(web, "Endpoints"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(web, "Mcp"), "*.cs", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.razor", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal);
    }
}
