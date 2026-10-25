using Connapse.Core;
using Connapse.Core.Interfaces;
using Hangfire;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public static class BatchesEndpoints
{
    public static IEndpointRouteBuilder MapBatchesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/batches").WithTags("Batches")
            .RequireAuthorization("RequireViewer");

        // GET /api/batches/{id}/status - Get the progress of an upload's ingestion job
        group.MapGet("/{id}/status", async (
            string id,
            [FromServices] JobStorage jobStorage,
            [FromServices] IDocumentStore documentStore,
            CancellationToken ct) =>
        {
            // The id is the job id an upload returned. The job names its document; the document
            // row holds the status, which outlives the job and any retries that replaced it.
            string? documentId = JobDocumentId(jobStorage, id);
            Document? document = documentId is null ? null : await documentStore.GetAsync(documentId, ct);
            if (document is null)
                return Results.NotFound(new { error = $"Batch {id} not found" });

            return Results.Ok(new
            {
                JobId = id,
                DocumentId = document.Id,
                ContainerId = document.ContainerId,
                State = document.Status switch
                {
                    DocumentStatus.Queued => "Queued",
                    DocumentStatus.Processing => "Processing",
                    DocumentStatus.Ready => "Completed",
                    _ => "Failed",
                },
                PercentComplete = document.Status == DocumentStatus.Ready ? 100 : 0,
                ErrorMessage = document.Metadata.GetValueOrDefault("ErrorMessage"),
            });
        })
        .WithName("GetBatchStatus")
        .WithDescription("Get the status of a batch upload operation");

        return app;
    }

    /// <summary>
    /// The document an upload's job names, or null when there is no such job. The job storage refuses an
    /// id it couldn't have issued, such as a word or a number too large for the PostgreSQL storage's, with a
    /// FormatException or an OverflowException.
    /// </summary>
    private static string? JobDocumentId(JobStorage jobStorage, string id)
    {
        try
        {
            return jobStorage.GetMonitoringApi().JobDetails(id)?.Job?.Args.FirstOrDefault() as string;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return null;
        }
    }
}
