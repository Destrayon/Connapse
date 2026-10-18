using Connapse.Architecture.Tests.Canaries;
using FluentAssertions;

namespace Connapse.Architecture.Tests;

/// <summary>Proves the service-location and response-shape rules still report planted violations.</summary>
[Trait("Category", "Unit")]
public class LocationAndShapeCanaryTests
{
    private static System.Reflection.Assembly TestAssembly => typeof(PlantedEndpoints).Assembly;

    [Fact]
    public void ArchRule_DetectsPlantedServiceLocators()
    {
        const string source = """
            static class Planted
            {
                static void Map(IEndpointRouteBuilder app)
                {
                    var g = app.MapGroup("/api/p");
                    g.MapGet("/x", (IServiceProvider sp) => 1);
                }

                [McpServerTool(Name = "planted_tool")]
                static string Tool(IServiceScopeFactory scopes) => "";
            }
            """;
        Rules.ServiceLocatorDependencies(TestAssembly, [("Planted.cs", source)]).Should().Contain(
        [
            "Connapse.Architecture.Tests.Canaries.PlantedComponent -> IServiceScopeFactory",
            "Planted GET /api/p/x -> IServiceProvider",
            "Planted planted_tool -> IServiceScopeFactory",
        ]);
    }

    [Fact]
    public void ArchRule_CountsInlineServiceLocationPerSite()
    {
        const string source = """
            app.MapGet("/x", (HttpContext http) => http.RequestServices.GetRequiredService<IFoo>());
            app.MapPost("/y", (HttpContext http) =>
            {
                http.RequestServices.GetRequiredService<IFoo>();
                return http.RequestServices.GetRequiredService<IBar>();
            });
            static void Helper(IServiceScopeFactory factory) { using var scope = factory.CreateScope(); }
            """;
        Rules.ServiceLocationCalls("Endpoints/Planted.cs", source).Should().BeEquivalentTo(
        [
            "Endpoints/Planted.cs: GET /x uses RequestServices",
            "Endpoints/Planted.cs: GET /x uses GetRequiredService",
            "Endpoints/Planted.cs: POST /y uses RequestServices x2",
            "Endpoints/Planted.cs: POST /y uses GetRequiredService x2",
            "Endpoints/Planted.cs: Helper uses CreateScope",
        ]);
        // Outside surface files only route handlers count: startup composition is allowed there.
        Rules.ServiceLocationCalls("Program.cs", source, wholeFile: false).Should()
            .Contain("Program.cs: GET /x uses RequestServices")
            .And.NotContain(e => e.Contains("Helper"));
    }

    [Fact]
    public void ShapeRule_CountsAnonymousBodiesPerSiteThroughProjectionsLocalsAndAliases()
    {
        const string source = """
            using R = Microsoft.AspNetCore.Http.Results;
            app.MapGet("/a", () => Results.Ok(new { id = 1 }));
            app.MapGet("/b", () => Results.Ok(list.Select(s => new { s.Id })));
            app.MapGet("/c", () =>
            {
                var body = new { error = "x" };
                return TypedResults.BadRequest(body);
            });
            app.MapGet("/d", () => R.Json(new { a = 1 }));
            app.MapGet("/e", () => flag ? Results.NotFound(new { a = 1 }) : Results.Conflict(new { b = 2 }));
            app.MapGet("/nested", () => Results.Ok(new { items = list.Select(s => new { s.Id }), extra = new { a = 1 } }));
            app.MapGet("/typed", () => TypedResults.Ok(new SourceResponse(1)));
            app.MapGet("/plain", () => Results.Ok(list));
            Log(new { ignored = true });
            """;
        Rules.AnonymousResponses("Program.cs", source).Should().BeEquivalentTo(
        [
            "Program.cs: GET /a: 1",
            "Program.cs: GET /b: 1",
            "Program.cs: GET /c: 1",
            "Program.cs: GET /d: 1",
            "Program.cs: GET /e: 2",
            "Program.cs: GET /nested: 3",
        ]);
    }

