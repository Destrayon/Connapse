using Connapse.Application;
using Connapse.Application.Approvals;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

/// <summary>
/// What became of a call held for a person's approval (CON-292), for an agent that works over REST. It admits whoever
/// may call MCP, where calls are held, and shows each caller only the requests its own token made.
/// </summary>
public static class ApprovalEndpoints
{
    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/approvals").WithTags("Approvals")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // GET /api/approvals/{id} - Where the caller's own approval request stands, optionally waiting for a decision
        group.MapGet("/{id:guid}", async (
            Guid id,
            [FromQuery] int? wait,
            [FromServices] GetMcpApprovalStatus status,
            HttpContext http,
            CancellationToken ct) =>
        {
            int seconds = Math.Clamp(wait ?? 0, 0, (int)GetMcpApprovalStatus.MaxWait.TotalSeconds);
            var found = await status.ExecuteAsync(new OperationContext(http.User, OperationSurface.Rest), id, TimeSpan.FromSeconds(seconds), ct);
            return found.Succeeded ? Results.Ok(found.Result) : OperationProblems.Result(found);
        })
        .Produces<McpApprovalRequestStatus>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("approvals.status")
        .WithDescription("Where an approval request your token made stands: pending, approved, denied (with the person's reason), expired or used, "
            + "and what to do next. ?wait=N (0-50) waits up to N seconds for a decision while it is pending. When approved, repeat the held call "
            + "exactly and it runs once. Another token's request reads as not found.")
        .RequireAuthorization("RequireAgent");

        // POST /api/approvals/deletions - Ask the token's owner to approve several deletions at once (CON-315)
        group.MapPost("/deletions", async (
            [FromBody] RequestDeletionsBody body,
            [FromServices] RequestDeletions request,
            HttpContext http,
            CancellationToken ct) =>
        {
            var asked = await request.ExecuteAsync(new OperationContext(http.User, OperationSurface.Rest), body.Items, ct);
            return asked.Succeeded ? Results.Ok(asked.Result) : OperationProblems.Result(asked);
        })
        .Produces<DeletionBatchView>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .WithName("approvals.batches.request")
        .WithDescription("Ask your token's owner to approve up to 50 deletions in one request: each item is a file (kind file, container, target "
            + "= file id or path), a folder (kind folder, container, target = folder path) or a memory space (kind memory_space, container = the "
            + "space), with a reason the owner reads. Every item is checked now; if any can't be deleted, nothing is asked and the detail names "
            + "each bad item. The owner approves all, some or none on the Approvals page, and Connapse deletes the approved items itself, skipping "
            + "any that are gone or changed since the request. Read each item's outcome with GET /api/approvals/deletions/{id}.")
        // The scope too, so an Editor's knowledge:read token can't ask for deletions.
        .RequireAuthorization("RequireEditor", "Scope:KnowledgeWrite");

        // GET /api/approvals/deletions/{id} - Where the caller's own batch of deletions stands, optionally waiting
        group.MapGet("/deletions/{id:guid}", async (
            Guid id,
            [FromQuery] int? wait,
            [FromServices] GetDeletionBatchStatus status,
            HttpContext http,
            CancellationToken ct) =>
        {
            int seconds = Math.Clamp(wait ?? 0, 0, (int)GetMcpApprovalStatus.MaxWait.TotalSeconds);
            var found = await status.ExecuteAsync(new OperationContext(http.User, OperationSurface.Rest), id, TimeSpan.FromSeconds(seconds), ct);
            return found.Succeeded ? Results.Ok(found.Result) : OperationProblems.Result(found);
        })
        .Produces<DeletionBatchView>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("approvals.batches.status")
        .WithDescription("Where a batch of deletions your token asked for stands: pending, expired, denied (with the owner's reason), running or "
            + "done, and each item's outcome: deleted, skipped (gone or changed since the request), failed, not_approved or denied. ?wait=N (0-50) "
            + "waits up to N seconds while it is pending or running. Another token's batch reads as not found.")
        .RequireAuthorization("RequireAgent");

        return app;
    }
}
