using Connapse.Application.Search;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Problems;

namespace Connapse.Web.Endpoints;

/// <summary>Search can run, and this is how the local model stands.</summary>
public sealed record ReadinessReport(string State);

public static class ReadinessEndpoints
{
    public static IEndpointRouteBuilder MapReadinessEndpoints(this IEndpointRouteBuilder app)
    {
        // /health stays liveness: the app is up, and a slow download mustn't make it look broken.
        app.MapGet("/health/ready", (ILocalModelMonitor model) => Report(model.Current))
            .AllowAnonymous().RequireRateLimiting(RateLimitingExtensions.ApiPolicy)
            .Produces<ReadinessReport>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithName("health.ready");
        return app;
    }

    public static IResult Report(LocalModelStatus status)
    {
        if (!status.HoldsEmbedding)
            return Results.Ok(new ReadinessReport(status.State.ToString()));

        var problem = OperationProblems.For(SearchStatus.ModelNotReady, status.Explanation);
        problem.Extensions["state"] = status.State.ToString();
        if (status.Percent is { } percent)
            problem.Extensions["percent"] = percent;
        return Results.Problem(problem);
    }
}