    [Fact]
    public void ShapeRule_FollowsUsingStaticResults()
    {
        const string source = """
            using static Microsoft.AspNetCore.Http.TypedResults;
            app.MapGet("/s", () => Ok(new { id = 1 }));
            app.MapGet("/local", () => Helper(new { id = 1 }));
            static object Helper(object o) => o;
            """;
        Rules.AnonymousResponses("Endpoints/S.cs", source).Should().BeEquivalentTo(["Endpoints/S.cs: GET /s: 1"]);
    }

    [Fact]
    public void ShapeRule_FollowsHelpersThatReturnAnonymousObjects()
    {
        const string source = """
            static class PlantedEndpoints
            {
                static void Map(IEndpointRouteBuilder app)
                {
                    app.MapPost("/test", (Result result) => Results.Ok(Report(result)));
                    app.MapPost("/test2", (Result result) => Results.Ok(PlantedEndpoints.Wrap(result)));
                    app.MapGet("/named", () => Results.Ok(Named()));
                }

                static object Report(Result r) => new { ok = r.Ok, message = r.Message };
                static object Wrap(Result r) { var body = Report(r); return body; }
                static SourceResponse Named() => new SourceResponse(1);
            }
            """;
        Rules.AnonymousResponses("Endpoints/PlantedEndpoints.cs", source).Should().BeEquivalentTo(
        [
            "Endpoints/PlantedEndpoints.cs: POST /test: 1",
            "Endpoints/PlantedEndpoints.cs: POST /test2: 1",
        ]);
    }

    [Fact]
    public void ShapeRule_FollowsLocalsInsideConditionalArguments()
    {
        const string source = """
            app.MapGet("/c", (bool flag) =>
            {
                var body = new { id = 1 };
                return Results.Ok(flag ? body : null);
            });
            """;
        Rules.AnonymousResponses("Program.cs", source).Should().BeEquivalentTo(["Program.cs: GET /c: 1"]);
    }

    [Fact]
    public void ShapeRule_FollowsLocalsDeclaredInAnOuterBlock()
    {
        const string source = """
            app.MapGet("/n", (bool failed) =>
            {
                var body = new { error = "x" };
                if (failed)
                {
                    return Results.BadRequest(body);
                }
                return Results.Ok();
            });
            """;
        Rules.AnonymousResponses("Program.cs", source).Should().BeEquivalentTo(["Program.cs: GET /n: 1"]);
    }

    [Fact]
    public void ArchRule_CountsEnumerableResolutionAsServiceLocation()
    {
        const string source = """
            app.MapGet("/all", (IServiceProvider sp) => sp.GetServices<IFoo>().Count());
            """;
        Rules.ServiceLocationCalls("Mcp/Planted.cs", source).Should().Contain("Mcp/Planted.cs: GET /all uses GetServices");
    }

    [Fact]
    public void ShapeRule_FollowsLocalAliasChainsOfAnyLength()
    {
        const string source = """
            app.MapGet("/chain", () =>
            {
                var a = new { id = 1 };
                var b = a; var c = b; var d = c; var e = d; var f = e;
                return Results.Ok(f);
            });
            app.MapGet("/cycle", () =>
            {
                object x = null; object y = x; x = y;
                return Results.Ok(x);
            });
            app.MapGet("/twice", () => Results.Ok(new { a = Describe(1), b = Describe(2) }));
            static object Describe(int n) => new { n };
            """;
        Rules.AnonymousResponses("Program.cs", source).Should().BeEquivalentTo(["Program.cs: GET /chain: 1", "Program.cs: GET /twice: 3"]);
    }

