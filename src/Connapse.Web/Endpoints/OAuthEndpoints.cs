// src/Connapse.Web/Endpoints/OAuthEndpoints.cs
using System.Security.Claims;
using Connapse.Core;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Web.Endpoints;

public static class OAuthEndpoints
{
    public static IEndpointRouteBuilder MapOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // -- Discovery --

        app.MapGet("/.well-known/oauth-protected-resource", (HttpContext ctx) =>
        {
            var baseUrl = GetBaseUrl(ctx);
            return Results.Json(new
            {
                resource = baseUrl,
                authorization_servers = new[] { baseUrl },
                scopes_supported = new[] { "knowledge:read", "knowledge:write" },
                bearer_methods_supported = new[] { "header" },
            });
        }).AllowAnonymous();

        app.MapGet("/.well-known/oauth-authorization-server", (HttpContext ctx) =>
        {
            var baseUrl = GetBaseUrl(ctx);
            return Results.Json(new
            {
                issuer = baseUrl,
                authorization_endpoint = $"{baseUrl}/oauth/authorize",
                token_endpoint = $"{baseUrl}/oauth/token",
                registration_endpoint = $"{baseUrl}/oauth/register",
                scopes_supported = new[] { "knowledge:read", "knowledge:write" },
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                code_challenge_methods_supported = new[] { "S256" },
                token_endpoint_auth_methods_supported = new[] { "none", "client_secret_post" },
                client_id_metadata_document_supported = true,
            });
        }).AllowAnonymous();

        // -- Token Endpoint --

        app.MapPost("/oauth/token", async (
            HttpContext ctx,
            [FromServices] OAuthAuthCodeService authCodeService,
            [FromServices] ITokenService tokenService,
            [FromServices] UserManager<ConnapseUser> userManager,
            [FromServices] ConnapseIdentityDbContext dbContext,
            CancellationToken ct) =>
        {
            var form = await ctx.Request.ReadFormAsync(ct);
            var grantType = form["grant_type"].ToString();

            return grantType switch
            {
                "authorization_code" => await HandleAuthorizationCodeGrant(
                    form, authCodeService, tokenService, userManager, dbContext, ct),
                "refresh_token" => await HandleRefreshTokenGrant(
                    form, tokenService, dbContext, ct),
                _ => Results.Json(new { error = "unsupported_grant_type" }, statusCode: 400),
            };
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitingExtensions.AuthPolicy)
        .DisableAntiforgery();

        // -- Dynamic Client Registration --

        app.MapPost("/oauth/register", async (
            [FromBody] ClientRegistrationRequest request,
            [FromServices] OAuthClientService clientService,
            CancellationToken ct) =>
        {
            try
            {
                var result = await clientService.RegisterAsync(
                    request.ClientName,
                    request.RedirectUris,
                    request.ApplicationType ?? "web",
                    ct);

                return Results.Json(new
                {
                    client_id = result.ClientId,
                    client_name = result.ClientName,
                    redirect_uris = result.RedirectUris,
                    grant_types = new[] { "authorization_code", "refresh_token" },
                    application_type = result.ApplicationType,
                    token_endpoint_auth_method = "none",
                }, statusCode: 201);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = "invalid_client_metadata", error_description = ex.Message }, statusCode: 400);
            }
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitingExtensions.AuthPolicy)
        .DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> HandleAuthorizationCodeGrant(
        IFormCollection form,
        OAuthAuthCodeService authCodeService,
        ITokenService tokenService,
        UserManager<ConnapseUser> userManager,
        ConnapseIdentityDbContext dbContext,
        CancellationToken ct)
    {
        var code = form["code"].ToString();
        var redirectUri = form["redirect_uri"].ToString();
        var clientId = form["client_id"].ToString();
        var codeVerifier = form["code_verifier"].ToString();

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(redirectUri) ||
            string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(codeVerifier))
        {
            return Results.Json(new { error = "invalid_request" }, statusCode: 400);
        }

