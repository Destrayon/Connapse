using Connapse.Application;
using Connapse.Architecture.Tests.Canaries;
using FluentAssertions;

namespace Connapse.Architecture.Tests;

/// <summary>
/// CON-282: every shared action is reachable from the web API. Operations are where the API and the MCP tools share
/// code, and nothing checked that each one also got a route; fact memory had MCP tools and no route for months.
/// </summary>
[Trait("Category", "Unit")]
public class OperationRouteTests
{
    /// <summary>Operations deliberately left without a web route, each with the reason.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Exempt = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["approvals.list"] = "UI only by design (#782): a token must never approve the MCP call it is waiting on, so every non-UI caller is refused.",
        ["approvals.decide"] = "UI only by design (#782): a token must never approve the MCP call it is waiting on, so every non-UI caller is refused.",
        ["search.knowledge"] = "Routed as search.container and search.source, which are this operation narrowed to a container or a source id.",
    };

    [Fact]
    public void EveryOperation_HasAWebRoute_UnlessExempt()
    {
        Rules.UnroutedOperations(typeof(OperationAttribute).Assembly.GetTypes(), RepoPaths.WebCSharpFiles(), Exempt).Should().Equal([],
            "a script reaches Connapse through the web API, so an action without a route is one only the AI tools or pages can run; "
            + "add the route, or an exemption here saying why it has none");
    }

    [Fact]
    public void ArchRule_DetectsAnOperationNoRouteReaches()
    {
        const string source = """
            static class PlantedRoutes
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    var group = app.MapGroup("/api/planted");
                    group.MapGet("/", async ([FromServices] PlantedCompleteOperation op, CancellationToken ct) => Results.Ok());
                    group.MapPost("/", PostAsync);
                }

                private static Task<IResult> PostAsync(HttpContext http, PlantedSharedIdA op) => Helper(op);

                // A helper the handler calls counts as the handler: planted.caller is routed through it.
                private static Task<IResult> Helper(PlantedSharedIdA op, PlantedCallingOperation caller = null) => Task.FromResult(Results.Ok());
            }

            public partial class PlantedTools
            {
                [McpServerTool(Name = "planted_tool")]
                public static string Tool(PlantedOperationWithFailures op) => "";
            }
            """;
        Type[] operations =
        [
            typeof(PlantedCompleteOperation), typeof(PlantedSharedIdA), typeof(PlantedOperationWithFailures),
            typeof(PlantedOperationWithoutScope), typeof(PlantedOperationWithoutRisk), typeof(PlantedAbstractOperation),
            typeof(PlantedCallingOperation), typeof(PlantedCalledOperation),
        ];
        Dictionary<string, string> exempt = new()
        {
            ["planted.norisk"] = "only the canary needs it",
            ["planted.noscope"] = " ",
            ["planted.shared"] = "stale: the planted POST reaches it",
            ["planted.gone"] = "no operation has this id",
        };

        Rules.UnroutedOperations(operations, [("Planted.cs", source)], exempt).Should().Equal(
            // planted.called is reported though the routed planted.caller calls it: an operation running inside another
            // isn't one a script can run. planted.failures: only an MCP tool takes it, which doesn't count.
            "planted.called: no web route",
            "planted.failures: no web route",
            "planted.noscope: exempt without a reason",
            "planted.shared: exempt, but a web route reaches it",
            "planted.gone: exempt, but no operation has this id");
    }

    [Fact]
    public void ArchRule_SeesTheRoutesItJudges()
    {
        // If the scan stopped finding routes, every operation would read as unrouted and the exemption list would be
        // the only thing failing; this pins that it still sees a known route's operation.
        Rules.UnroutedOperations([typeof(Connapse.Application.Memory.CopyMemoryFact)], RepoPaths.WebCSharpFiles(),
            new Dictionary<string, string>()).Should().BeEmpty();
    }
}
