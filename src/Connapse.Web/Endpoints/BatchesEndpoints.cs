using Connapse.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public static class BatchesEndpoints
{
    public static IEndpointRouteBuilder MapBatchesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/batches").WithTags("Batches");

        // GET /api/batches/{id}/status - Get batch upload progress
        group.MapGet("/{id}/status", async (
            string id,
            [FromServices] IIngestionQueue queue,
            CancellationToken ct) =>
        {
            var status = await queue.GetStatusAsync(id);
            return status is not null
                ? Results.Ok(status)
                : Results.NotFound(new { error = $"Batch {id} not found" });
        })
        .WithName("GetBatchStatus")
        .WithDescription("Get the status of a batch upload operation");

        return app;
    }
}
