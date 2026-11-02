using Connapse.Application;
using Connapse.Application.Invites;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

/// <summary>A pending invite as REST answers it. Its token is never part of it.</summary>
public sealed record InviteResponse(Guid Id, string Email, string Role, string? InvitedBy, DateTime CreatedAt, DateTime ExpiresAt)
{
    public static InviteResponse Of(Invite invite) =>
        new(invite.Id, invite.Email, invite.Role, invite.InvitedBy, invite.CreatedAt, invite.ExpiresAt);
}

/// <summary>A new invite and its token, answered only here: the link to send is <c>/register?token=</c> and the token.</summary>
public sealed record CreatedInviteResponse(Guid Id, string Email, string Role, DateTime CreatedAt, DateTime ExpiresAt, string Token);

public static class InviteEndpoints
{
    public static IEndpointRouteBuilder MapInviteEndpoints(this IEndpointRouteBuilder app)
    {
        // Invites are user management: the scope too, so an administrator's token issued for less is refused at the route.
        var group = app.MapGroup("/api/v1/invites").WithTags("Invites")
            .RequireAuthorization("RequireAdmin", "Scope:AdminUsers");

        // GET /api/v1/invites — pending invites, newest first (paginated)
        group.MapGet("/", async (
            [FromQuery] int? skip,
            [FromQuery] int? take,
            [FromServices] ListInvites list,
            HttpContext http,
            CancellationToken ct) =>
        {
            var listed = await list.RunAsync(Rest(http), new ListInvitesQuery(skip ?? 0, take ?? 50), ct);
            if (!listed.Succeeded)
                return OperationProblems.Result(listed);

            var page = listed.Result!;
            return Results.Ok(new PagedResponse<InviteResponse>(page.Items.Select(InviteResponse.Of).ToList(), page.TotalCount, page.HasMore));
        })
        .Produces<PagedResponse<InviteResponse>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .WithName("invites.list")
        .WithDescription("List pending invites with pagination (?skip=0&take=50) (Admin only). An invite's token is never listed.");

        // POST /api/v1/invites — invite someone; the answer carries the invite's token, this once
        group.MapPost("/", async (
            [FromBody] CreateInviteRequest request,
            [FromServices] CreateInvite create,
            HttpContext http,
            CancellationToken ct) =>
        {
            var created = await create.RunAsync(Rest(http), request, ct);
            if (!created.Succeeded)
                return OperationProblems.Result(created);

            var (invite, token) = created.Result!;
            return Results.Created((string?)null, new CreatedInviteResponse(invite.Id, invite.Email, invite.Role, invite.CreatedAt, invite.ExpiresAt, token));
        })
        .Produces<CreatedInviteResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("invites.create")
        .WithDescription("Invite someone with the Viewer, Editor or Admin role (Admin only; Admins only by the Owner). The token in the answer is the link's secret, /register?token=<token>, and is never answered again.");

        // DELETE /api/v1/invites/{id} — revoke a pending invite
        group.MapDelete("/{id:guid}", async (
            Guid id,
            [FromServices] RevokeInvite revoke,
            HttpContext http,
            CancellationToken ct) =>
        {
            var revoked = await revoke.RunAsync(Rest(http), id, ct);
            return revoked.Succeeded ? Results.NoContent() : OperationProblems.Result(revoked);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("invites.revoke")
        .WithDescription("Revoke a pending invite, so its link stops working (Admin only).");

        return app;
    }

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);
}
