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

        return app;
    }
}
