using Connapse.Application;
using Connapse.Application.Audit;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/audit").WithTags("Audit");

        // GET /api/v1/audit/ — the audit log, newest first, for administrators (#880)
        group.MapGet("/", async (
            [FromQuery] string? action,
            [FromQuery] Guid? userId,
            [FromQuery] string? resourceType,
            [FromQuery] string? resourceId,
            [FromQuery] DateTime? since,
            [FromQuery] DateTime? until,
            [FromQuery] string? before,
            [FromQuery] int? take,
            [FromServices] ListAuditLog list,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (before is not null && !AuditCursor.TryParse(before, out _))
                return OperationProblems.Result(AuditReadStatus.InvalidPage, "before must be a nextBefore value this endpoint returned");
            AuditCursor.TryParse(before, out var cursor);
            var read = await list.RunAsync(new OperationContext(http.User, OperationSurface.Rest),
                new AuditQuery(action, userId, resourceType, resourceId, since, until, cursor, take ?? 50), ct);
            if (!read.Succeeded)
                return OperationProblems.Result(read);
            var page = read.Result!;
            return Results.Ok(new AuditLogResponse(page.Items, page.Next is not null, page.Next?.ToString()));
        })
        .Produces<AuditLogResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .WithName("audit.list")
        .WithDescription("Read the audit log, newest first (?take=50, up to 200); pass a page's nextBefore as ?before= for the next. Filter by action, userId, resourceType, resourceId, and a since (inclusive) to until (exclusive) range in UTC. Administrators only.")
        .RequireAuthorization(Policies.AuditReader);

        return app;
    }
}

/// <summary>A page of the audit log, newest first. Pass <paramref name="NextBefore"/> as <c>?before=</c> for the next page.</summary>
public sealed record AuditLogResponse(IReadOnlyList<AuditEntry> Items, bool HasMore, string? NextBefore);
