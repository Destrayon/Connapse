using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Links;
using Connapse.Application.Ports;
using Connapse.Application.Users;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Connapse.Web;
using Connapse.Web.Problems;

namespace Connapse.Web.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");

        // POST /api/v1/auth/token — email+password → JWT access token + refresh token
        group.MapPost("/token", async (
            [FromBody] LoginRequest request,
            [FromServices] UserManager<ConnapseUser> userManager,
            [FromServices] SignInManager<ConnapseUser> signInManager,
            [FromServices] ITokenService tokenService,
            [FromServices] IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "email and password are required.");

            var user = await userManager.FindByEmailAsync(request.Email);
            if (user is null)
                return Results.Unauthorized();

            var signInResult = await signInManager.CheckPasswordSignInAsync(
                user, request.Password, lockoutOnFailure: false);

            if (!signInResult.Succeeded)
            {
                await auditLogger.LogAsync("auth.token.failed", "user", user.Id.ToString(),
                    new { request.Email }, ct);
                return Results.Unauthorized();
            }

            var roles = await userManager.GetRolesAsync(user);
            var claims = BuildClaims(user, roles);
            var tokenResponse = await tokenService.GenerateTokenPairAsync(claims, user.Id, cancellationToken: ct);

            user.LastLoginAt = DateTime.UtcNow;
            await userManager.UpdateAsync(user);

            await auditLogger.LogAsync("auth.token.issued", "user", user.Id.ToString(), null, ct);

            return Results.Ok(tokenResponse);
        })
        .Produces<TokenResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .WithName("GetToken")
        .WithDescription("Exchange email/password for a JWT access token and refresh token")
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitingExtensions.AuthPolicy);

        // POST /api/v1/auth/token/refresh — rotate refresh token → new token pair
        // OAuth-issued refresh tokens (tagged with client_id) must use /oauth/token instead
        group.MapPost("/token/refresh", async (
            [FromBody] RefreshTokenRequest request,
            [FromServices] ITokenService tokenService,
            [FromServices] ConnapseIdentityDbContext dbContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(request.RefreshToken))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "refreshToken is required.");

            // Reject OAuth-issued refresh tokens — they must go through /oauth/token
            var tokenHash = OAuthEndpoints.ComputeSha256Hex(request.RefreshToken);
            var existingToken = await dbContext.RefreshTokens
                .FirstOrDefaultAsync(r => r.TokenHash == tokenHash, ct);
            if (existingToken?.ClientId is not null)
                return ApiProblem.BadRequest("OAuth tokens must use /oauth/token endpoint");

            var tokenResponse = await tokenService.RefreshTokenAsync(request.RefreshToken, cancellationToken: ct);
            if (tokenResponse is null)
                return ApiProblem.Unauthorized("Invalid or expired refresh token");

            return Results.Ok(tokenResponse);
        })
        .Produces<TokenResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .WithName("RefreshToken")
        .WithDescription("Exchange a refresh token for a new JWT access token and refresh token")
        .AllowAnonymous();

        // GET /api/v1/auth/me — return identity for the currently authenticated caller
        group.MapGet("/me", async (
            HttpContext httpContext,
            [FromServices] UserManager<ConnapseUser> userManager,
            CancellationToken ct) =>
        {
            var userId = GetUserId(httpContext);
            if (userId is null)
                return Results.Unauthorized();

            var user = await userManager.FindByIdAsync(userId.Value.ToString());
            if (user is null)
                return Results.Unauthorized();

            var roles = await userManager.GetRolesAsync(user);

            return Results.Ok(new MeResponse(
                user.Id,
                user.Email ?? "",
                user.DisplayName,
                roles.ToList(),
                user.CreatedAt));
        })
        .Produces<MeResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .WithName("GetMe")
        .WithDescription("Return the authenticated caller's identity (id, email, display name, roles).")
        .RequireAuthorization();

        // GET /api/v1/auth/links — the caller's linked accounts, so an agent can say why search hides a document (#865)
        group.MapGet("/links", async (
            HttpContext http,
            [FromServices] ListAccountLinks list,
            CancellationToken ct) =>
        {
            var read = await list.RunAsync(new OperationContext(http.User, OperationSurface.Rest), new ListAccountLinksQuery(), ct);
            return read.Succeeded
                ? Results.Ok(AccountLinksResponse.From(read.Result!))
                : OperationProblems.Result(read);
        })
        .Produces<AccountLinksResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .WithName("ListAccountLinks")
        .WithDescription("List the caller's linked accounts (AWS, Entra ID, GitHub, Atlassian, custom providers): whether each is linked, as whom, since when, and whether this server offers linking. Search hides documents that only an unlinked account may read. Linking itself happens in a browser at /profile/integrations.")
        .RequireAuthorization(Policies.Account);

        // GET /api/v1/auth/pats — list the authenticated user's PATs
        group.MapGet("/pats", async (
            HttpContext httpContext,
            [FromServices] PatService patService,
            CancellationToken ct) =>
        {
            var userId = GetUserId(httpContext);
            if (userId is null)
                return Results.Unauthorized();

            var pats = await patService.ListAsync(userId.Value, ct);
            return Results.Ok(pats);
        })
        .Produces<IReadOnlyList<PatListItem>>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .WithName("ListPats")
        .WithDescription("List personal access tokens for the authenticated user")
        .RequireAuthorization();

        // POST /api/v1/auth/pats — create a PAT (token shown only once)
        group.MapPost("/pats", async (
            [FromBody] PatCreateRequest request,
            HttpContext httpContext,
            [FromServices] PatService patService,
            [FromServices] IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            var userId = GetUserId(httpContext);
            if (userId is null)
                return Results.Unauthorized();

            var pat = await patService.CreateAsync(userId.Value, request, ct);
            await auditLogger.LogAsync("pat.created", "pat", pat.Id.ToString(),
                new { pat.Name, pat.Scopes }, ct);

            return Results.Ok(pat);
        })
        .Produces<PatCreateResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .WithName("CreatePat")
        .WithDescription("Create a personal access token (the raw token is returned only on creation)")
        .RequireAuthorization();

        // DELETE /api/v1/auth/pats/{id} — revoke a PAT
        group.MapDelete("/pats/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] PatService patService,
            [FromServices] IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            var userId = GetUserId(httpContext);
            if (userId is null)
                return Results.Unauthorized();

            var revoked = await patService.RevokeAsync(userId.Value, id, ct);
            if (!revoked)
                return ApiProblem.NotFound("PAT not found or already revoked");

            await auditLogger.LogAsync("pat.revoked", "pat", id.ToString(), null, ct);
            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("RevokePat")
        .WithDescription("Revoke a personal access token by ID")
        .RequireAuthorization();

        // GET /api/v1/auth/users — list users (Admin only, paginated)
        group.MapGet("/users", async (
            [FromQuery] int? skip,
            [FromQuery] int? take,
            [FromServices] ListUsers list,
            HttpContext http,
            CancellationToken ct) =>
        {
            var listed = await list.RunAsync(Rest(http), new ListUsersQuery(skip ?? 0, take ?? 50), ct);
            if (!listed.Succeeded)
                return OperationProblems.Result(listed);

            var page = listed.Result!;
            var items = page.Items.Select(u => new UserListItem(u.Id, u.Email, u.DisplayName, u.Roles, u.CreatedAt, u.LastLoginAt)).ToList();
            return Results.Ok(new PagedResponse<UserListItem>(items, page.TotalCount, page.HasMore));
        })
        .Produces<PagedResponse<UserListItem>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .WithName("users.list")
        .WithDescription("List users with pagination (?skip=0&take=50) (Admin only)")
        .RequireAuthorization("RequireAdmin");

        // PUT /api/v1/auth/users/{id}/roles — replace a user's roles (Admin only; Admins only by the Owner)
        group.MapPut("/users/{id:guid}/roles", async (
            Guid id,
            [FromBody] AssignRolesRequest request,
            [FromServices] SetUserRoles setRoles,
            HttpContext http,
            CancellationToken ct) =>
        {
            var set = await setRoles.RunAsync(Rest(http), new SetUserRolesRequest(id, request.Roles), ct);
            return set.Succeeded ? Results.NoContent() : OperationProblems.Result(set);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("users.setRoles")
        .WithDescription("Replace a user's roles (Admin only). Nobody changes their own account or the Owner's, only the Owner manages Admins, and Owner and Agent are never assigned.")
        .RequireAuthorization("RequireAdmin");

        // DELETE /api/v1/auth/users/{id} — delete a user (Admin only), queuing their custom-provider unlinks
        group.MapDelete("/users/{id:guid}", async (
            Guid id,
            [FromServices] DeleteUser delete,
            HttpContext http,
            CancellationToken ct) =>
        {
            var deleted = await delete.RunAsync(Rest(http), id, ct);
            return deleted.Succeeded ? Results.NoContent() : OperationProblems.Result(deleted);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status500InternalServerError)
        .WithName("users.delete")
        .WithDescription("Delete a user (Admin only). Nobody deletes their own account or the Owner's, and only the Owner deletes an Admin. Their custom-provider links are unlinked afterwards.")
        // The scope too: an administrator's token issued for less (knowledge:read) is refused here, before the operation.
        .RequireAuthorization("RequireAdmin", "Scope:AdminUsers");

        return app;
    }

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);

    private static Guid? GetUserId(HttpContext httpContext)
    {
        var idClaim = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idClaim, out var id) ? id : null;
    }

    private static IEnumerable<Claim> BuildClaims(ConnapseUser user, IList<string> roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName ?? user.Email ?? ""),
            new(ClaimTypes.Email, user.Email ?? ""),
        };

        foreach (var role in roles)
            claims.Add(new(ClaimTypes.Role, role));

        return claims;
    }
}
