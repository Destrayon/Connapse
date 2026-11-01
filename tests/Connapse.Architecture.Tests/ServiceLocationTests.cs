using FluentAssertions;

namespace Connapse.Architecture.Tests;

/// <summary>
/// Proof test for epic #783, problem P4: pages and handlers resolve services at runtime, which hides
/// their real dependencies from every other rule. Covers injected locators and inline calls.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class ServiceLocationTests
{
    [Fact]
    public void Surfaces_DoNotUseServiceLocation_ExceptAllowList()
    {
        var files = RepoPaths.WebCSharpFiles().ToList();
        var injected = Rules.ServiceLocatorDependencies(StructureScan.WebAssembly, files);
        // Handlers mapped from another file are route handlers too, wherever they are declared.
        var routes = SourceScan.QualifiedRoutes(files.Select(f => f.Source));
        var inline = files
            .SelectMany(f => Rules.ServiceLocationCalls(f.Path, f.Source, wholeFile: IsSurfaceFile(f.Path), routes))
            .Concat(RazorFiles().SelectMany(f => Rules.ServiceLocationCalls(f.Path, f.Source)));

        ExactMatchList.AssertMatches(
            "service-location.json",
            injected.Concat(inline),
            "Take the dependency through DI (an operation), not IServiceProvider or IServiceScopeFactory.");
    }

    [Fact]
    public void SurfaceSourceFiles_AreFound()
    {
        // Guards the source scans above against passing because they read nothing.
        RepoPaths.WebCSharpFiles().Select(f => f.Path).Should()
            .Contain("Endpoints/SourcesEndpoints.cs").And.Contain("Mcp/McpTools.cs").And.Contain("Program.cs");
        RazorFiles().Select(f => f.Path).Should().Contain("Components/Pages/Sources.razor");
    }

    /// <summary>
    /// Endpoints, MCP tools and component code-behind are surfaces throughout. Elsewhere (Program.cs
    /// startup, middleware such as the rate limiter) only route handlers are: resolving services
    /// there is composition, not a page or handler hiding a dependency.
    /// </summary>
    internal static bool IsSurfaceFile(string path) =>
        path.StartsWith("Endpoints/", StringComparison.Ordinal) || path.StartsWith("Mcp/", StringComparison.Ordinal)
        || path.StartsWith("Components/", StringComparison.Ordinal);

    internal static IEnumerable<(string Path, string Source)> RazorFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoPaths.WebProject, "Components"), "*.razor", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(f => (RepoPaths.RelativeToWeb(f), File.ReadAllText(f)));
}
