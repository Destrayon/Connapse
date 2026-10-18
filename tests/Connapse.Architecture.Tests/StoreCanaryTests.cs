using Connapse.Architecture.Tests.Canaries;
using FluentAssertions;

namespace Connapse.Architecture.Tests;

/// <summary>
/// Proves the store rules can still see: planted violations must be reported. If a rule is weakened
/// or a scan goes blind, the production lists would keep passing; these fail instead.
/// </summary>
[Trait("Category", "Unit")]
public class StoreCanaryTests
{
    private static System.Reflection.Assembly TestAssembly => typeof(PlantedEndpoints).Assembly;

    [Fact]
    public void ArchRule_DetectsComponentStoresInjectedWrappedOrByConstructor()
    {
        Rules.ComponentStoreInjections(TestAssembly).Should().Contain(
        [
            "Connapse.Architecture.Tests.Canaries.PlantedComponent -> ISourceStore",
            "Connapse.Architecture.Tests.Canaries.PlantedComponent -> Lazy<IFolderStore>",
            "Connapse.Architecture.Tests.Canaries.PlantedConstructorComponent -> ISettingsStore",
        ]);
    }

    [Fact]
    public void ArchRule_ReportsEachHandlerAndEveryWayItReachesAStore()
    {
        const string source = """
            static class PlantedEndpoints
            {
                public static void Map(RouteGroupBuilder group)
                {
                    group.MapGet("/", async (ISourceStore sources, CancellationToken ct) => { await Task.Yield(); return 1; });
                    group.MapPost("/", async ([FromServices] ISourceStore sources) => await sources.ListAsync());
                    group.MapPut("/{id}", (IServiceProvider services) => services.GetRequiredService<IContainerStore>());
                    group.MapPatch("/{id}", (IServiceProvider services) => services.GetService(typeof(Connapse.Core.Interfaces.IDocumentStore)));
                    group.MapDelete("/{id}", DeleteAsync);
                }

                private static Task DeleteAsync(Lazy<IFolderStore> folders, IDbContextFactory<KnowledgeDbContext> db) => Task.CompletedTask;
            }

            static class PlantedTools
            {
                [McpServerTool(Name = "planted_tool"), Description("x")]
                public static string Tool(IServiceProvider services) => services.GetRequiredService<ISettingsStore>().ToString();
            }
            """;

        Rules.HandlerStoreDependencies([("Planted.cs", source)]).Should().BeEquivalentTo(
        [
            "Planted GET / -> ISourceStore",
            "Planted POST / -> ISourceStore",
            "Planted PUT /{id} -> IContainerStore",
            "Planted PATCH /{id} -> IDocumentStore",
            "Planted DELETE /{id} -> Lazy<IFolderStore>",
            "Planted DELETE /{id} -> IDbContextFactory<KnowledgeDbContext>",
            "Planted planted_tool -> ISettingsStore",
        ]);
    }

    [Fact]
    public void ArchRule_FindsRoutesOutsideTheEndpointsFolder()
    {
        // Program.cs maps routes too; the scan must cover every file in the web project.
        RepoPaths.WebCSharpFiles().Select(f => f.Path).Should()
            .Contain("Program.cs").And.Contain("Endpoints/SourcesEndpoints.cs").And.Contain("Mcp/McpTools.cs")
            .And.NotContain(p => p.StartsWith("obj/") || p.StartsWith("bin/"));
    }
}
