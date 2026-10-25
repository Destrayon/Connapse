// src/Connapse.Web/Endpoints/OAuthEndpoints.cs
using System.Security.Claims;
using Connapse.Core;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

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
            BuildProtectedResourceMetadata(ctx, resourcePath: null)).AllowAnonymous()
            .Produces<ProtectedResourceMetadata>();

        app.MapGet("/.well-known/oauth-protected-resource/{**resourcePath}", (HttpContext ctx, string resourcePath) =>
            BuildProtectedResourceMetadata(ctx, resourcePath)).AllowAnonymous()
            .Produces<ProtectedResourceMetadata>();

        app.MapGet("/.well-known/oauth-authorization-server", (HttpContext ctx) =>
        {
            var baseUrl = GetBaseUrl(ctx);
            return Results.Json(new AuthorizationServerMetadata(
                Issuer: baseUrl,
                AuthorizationEndpoint: $"{baseUrl}/oauth/authorize",
                TokenEndpoint: $"{baseUrl}/oauth/token",
                RegistrationEndpoint: $"{baseUrl}/oauth/register",
                ScopesSupported: ["knowledge:read", "knowledge:write"],
                ResponseTypesSupported: ["code"],
                GrantTypesSupported: ["authorization_code", "refresh_token"],
                CodeChallengeMethodsSupported: ["S256"],
                TokenEndpointAuthMethodsSupported: ["none", "client_secret_post"],
                ClientIdMetadataDocumentSupported: true));
        }).AllowAnonymous()
        .Produces<AuthorizationServerMetadata>();

        // -- Static CLI Metadata Document --

        app.MapGet("/oauth/clients/cli.json", (HttpContext ctx) =>
        {
            var baseUrl = GetBaseUrl(ctx);
            return Results.Json(new ClientMetadataDocument(
                ClientId: $"{baseUrl}/oauth/clients/cli.json",
                ClientName: "Connapse CLI",
                RedirectUris: ["http://127.0.0.1/callback"],
                GrantTypes: ["authorization_code", "refresh_token"],
                ResponseTypes: ["code"],
                TokenEndpointAuthMethod: "none"));
        }).AllowAnonymous()
        .Produces<ClientMetadataDocument>();

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
                    ctx, form, authCodeService, tokenService, userManager, dbContext, ct),
                "refresh_token" => await HandleRefreshTokenGrant(
                    ctx, form, tokenService, dbContext, ct),
                _ => Results.Json(new OAuthError("unsupported_grant_type"), statusCode: 400),
            };
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitingExtensions.AuthPolicy)
        .AddEndpointFilter(RequireFormContentType)
        .DisableAntiforgery()
        .Produces<OAuthTokenResponse>()
        .Produces<OAuthError>(StatusCodes.Status400BadRequest)
        .Produces<OAuthError>(StatusCodes.Status429TooManyRequests);

        // -- Dynamic Client Registration --

        app.MapPost("/oauth/register", async (
            HttpContext ctx,
            [FromServices] OAuthClientService clientService,
            CancellationToken ct) =>
        {
            // Read here rather than bound, so a body that isn't a JSON registration gets RFC 7591's error, not the
            // framework's: this route is outside /api, where binding failures become problems.
            ClientRegistrationRequest? request = null;
            if (ctx.Request.HasJsonContentType())
            {
                try
                {
                    request = await ctx.Request.ReadFromJsonAsync<ClientRegistrationRequest>(ct);
                }
                catch (System.Text.Json.JsonException)
                {
                }
            }
            if (request is null)
                return Results.Json(new OAuthError("invalid_client_metadata", "The body must be a JSON client registration."), statusCode: 400);

            try
            {
                var result = await clientService.RegisterAsync(
                    request.ClientName,
                    request.RedirectUris,
                    request.ApplicationType ?? "web",
                    ctx.Connection.RemoteIpAddress?.ToString(),
                    ct);

                return Results.Json(new ClientRegistrationResponse(
                    ClientId: result.ClientId,
                    ClientName: result.ClientName,
                    RedirectUris: result.RedirectUris,
                    GrantTypes: ["authorization_code", "refresh_token"],
                    ApplicationType: result.ApplicationType,
                    TokenEndpointAuthMethod: "none"), statusCode: 201);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new OAuthError("invalid_client_metadata", ex.Message), statusCode: 400);
            }
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitingExtensions.AuthPolicy)
        .DisableAntiforgery()
        // The body is read by hand above, so the document is told what it takes. Not Accepts<T>: that makes routing
        // refuse any other content type before the handler, with the framework's answer instead of RFC 7591's.
        .AddOpenApiOperationTransformer(async (operation, context, ct) =>
        {
            const string schema = nameof(ClientRegistrationRequest);
            if (context.Document!.Components?.Schemas?.ContainsKey(schema) != true)
                context.Document.AddComponent(schema, await context.GetOrCreateSchemaAsync(typeof(ClientRegistrationRequest), null, ct));
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new() { Schema = new OpenApiSchemaReference(schema, context.Document) },
                },
            };
        })
        .Produces<ClientRegistrationResponse>(StatusCodes.Status201Created)
        .Produces<OAuthError>(StatusCodes.Status400BadRequest)
        .Produces<OAuthError>(StatusCodes.Status429TooManyRequests);

        return app;
    }

    private static async Task<IResult> HandleAuthorizationCodeGrant(
        HttpContext ctx,
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
        var resourceParam = form["resource"].ToString();

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(redirectUri) ||
            string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(codeVerifier))
        {
            return Results.Json(new OAuthError("invalid_request"), statusCode: 400);
        }

        var exchangeResult = await authCodeService.ExchangeAsync(code, codeVerifier, redirectUri, clientId, ct);
        if (exchangeResult is null || !await ClientMayHoldGrantsAsync(dbContext, clientId, ct))
            return Results.Json(new OAuthError("invalid_grant"), statusCode: 400);

        // RFC 8707 §2 / MCP §Token Audience Binding: if the client included a
        // `resource` parameter at the token endpoint, it MUST match the value
        // it presented at the authorization endpoint. Mismatches mean the
        // client is requesting a token for a different audience than the user
        // consented to — reject as invalid_target.
        if (!string.IsNullOrWhiteSpace(resourceParam) &&
            !string.Equals(resourceParam, exchangeResult.Resource, StringComparison.Ordinal))
        {
            return Results.Json(new OAuthError("invalid_target"), statusCode: 400);
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

        var user = await userManager.FindByIdAsync(exchangeResult.UserId.ToString());
        if (user is null)
            return Results.Json(new OAuthError("invalid_grant"), statusCode: 400);

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
            refreshEntity.Scope = exchangeResult.Scope;
            await dbContext.SaveChangesAsync(ct);
        }

        return Results.Json(new OAuthTokenResponse(
            tokenResponse.AccessToken,
            "bearer",
            (int)(tokenResponse.ExpiresAt - DateTime.UtcNow).TotalSeconds,
            tokenResponse.RefreshToken,
            exchangeResult.Scope));
    }

    /// <summary>
    /// Whether a client may still be issued tokens. A registered client may only while its registration
    /// exists, so a code or refresh token minted while it was being revoked (#881) stops working at its next
    /// use. A client known by its metadata URL never registers; revoking it ends its current grants only.
    /// </summary>
    private static async Task<bool> ClientMayHoldGrantsAsync(ConnapseIdentityDbContext dbContext, string clientId, CancellationToken ct) =>
        Uri.TryCreate(clientId, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || (uri.Scheme == "http" && uri.Host is "127.0.0.1" or "localhost"))
            || await dbContext.OAuthClients.AnyAsync(c => c.ClientId == clientId, ct);

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
            return Results.Json(new OAuthError("invalid_request"), statusCode: 400);

        // Look up the old refresh token to validate client_id/resource and propagate them
        var oldTokenHash = ComputeSha256Hex(refreshToken);
        var oldRefreshEntity = await dbContext.RefreshTokens
            .FirstOrDefaultAsync(r => r.TokenHash == oldTokenHash, ct);
        // Only a refresh token issued to an OAuth client refreshes here. A sign-in's belongs to
        // /api/v1/auth/token/refresh: rotating it here would bind its replacement to this client with no granted
        // scope, which no refresh accepts afterwards (#934).
        if (oldRefreshEntity?.ClientId is not { } boundClient
            || !string.Equals(boundClient, clientId, StringComparison.Ordinal) || !await ClientMayHoldGrantsAsync(dbContext, boundClient, ct))
        {
            return Results.Json(new OAuthError("invalid_grant"), statusCode: 400);
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
            return Results.Json(new OAuthError("invalid_target"), statusCode: 400);
        }

        // Bind the refreshed access token's `iss` to the current request's
        // scheme+host (same rule as at initial mint) so it matches the AS
        // metadata document per RFC 9068 §2.2.
        var issuer = $"{ctx.Request.Scheme}://{ctx.Request.Host}";

        var tokenResponse = await tokenService.RefreshTokenAsync(refreshToken, issuer, ct);
        if (tokenResponse is null)
            return Results.Json(new OAuthError("invalid_grant"), statusCode: 400);

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

        return Results.Json(new OAuthTokenResponse(
            tokenResponse.AccessToken,
            "bearer",
            (int)(tokenResponse.ExpiresAt - DateTime.UtcNow).TotalSeconds,
            tokenResponse.RefreshToken));
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

        return Results.Json(new ProtectedResourceMetadata(
            resource,
            [baseUrl],
            ["knowledge:read", "knowledge:write"],
            ["header"]));
    }

    // OAuth 2.1 requires application/x-www-form-urlencoded (RFC 6749 §3.2)
    private static async ValueTask<object?> RequireFormContentType(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!context.HttpContext.Request.HasFormContentType)
        {
            return Results.Json(
                new OAuthError("invalid_request", "Content-Type must be application/x-www-form-urlencoded"), statusCode: 400);
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

// The OAuth documents and answers, under the member names their RFCs define.

/// <summary>RFC 9728 protected resource metadata.</summary>
public sealed record ProtectedResourceMetadata(
    [property: System.Text.Json.Serialization.JsonPropertyName("resource")] string Resource,
    [property: System.Text.Json.Serialization.JsonPropertyName("authorization_servers")] IReadOnlyList<string> AuthorizationServers,
    [property: System.Text.Json.Serialization.JsonPropertyName("scopes_supported")] IReadOnlyList<string> ScopesSupported,
    [property: System.Text.Json.Serialization.JsonPropertyName("bearer_methods_supported")] IReadOnlyList<string> BearerMethodsSupported);

/// <summary>RFC 8414 authorization server metadata.</summary>
public sealed record AuthorizationServerMetadata(
    [property: System.Text.Json.Serialization.JsonPropertyName("issuer")] string Issuer,
    [property: System.Text.Json.Serialization.JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: System.Text.Json.Serialization.JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: System.Text.Json.Serialization.JsonPropertyName("registration_endpoint")] string RegistrationEndpoint,
    [property: System.Text.Json.Serialization.JsonPropertyName("scopes_supported")] IReadOnlyList<string> ScopesSupported,
    [property: System.Text.Json.Serialization.JsonPropertyName("response_types_supported")] IReadOnlyList<string> ResponseTypesSupported,
    [property: System.Text.Json.Serialization.JsonPropertyName("grant_types_supported")] IReadOnlyList<string> GrantTypesSupported,
    [property: System.Text.Json.Serialization.JsonPropertyName("code_challenge_methods_supported")] IReadOnlyList<string> CodeChallengeMethodsSupported,
    [property: System.Text.Json.Serialization.JsonPropertyName("token_endpoint_auth_methods_supported")] IReadOnlyList<string> TokenEndpointAuthMethodsSupported,
    [property: System.Text.Json.Serialization.JsonPropertyName("client_id_metadata_document_supported")] bool ClientIdMetadataDocumentSupported);

/// <summary>A client ID metadata document, as the CLI's client ID points to.</summary>
public sealed record ClientMetadataDocument(
    [property: System.Text.Json.Serialization.JsonPropertyName("client_id")] string ClientId,
    [property: System.Text.Json.Serialization.JsonPropertyName("client_name")] string ClientName,
    [property: System.Text.Json.Serialization.JsonPropertyName("redirect_uris")] IReadOnlyList<string> RedirectUris,
    [property: System.Text.Json.Serialization.JsonPropertyName("grant_types")] IReadOnlyList<string> GrantTypes,
    [property: System.Text.Json.Serialization.JsonPropertyName("response_types")] IReadOnlyList<string> ResponseTypes,
    [property: System.Text.Json.Serialization.JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod);

/// <summary>An RFC 7591 registration's answer.</summary>
public sealed record ClientRegistrationResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("client_id")] string ClientId,
    [property: System.Text.Json.Serialization.JsonPropertyName("client_name")] string ClientName,
    [property: System.Text.Json.Serialization.JsonPropertyName("redirect_uris")] IReadOnlyList<string> RedirectUris,
    [property: System.Text.Json.Serialization.JsonPropertyName("grant_types")] IReadOnlyList<string> GrantTypes,
    [property: System.Text.Json.Serialization.JsonPropertyName("application_type")] string ApplicationType,
    [property: System.Text.Json.Serialization.JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod);

/// <summary>An RFC 6749 token answer. <c>scope</c> is on an authorization code grant's only.</summary>
public sealed record OAuthTokenResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string AccessToken,
    [property: System.Text.Json.Serialization.JsonPropertyName("token_type")] string TokenType,
    [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int ExpiresIn,
    [property: System.Text.Json.Serialization.JsonPropertyName("refresh_token")] string RefreshToken,
    [property: System.Text.Json.Serialization.JsonPropertyName("scope"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Scope = null);

/// <summary>An RFC 6749 error. The rate limiter answers outside /api with the same shape.</summary>
public sealed record OAuthError(
    [property: System.Text.Json.Serialization.JsonPropertyName("error")] string Error,
    [property: System.Text.Json.Serialization.JsonPropertyName("error_description"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ErrorDescription = null);
