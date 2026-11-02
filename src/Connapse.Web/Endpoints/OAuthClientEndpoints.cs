using Connapse.Application;
using Connapse.Application.OAuthClients;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public static class OAuthClientEndpoints
{
    public static IEndpointRouteBuilder MapOAuthClientEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/oauth-clients").WithTags("OAuth Clients")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // GET /api/v1/oauth-clients/ — the OAuth clients with access, for administrators (#881)
        group.MapGet("/", async ([FromServices] ListOAuthClients list, HttpContext http, CancellationToken ct) =>
        {
            var read = await list.RunAsync(new OperationContext(http.User, OperationSurface.Rest), ct);
            return read.Succeeded ? Results.Ok(read.Result) : OperationProblems.Result(read);
        })
        .Produces<IReadOnlyList<OAuthClientSummary>>()
        .WithName("oauthClients.list")
        .WithDescription("List the OAuth clients with access: those that registered themselves, then those known by a metadata URL that hold grants, with their active grant and user counts. Administrators only.")
        .RequireAuthorization(Policies.ClientAdmin);

        // DELETE /api/v1/oauth-clients/{clientId} — revoke a client (#881)
        group.MapDelete("/{clientId}", async (string clientId, [FromServices] RevokeOAuthClient revoke, HttpContext http, CancellationToken ct) =>
        {
            // A metadata-URL client id is sent escaped, and routing leaves its %2F escaped in the value.
            var revoked = await revoke.RunAsync(new OperationContext(http.User, OperationSurface.Rest), Uri.UnescapeDataString(clientId), ct);
            return revoked.Succeeded ? Results.Ok(revoked.Result) : OperationProblems.Result(revoked);
        })
        .Produces<OAuthClientRevocation>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("oauthClients.revoke")
        .WithDescription("Revoke an OAuth client: remove its registration, revoke its refresh tokens and delete its pending authorization codes. Access tokens it already holds expire within their short lifetime. Escape a metadata-URL client id. Administrators only.")
        .RequireAuthorization(Policies.ClientAdmin);

        return app;
    }
}