        var exchangeResult = await authCodeService.ExchangeAsync(code, codeVerifier, redirectUri, clientId, ct);
        if (exchangeResult is null)
            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);

        var user = await userManager.FindByIdAsync(exchangeResult.UserId.ToString());
        if (user is null)
            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);

        var roles = await userManager.GetRolesAsync(user);
        var claims = BuildClaims(user, roles, exchangeResult.Scope, clientId);
        var tokenResponse = await tokenService.GenerateTokenPairAsync(claims, user.Id, ct);

        // Tag the refresh token with the client_id
        var refreshTokenHash = ComputeSha256Hex(tokenResponse.RefreshToken);
        var refreshEntity = await dbContext.RefreshTokens
            .FirstOrDefaultAsync(r => r.TokenHash == refreshTokenHash, ct);
        if (refreshEntity is not null)
        {
            refreshEntity.ClientId = clientId;
            await dbContext.SaveChangesAsync(ct);
        }

        return Results.Json(new
        {
            access_token = tokenResponse.AccessToken,
            token_type = "bearer",
            expires_in = 3600,
            refresh_token = tokenResponse.RefreshToken,
            scope = exchangeResult.Scope,
        });
    }

    private static async Task<IResult> HandleRefreshTokenGrant(
        IFormCollection form,
        ITokenService tokenService,
        ConnapseIdentityDbContext dbContext,
        CancellationToken ct)
    {
        var refreshToken = form["refresh_token"].ToString();
        var clientId = form["client_id"].ToString();
        if (string.IsNullOrEmpty(refreshToken))
            return Results.Json(new { error = "invalid_request" }, statusCode: 400);

        // Look up the old refresh token to validate client_id and propagate it
        var oldTokenHash = ComputeSha256Hex(refreshToken);
        var oldRefreshEntity = await dbContext.RefreshTokens
            .FirstOrDefaultAsync(r => r.TokenHash == oldTokenHash, ct);
        if (oldRefreshEntity?.ClientId is not null &&
            !string.Equals(oldRefreshEntity.ClientId, clientId, StringComparison.Ordinal))
        {
            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
        }

        var tokenResponse = await tokenService.RefreshTokenAsync(refreshToken, ct);
        if (tokenResponse is null)
            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);

        // Tag the new refresh token with client_id
        if (!string.IsNullOrEmpty(clientId))
        {
            var newTokenHash = ComputeSha256Hex(tokenResponse.RefreshToken);
            var newRefreshEntity = await dbContext.RefreshTokens
                .FirstOrDefaultAsync(r => r.TokenHash == newTokenHash, ct);
            if (newRefreshEntity is not null)
            {
                newRefreshEntity.ClientId = clientId;
                await dbContext.SaveChangesAsync(ct);
            }
        }

        return Results.Json(new
        {
            access_token = tokenResponse.AccessToken,
            token_type = "bearer",
            expires_in = 3600,
            refresh_token = tokenResponse.RefreshToken,
        });
    }

    private static IEnumerable<Claim> BuildClaims(
        ConnapseUser user, IList<string> roles, string scope, string clientId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName ?? user.Email ?? ""),
            new(ClaimTypes.Email, user.Email ?? ""),
            new("client_id", clientId),
        };

        foreach (var role in roles)
            claims.Add(new(ClaimTypes.Role, role));

        foreach (var s in scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            claims.Add(new("scope", s));

        return claims;
    }

    private static string GetBaseUrl(HttpContext ctx)
    {
        var request = ctx.Request;
        return $"{request.Scheme}://{request.Host}";
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}

public record ClientRegistrationRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("client_name")] string ClientName,
    [property: System.Text.Json.Serialization.JsonPropertyName("redirect_uris")] List<string> RedirectUris,
    [property: System.Text.Json.Serialization.JsonPropertyName("application_type")] string? ApplicationType,
    [property: System.Text.Json.Serialization.JsonPropertyName("grant_types")] List<string>? GrantTypes = null);
