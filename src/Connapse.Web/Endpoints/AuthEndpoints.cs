using System.Security.Claims;
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
            // Reject OAuth-issued refresh tokens — they must go through /oauth/token
            var tokenHash = OAuthEndpoints.ComputeSha256Hex(request.RefreshToken);
            var existingToken = await dbContext.RefreshTokens
                .FirstOrDefaultAsync(r => r.TokenHash == tokenHash, ct);
            if (existingToken?.ClientId is not null)
                return Results.Json(new { error = "OAuth tokens must use /oauth/token endpoint" },
                    statusCode: StatusCodes.Status400BadRequest);

            var tokenResponse = await tokenService.RefreshTokenAsync(request.RefreshToken, ct);
            if (tokenResponse is null)
                return Results.Json(new { error = "Invalid or expired refresh token" },
                    statusCode: StatusCodes.Status401Unauthorized);

            return Results.Ok(tokenResponse);
        })
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
        .WithName("GetMe")
        .WithDescription("Return the authenticated caller's identity (id, email, display name, roles).")
        .RequireAuthorization();

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
                return Results.NotFound(new { error = "PAT not found or already revoked" });

            await auditLogger.LogAsync("pat.revoked", "pat", id.ToString(), null, ct);
            return Results.NoContent();
        })
        .WithName("RevokePat")
        .WithDescription("Revoke a personal access token by ID")
        .RequireAuthorization();

        // GET /api/v1/auth/users — list users (Admin only, paginated)
        group.MapGet("/users", async (
            [FromQuery] int? skip,
            [FromQuery] int? take,
            [FromServices] ConnapseIdentityDbContext dbContext,
            CancellationToken ct) =>
        {
            var effectiveSkip = skip ?? 0;
            var effectiveTake = take ?? 50;
            var validationError = PaginationValidator.Validate(effectiveSkip, effectiveTake);
            if (validationError is not null) return validationError;

            var totalCount = await dbContext.Users.CountAsync(ct);

            var users = await dbContext.Users
                .OrderBy(u => u.CreatedAt)
                .Skip(effectiveSkip)
                .Take(effectiveTake)
                .ToListAsync(ct);

            var userIds = users.Select(u => u.Id).ToList();

            // Batch-load role mappings only for this page of users
            var rolesByUserId = await dbContext.UserRoles
                .Where(ur => userIds.Contains(ur.UserId))
                .Join(
                    dbContext.Roles,
                    ur => ur.RoleId,
                    r => r.Id,
                    (ur, r) => new { ur.UserId, RoleName = r.Name! })
                .ToListAsync(ct);

            var roleLookup = rolesByUserId
                .GroupBy(x => x.UserId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.RoleName).ToList());

            var items = users.Select(u => new UserListItem(
                u.Id,
                u.Email ?? "",
                u.DisplayName,
                roleLookup.GetValueOrDefault(u.Id, []),
                u.CreatedAt,
                u.LastLoginAt)).ToList();

            return Results.Ok(new PagedResponse<UserListItem>(items, totalCount, effectiveSkip + effectiveTake < totalCount));
        })
        .WithName("ListUsers")
        .WithDescription("List users with pagination (?skip=0&take=50) (Admin only)")
        .RequireAuthorization("RequireAdmin");

        // PUT /api/v1/auth/users/{id}/roles — assign roles to a user (Admin only)
        group.MapPut("/users/{id:guid}/roles", async (
            Guid id,
            [FromBody] AssignRolesRequest request,
            [FromServices] UserManager<ConnapseUser> userManager,
            [FromServices] IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            var targetUser = await userManager.FindByIdAsync(id.ToString());
            if (targetUser is null)
                return Results.NotFound(new { error = "User not found" });

            // Owner and Agent roles cannot be assigned via this endpoint
            var reservedRoles = new[] { "Owner", "Agent" };
            var blocked = request.Roles.FirstOrDefault(r =>
                reservedRoles.Contains(r, StringComparer.OrdinalIgnoreCase));
            if (blocked is not null)
                return Results.BadRequest(new { error = $"The '{blocked}' role cannot be assigned via this endpoint." });

            var currentRoles = await userManager.GetRolesAsync(targetUser);

            // Never remove the Owner role — preserve it regardless of requested roles
            var ownerRoles = currentRoles
                .Where(r => r.Equals("Owner", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var rolesToRemove = currentRoles
                .Except(ownerRoles, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (rolesToRemove.Count > 0)
                await userManager.RemoveFromRolesAsync(targetUser, rolesToRemove);

            var rolesToAdd = request.Roles
                .Where(r => !currentRoles.Contains(r, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (rolesToAdd.Count > 0)
            {
                var addResult = await userManager.AddToRolesAsync(targetUser, rolesToAdd);
                if (!addResult.Succeeded)
                    return Results.BadRequest(new { errors = addResult.Errors.Select(e => e.Description) });
            }

            await auditLogger.LogAsync("user.roles.updated", "user", id.ToString(),
                new { Roles = request.Roles }, ct);

            return Results.NoContent();
        })
        .WithName("AssignUserRoles")
        .WithDescription("Assign roles to a user (Admin only)")
        .RequireAuthorization("RequireAdmin");

        return app;
    }

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
