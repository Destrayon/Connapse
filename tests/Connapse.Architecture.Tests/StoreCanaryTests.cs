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
        Rules.ComponentStoreInjections(TestAssembly, []).Should().Contain(
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

    [Fact]
    public void ArchRule_KeysRoutesByTheirFullGroupPath()
    {
        const string source = """
            static class Planted
            {
                static void Map(IEndpointRouteBuilder app)
                {
                    var links = app.MapGroup("/api/links").WithTags("x");
                    links.MapGet("/", (IConnectionStore c) => 1);
                    var sites = app.MapGroup("/api/sites");
                    var nested = sites.MapGroup("/{id}");
                    sites.MapGet("/", (IConnectionStore c) => 1);
                    nested.MapGet("/spaces", (ISourceStore s) => 1);
                    app.MapGroup("/api/inline").MapPost("/", (ISourceStore s) => 1);
                }
            }
            """;
        Rules.HandlerStoreDependencies([("Planted.cs", source)]).Should().BeEquivalentTo(
        [
            "Planted GET /api/links/ -> IConnectionStore",
            "Planted GET /api/sites/ -> IConnectionStore",
            "Planted GET /api/sites/{id}/spaces -> ISourceStore",
            "Planted POST /api/inline/ -> ISourceStore",
        ]);
    }

    [Fact]
    public void ArchRule_FollowsHelpersButNotSameNamedStoreCalls()
    {
        const string source = """
            static class Planted
            {
                static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/a", (IServiceProvider sp) => LoadAsync(sp));
                    app.MapGet("/b", (IContainerStore other) => other.GetAsync("x"));
                }

                static Task LoadAsync(IServiceProvider sp) => Planted.ReadAsync(sp.GetRequiredService<IServiceScopeFactory>());
                static Task ReadAsync(IServiceScopeFactory f) => f.CreateScope().ServiceProvider.GetRequiredService<ISettingsStore>().GetAsync("x");
                static Task GetAsync(string id, IFolderStore notReachedFromB) => Task.CompletedTask;
            }
            """;
        Rules.HandlerStoreDependencies([("Planted.cs", source)]).Should().BeEquivalentTo(
        [
            "Planted GET /a -> ISettingsStore",
            "Planted GET /b -> IContainerStore",
        ]);
    }

    [Fact]
    public void ArchRule_DetectsStoresResolvedInComponentCode()
    {
        const string razor = """""
            @inject IServiceScopeFactory Scopes
            <p>@("}")</p>
            @code {
                private string text = """"
                    four quotes, then a brace: }
                    """";
                private async Task LoadAsync()
                {
                    await using var scope = Scopes.CreateAsyncScope();
                    var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
                    var docs = (IDocumentStore)scope.ServiceProvider.GetService(typeof(IDocumentStore));
                }
            }
            """"";
        Rules.ComponentStoreInjections(TestAssembly, [("Components/Pages/Planted.razor", razor)]).Should().Contain(
        [
            "Pages.Planted -> ISourceStore (resolved)",
            "Pages.Planted -> IDocumentStore (resolved)",
        ]);
    }

    [Fact]
    public void RazorSource_FailsClosedOnAnUnbalancedBlock()
    {
        var act = () => RazorSource.CodeBlocks("@code { private void M() { ");
        act.Should().Throw<InvalidOperationException>().WithMessage("*Unbalanced*");
    }

    [Fact]
    public void ArchRule_FollowsStaticHelpersInOtherFilesAndEnumerableResolution()
    {
        const string endpoints = """
            static class PlantedEndpoints
            {
                static void Map(IEndpointRouteBuilder app)
                {
                    app.MapPost("/save", (IServiceProvider sp) => SettingsHelpers.SaveAsync(sp));
                    app.MapGet("/all", (IServiceProvider sp) => sp.GetServices<ISettingsStore>().First());
                    app.MapGet("/group", SettingsHelpers.Handle);
                    app.MapGet("/service", (PlantedService service) => service.RunAsync());
                }
            }
            """;
        const string helpers = """
            static class SettingsHelpers
            {
                public static Task SaveAsync(IServiceProvider sp) => sp.GetRequiredService<ISettingsStore>().SaveAsync("x", 1);
                public static Task Handle(IContainerStore containers) => Task.CompletedTask;
            }

            // An injected service is the migration path, so it is not followed (see HandlerScan remarks).
            class PlantedService(IFolderStore folders)
            {
                public Task RunAsync() => folders.ListAsync(Guid.Empty);
            }
            """;
        Rules.HandlerStoreDependencies([("Endpoints/PlantedEndpoints.cs", endpoints), ("Services/SettingsHelpers.cs", helpers)])
            .Should().BeEquivalentTo(
            [
                "PlantedEndpoints POST /save -> ISettingsStore",
                "PlantedEndpoints GET /all -> ISettingsStore",
                "PlantedEndpoints GET /group -> IContainerStore",
            ]);
    }
}
