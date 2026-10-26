using Connapse.Application;
using Connapse.Application.Files;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public static class BatchesEndpoints
{
    public static IEndpointRouteBuilder MapBatchesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/batches").WithTags("Batches")
            .RequireAuthorization("RequireViewer")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // GET /api/batches/{id}/status - Get the progress of an upload's ingestion job
        group.MapGet("/{id}/status", async (
            HttpContext http,
            string id,
            [FromServices] GetBatchStatus status,
            CancellationToken ct) =>
        {
            var read = await status.RunAsync(new OperationContext(http.User, OperationSurface.Rest), id, ct);
            if (!read.Succeeded)
                return OperationProblems.Result(read);

            var batch = read.Result!;
            return Results.Ok(new BatchStatusResponse(
                batch.JobId, batch.DocumentId, batch.ContainerId, batch.State, batch.PercentComplete, batch.ErrorMessage));
        })
        .Produces<BatchStatusResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("GetBatchStatus")
        .WithDescription("Get the status of a batch upload operation");

        return app;
    }
}

public record BatchStatusResponse(
    string JobId,
    string DocumentId,
    string ContainerId,
    string State,
    int PercentComplete,
    string? ErrorMessage);