    [Fact]
    public void ShapeRule_CountsAnonymousObjectsAHandlerReturnsDirectly()
    {
        const string source = """
            app.MapGet("/direct", () => new { id = 1 });
            app.MapGet("/block", (bool f) => { if (f) return Results.Ok(new { a = 1 }); return new { b = 2 }; });
            app.MapGet("/group", Handler);
            app.MapGet("/qualified", SummaryEndpoints.Get);
            static class SummaryEndpoints { public static object Get() => new { id = 1 }; }
            static object Handler() => new { id = 1 };
            """;
        Rules.AnonymousResponses("Program.cs", source).Should().BeEquivalentTo(
            ["Program.cs: GET /direct: 1", "Program.cs: GET /block: 2", "Program.cs: GET /group: 1", "Program.cs: GET /qualified: 1"]);
    }

    [Fact]
    public void ShapeRule_FollowsOnlyTheHelperOverloadThatFitsTheCall()
    {
        const string source = """
            app.MapGet("/o", () => Results.Ok(Shape(1)));
            static object Shape(int n) => new { id = n };
            static object Shape(string s, int n) => new { id = s, n };
            """;
        Rules.AnonymousResponses("Program.cs", source).Should().BeEquivalentTo(["Program.cs: GET /o: 1"]);
    }

    [Fact]
    public void ShapeRule_LooksUpAQualifiedHandlerInItsOwnClass()
    {
        const string source = """
            app.MapGet("/named", LinkAppEndpoints.Get);
            static class LinkAppEndpoints { public static LinkApp Get() => new LinkApp(); }
            static class SiteEndpoints { public static object Get() => new { id = 1 }; }
            """;
        Rules.AnonymousResponses("Program.cs", source).Should().BeEmpty();
    }

    [Fact]
    public void ArchRule_CountsServiceLocationInMethodGroupHandlersOutsideSurfaceFiles()
    {
        const string source = """
            app.MapGet("/x", Handle);
            static IResult Handle(HttpContext ctx) => Results.Ok(ctx.RequestServices.GetRequiredService<TimeProvider>());
            """;
        Rules.ServiceLocationCalls("Program.cs", source, wholeFile: false).Should()
            .Contain(e => e.StartsWith("Program.cs: GET /x uses ", StringComparison.Ordinal));
    }

    [Fact]
    public void Rules_FollowHandlersMappedFromAnotherFile()
    {
        const string program = """app.MapGet("/x", HealthHandler.Handle);""";
        const string handler = """
            static class HealthHandler
            {
                public static object Handle(HttpContext ctx)
                {
                    var foo = ctx.RequestServices.GetRequiredService<IFoo>();
                    return new { id = 1 };
                }
            }
            """;
        var routes = SourceScan.QualifiedRoutes([program, handler]);

        Rules.ServiceLocationCalls("Services/HealthHandler.cs", handler, wholeFile: false, routes).Should()
            .Contain(e => e.StartsWith("Services/HealthHandler.cs: GET /x uses ", StringComparison.Ordinal));
        Rules.AnonymousResponses("Services/HealthHandler.cs", handler, routes).Should().Equal("Services/HealthHandler.cs: GET /x: 1");
    }

    [Fact]
    public void ShapeRule_KeepsSameNamedHandlersOfDifferentClassesApart()
    {
        const string program = """app.MapGet("/a", A.Handle);""";
        const string handlers = """
            static class A { public static object Handle() => new { id = 1 }; }
            static class B
            {
                public static void Map(IEndpointRouteBuilder app) => app.MapGet("/b", Handle);
                static Named Handle() => new Named();
            }
            """;
        var routes = SourceScan.QualifiedRoutes([program, handlers]);

        Rules.AnonymousResponses("Endpoints/Handlers.cs", handlers, routes).Should().Equal("Endpoints/Handlers.cs: GET /a: 1");
    }

    [Fact]
    public void ShapeRule_ResolvesATopLevelHandlerToTheTopLevelFunction()
    {
        const string source = """
            app.MapGet("/x", Handle);
            static Named Handle() => new Named();
            static class Other { public static object Handle() => new { id = 1 }; }
            """;
        Rules.AnonymousResponses("Program.cs", source).Should().BeEmpty();
    }
}
