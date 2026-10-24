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
            [FromQuery] int? skip,
            [FromQuery] int? take,
            [FromServices] ListAuditLog list,
            HttpContext http,
            CancellationToken ct) =>
        {
            var read = await list.RunAsync(new OperationContext(http.User, OperationSurface.Rest),
                new AuditQuery(action, userId, resourceType, resourceId, since, until, skip ?? 0, take ?? 50), ct);
            if (!read.Succeeded)
                return OperationProblems.Result(read);
            var page = read.Result!;
            return Results.Ok(new PagedResponse<AuditEntry>(page.Items, page.Items.Count, page.HasMore));
        })
        .WithName("ListAuditLog")
        .WithDescription("Read the audit log, newest first (?skip=0&take=50, up to 200). Filter by action, userId, resourceType, resourceId, and a since (inclusive) to until (exclusive) range in UTC. Administrators only.")
        .RequireAuthorization(Policies.AuditReader);

        return app;
    }
}
