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

        // RFC 9728 §3.1: the Protected Resource Metadata discovery URL for a
        // resource whose identifier is "https://host/foo/bar" is
        // "https://host/.well-known/oauth-protected-resource/foo/bar". The
        // catch-all route below echoes whichever path the client used to
        // discover the metadata back into the "resource" claim, which RFC 9728
        // §3.3 requires to equal the protected resource's identifier. Strict
        // MCP clients (Claude Code among them) reject the document when this
        // doesn't match the URL they are trying to reach, which is why the
        // bare endpoint alone is not sufficient for multi-path deployments.
        app.MapGet("/.well-known/oauth-protected-resource", (HttpContext ctx) =>
            BuildProtectedResourceMetadata(ctx, resourcePath: null)).AllowAnonymous();

        app.MapGet("/.well-known/oauth-protected-resource/{**resourcePath}", (HttpContext ctx, string resourcePath) =>
            BuildProtectedResourceMetadata(ctx, resourcePath)).AllowAnonymous();

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

        // -- Static CLI Metadata Document --

        app.MapGet("/oauth/clients/cli.json", (HttpContext ctx) =>
        {
            var baseUrl = GetBaseUrl(ctx);
            return Results.Json(new
            {
                client_id = $"{baseUrl}/oauth/clients/cli.json",
                client_name = "Connapse CLI",
                redirect_uris = new[]
                {
                    "http://127.0.0.1/callback",
                },
                grant_types = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" },
                token_endpoint_auth_method = "none",
            });
        }).AllowAnonymous();

        // -- Token Endpoint --

        app.MapPost("/oauth/token", async (
            HttpContext ctx,
            [FromServices] OAuthAuthCodeService authCodeService,
            [FromServices] ITokenService tokenService,
            [FromServices] UserManager<ConnapseUser> userManager,
            [FromServices] ConnapseIdentityDbContext dbContext,
            [FromServices] ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var form = await ctx.Request.ReadFormAsync(ct);
            var grantType = form["grant_type"].ToString();

            return grantType switch
            {
                "authorization_code" => await HandleAuthorizationCodeGrant(
                    ctx, form, authCodeService, tokenService, userManager, dbContext, loggerFactory, ct),
                "refresh_token" => await HandleRefreshTokenGrant(
                    ctx, form, tokenService, dbContext, ct),
                _ => Results.Json(new { error = "unsupported_grant_type" }, statusCode: 400),
            };
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitingExtensions.AuthPolicy)
        .AddEndpointFilter(RequireFormContentType)
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
        HttpContext ctx,
        IFormCollection form,
        OAuthAuthCodeService authCodeService,
        ITokenService tokenService,
        UserManager<ConnapseUser> userManager,
        ConnapseIdentityDbContext dbContext,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("OAuth.Token");
        var code = form["code"].ToString();
        var redirectUri = form["redirect_uri"].ToString();
        var clientId = form["client_id"].ToString();
        var codeVerifier = form["code_verifier"].ToString();
        var resourceParam = form["resource"].ToString();

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(redirectUri) ||
            string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(codeVerifier))
        {
            return Results.Json(new { error = "invalid_request" }, statusCode: 400);
        }

        var exchangeResult = await authCodeService.ExchangeAsync(code, codeVerifier, redirectUri, clientId, ct);
        if (exchangeResult is null)
            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);

        // RFC 8707 §2 / MCP §Token Audience Binding: if the client included a
        // `resource` parameter at the token endpoint, it MUST match the value
        // it presented at the authorization endpoint. Mismatches mean the
        // client is requesting a token for a different audience than the user
        // consented to — reject as invalid_target.
        if (!string.IsNullOrWhiteSpace(resourceParam) &&
            !string.Equals(resourceParam, exchangeResult.Resource, StringComparison.Ordinal))
        {
            return Results.Json(new { error = "invalid_target" }, statusCode: 400);
        }

        // Bind the access token's `aud` claim to the resource the client asked
        // for so that MCP clients can verify the token was issued for their
        // intended resource. If no resource was provided (legacy / non-MCP
        // clients), fall back to the static server audience.
        var audience = exchangeResult.Resource;

        // RFC 9068 §2.2 / RFC 8414: the `iss` claim must equal the authorization
        // server's issuer identifier advertised in /.well-known/oauth-authorization-server.
        // That document computes `issuer` from the request's scheme+host, so we bind
        // the token's `iss` to the same value at mint time. Without this, spec-compliant
        // MCP clients silently discard the token because `iss` won't match AS metadata.
        var issuer = $"{ctx.Request.Scheme}://{ctx.Request.Host}";

        logger.LogInformation(
            "Token mint: resourceParam='{ResourceParam}' storedResource='{StoredResource}' audienceIssued='{Audience}' issuerIssued='{Issuer}'",
            string.IsNullOrEmpty(resourceParam) ? "<empty>" : resourceParam,
            exchangeResult.Resource ?? "<null>",
            audience ?? "<null-will-fall-back-to-static>",
            issuer);

        var user = await userManager.FindByIdAsync(exchangeResult.UserId.ToString());
        if (user is null)
            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);

        var roles = await userManager.GetRolesAsync(user);
        var claims = BuildClaims(user, roles, exchangeResult.Scope, clientId);
        var tokenResponse = await tokenService.GenerateTokenPairAsync(claims, user.Id, audience, issuer, ct);

        // Tag the refresh token with the client_id and the resource so refresh
        // cycles keep the same `aud` binding.
        var refreshTokenHash = ComputeSha256Hex(tokenResponse.RefreshToken);
        var refreshEntity = await dbContext.RefreshTokens
            .FirstOrDefaultAsync(r => r.TokenHash == refreshTokenHash, ct);
        if (refreshEntity is not null)
        {
            refreshEntity.ClientId = clientId;
            refreshEntity.Resource = exchangeResult.Resource;
            await dbContext.SaveChangesAsync(ct);
        }

        return Results.Json(new
        {
            access_token = tokenResponse.AccessToken,
            token_type = "bearer",
            expires_in = (int)(tokenResponse.ExpiresAt - DateTime.UtcNow).TotalSeconds,
            refresh_token = tokenResponse.RefreshToken,
            scope = exchangeResult.Scope,
        });
    }

    private static async Task<IResult> HandleRefreshTokenGrant(
        HttpContext ctx,
        IFormCollection form,
        ITokenService tokenService,
        ConnapseIdentityDbContext dbContext,
        CancellationToken ct)
    {
        var refreshToken = form["refresh_token"].ToString();
        var clientId = form["client_id"].ToString();
        var resourceParam = form["resource"].ToString();
        if (string.IsNullOrEmpty(refreshToken))
            return Results.Json(new { error = "invalid_request" }, statusCode: 400);

        // Look up the old refresh token to validate client_id/resource and propagate them
        var oldTokenHash = ComputeSha256Hex(refreshToken);
        var oldRefreshEntity = await dbContext.RefreshTokens
            .FirstOrDefaultAsync(r => r.TokenHash == oldTokenHash, ct);
        if (oldRefreshEntity?.ClientId is not null &&
            !string.Equals(oldRefreshEntity.ClientId, clientId, StringComparison.Ordinal))
        {
            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
        }

        // RFC 8707: an incoming `resource` at refresh time must match what the
        // original token was bound to. Clients may omit it to request the same
        // resource (common with MCP clients that only send it at authorize).
        // If the client sends a resource but the stored token isn't bound to
        // one (pre-migration or legacy /api flow), reject — otherwise we'd
        // silently issue a token with the static audience, which is exactly
        // the mismatch strict clients discard.
        if (!string.IsNullOrWhiteSpace(resourceParam) &&
            !string.Equals(resourceParam, oldRefreshEntity?.Resource, StringComparison.Ordinal))
        {
            return Results.Json(new { error = "invalid_target" }, statusCode: 400);
        }

        // Bind the refreshed access token's `iss` to the current request's
        // scheme+host (same rule as at initial mint) so it matches the AS
        // metadata document per RFC 9068 §2.2.
        var issuer = $"{ctx.Request.Scheme}://{ctx.Request.Host}";

        var tokenResponse = await tokenService.RefreshTokenAsync(refreshToken, issuer, ct);
        if (tokenResponse is null)
            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);

        // Tag the new refresh token with client_id (Resource is already
        // carried forward inside JwtTokenService.RefreshTokenAsync).
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
            expires_in = (int)(tokenResponse.ExpiresAt - DateTime.UtcNow).TotalSeconds,
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

    private static IResult BuildProtectedResourceMetadata(HttpContext ctx, string? resourcePath)
    {
        var baseUrl = GetBaseUrl(ctx);
        var normalizedPath = string.IsNullOrEmpty(resourcePath)
            ? string.Empty
            : "/" + resourcePath.TrimStart('/');
        var resource = baseUrl + normalizedPath;

        return Results.Json(new
        {
            resource,
            authorization_servers = new[] { baseUrl },
            scopes_supported = new[] { "knowledge:read", "knowledge:write" },
            bearer_methods_supported = new[] { "header" },
        });
    }

    // OAuth 2.1 requires application/x-www-form-urlencoded (RFC 6749 §3.2)
    private static async ValueTask<object?> RequireFormContentType(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!context.HttpContext.Request.HasFormContentType)
        {
            return Results.Json(new
            {
                error = "invalid_request",
                error_description = "Content-Type must be application/x-www-form-urlencoded",
            }, statusCode: 400);
        }

        return await next(context);
    }

    internal static string ComputeSha256Hex(string input)
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
