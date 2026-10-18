using Connapse.Architecture.Tests.Canaries;
using FluentAssertions;

namespace Connapse.Architecture.Tests;

/// <summary>Proves the service-location and response-shape rules still report planted violations.</summary>
[Trait("Category", "Unit")]
public class LocationAndShapeCanaryTests
{
    private static readonly string[] CanaryNamespace = [typeof(PlantedEndpoints).Namespace!];
    private static System.Reflection.Assembly TestAssembly => typeof(PlantedEndpoints).Assembly;

    [Fact]
    public void ArchRule_DetectsPlantedServiceLocators()
    {
        Rules.ServiceLocatorDependencies(TestAssembly, CanaryNamespace).Should().Contain(
        [
            "Connapse.Architecture.Tests.Canaries.PlantedComponent -> IServiceScopeFactory",
            "PlantedEndpoints -> IServiceProvider",
        ]);
    }

    [Fact]
    public void ArchRule_DetectsInlineServiceLocation()
    {
        const string source = """
            app.MapGet("/x", (HttpContext http) => http.RequestServices.GetRequiredService<IFoo>());
            using var scope = factory.CreateScope();
            """;
        Rules.ServiceLocationCalls("Endpoints/Planted.cs", source).Should().BeEquivalentTo(
        [
            "Endpoints/Planted.cs uses RequestServices",
            "Endpoints/Planted.cs uses GetRequiredService",
            "Endpoints/Planted.cs uses CreateScope",
        ]);
    }

    [Fact]
    public void ShapeRule_CountsOnlyAnonymousResultBodies()
    {
        const string source = """
            return Results.Ok(new { id = 1 });
            return Results.NotFound(new { error = "missing" });
            return Microsoft.AspNetCore.Http.Results.Json(new { a = 1 });
            return TypedResults.BadRequest(new { error = "bad" });
            return TypedResults.Ok(new SourceResponse(1));
            return Results.Ok(list);
            Log(new { ignored = true });
            """;
        SourceScan.CountAnonymousResponses(source).Should().Be(4);
    }
}
