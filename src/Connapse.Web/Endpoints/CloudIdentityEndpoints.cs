using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Identity.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Connapse.Web.Endpoints;

public static class CloudIdentityEndpoints
{
    private const string AzureStateCookieName = "__connapse_az_state";
    private const string AzurePkceCookieName = "__connapse_az_pkce";

    private const string CognitoStateCookieName = "__connapse_cog_state";
    private const string CognitoPkceCookieName = "__connapse_cog_pkce";
    private const string CognitoNonceCookieName = "__connapse_cog_nonce";
    private const string CognitoCookiePath = "/api/v1/auth/cloud/cognito";

    // Cached per issuer so signing keys are fetched from the pool's discovery document once and
    // reused, rather than refetched on every callback.
    private static readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>>
        CognitoConfigManagers = new();

    public static IEndpointRouteBuilder MapCloudIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth/cloud").WithTags("Cloud Identity");

        // GET /api/v1/auth/cloud/identities — list current user's linked cloud identities
        group.MapGet("/identities", async (
            HttpContext httpContext,
            [FromServices] ICloudIdentityService service,
            CancellationToken ct) =>
        {
            var userId = GetUserId(httpContext);
            if (userId is null) return Results.Unauthorized();

            var identities = await service.ListAsync(userId.Value, ct);
            return Results.Ok(new
            {
                identities,
                azureAdConfigured = service.IsAzureAdConfigured()
            });
        }).RequireAuthorization();

        // --- Azure OAuth2 ---

        // GET /api/v1/auth/cloud/azure/connect — redirect to Azure AD authorize endpoint
        group.MapGet("/azure/connect", (
            HttpContext httpContext,
            [FromServices] ICloudIdentityService service) =>
        {
            if (!service.IsAzureAdConfigured())
                return Results.BadRequest(new { error = "azure_ad_not_configured", message = "Azure AD is not configured. An admin must set ClientId and TenantId in settings." });

            var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
            var result = service.GetAzureConnectUrl(baseUrl);

            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = httpContext.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(10),
                Path = "/api/v1/auth/cloud/azure"
            };

            httpContext.Response.Cookies.Append(AzureStateCookieName, result.State, cookieOptions);
            httpContext.Response.Cookies.Append(AzurePkceCookieName, result.CodeVerifier, cookieOptions);

            return Results.Redirect(result.AuthorizeUrl);
        }).RequireAuthorization();

        // GET /api/v1/auth/cloud/azure/callback — Azure AD OAuth2 callback
        group.MapGet("/azure/callback", async (
            HttpContext httpContext,
            [FromQuery] string code,
            [FromQuery] string state,
            [FromServices] ICloudIdentityService service,
            CancellationToken ct) =>
        {
            var expectedState = httpContext.Request.Cookies[AzureStateCookieName];
            if (string.IsNullOrEmpty(expectedState) || expectedState != state)
                return Results.BadRequest(new { error = "invalid_state", message = "Invalid or expired state parameter." });

            var codeVerifier = httpContext.Request.Cookies[AzurePkceCookieName];
            if (string.IsNullOrEmpty(codeVerifier))
                return Results.BadRequest(new { error = "invalid_pkce", message = "Missing PKCE code verifier." });

            var deleteCookieOptions = new CookieOptions { Path = "/api/v1/auth/cloud/azure" };
            httpContext.Response.Cookies.Delete(AzureStateCookieName, deleteCookieOptions);
            httpContext.Response.Cookies.Delete(AzurePkceCookieName, deleteCookieOptions);

            var userId = GetUserId(httpContext);
            if (userId is null) return Results.Unauthorized();

            var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
            var redirectUri = $"{baseUrl}/api/v1/auth/cloud/azure/callback";

            try
            {
                await service.HandleAzureCallbackAsync(userId.Value, code, codeVerifier, redirectUri, ct);
                return Results.Redirect("/profile");
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = "azure_callback_failed", message = ex.Message });
            }
        }).RequireAuthorization();

        // --- Cognito (per-user AWS identity link) ---
        //
        // A separate table (AwsIdentityLinkStore / UserAwsIdentityLinkEntity) from the
        // Azure connect/callback above, which write to UserCloudIdentityEntity via
        // ICloudIdentityService. This link exists so per-user AWS permissions can later be
        // resolved for search — it is not a connector credential. It shares the same versioned
        // route group as the Azure endpoints above: this callback URL is registered in the
        // customer's Cognito app client, so it must not need to change again after ship.

        // GET /api/v1/auth/cloud/cognito/connect — redirect to the pool's authorize endpoint.
        // Mirrors /azure/connect deliberately. A second convention for the same job in the same
        // file costs more than reusing an imperfect one.
        group.MapGet("/cognito/connect", (
            HttpContext http,
            IOptionsMonitor<CognitoSettings> settings) =>
        {
            var userId = GetUserId(http);
            if (userId is null) return Results.Unauthorized();

            var cognito = settings.CurrentValue;
            if (!cognito.IsConfigured)
                return Results.Problem(
                    "Cognito is not configured. An administrator sets it up under Settings.",
                    statusCode: StatusCodes.Status409Conflict);

            // PKCE: the verifier never leaves this deployment, and the challenge is what Cognito
            // holds until the callback proves possession of the verifier that produced it.
            string verifier = GenerateCodeVerifier();
            string challenge = ComputeCodeChallenge(verifier);
            // State, verifier and nonce are stashed as browser-scoped cookies, not
            // session-scoped ones: they carry no user identity of their own. Without more, the
            // callback would decide whose account to link by trusting whichever principal
            // happens to be signed in when it runs — which can be a different person than the
            // one who clicked Connect, if their session ends and someone else signs in on the
            // same browser inside the cookie's lifetime. Prefixing the initiating user's id onto
            // the opaque state lets the callback catch that without a fourth cookie. The id is
            // not a secret, so it must not be treated as contributing entropy — the random half
            // is generated exactly as it was before this was added.
            string state = $"{userId.Value}:{GenerateOpaqueToken()}";
            // Bound in the authorize request; checked against the ID token's `nonce` claim in the
            // callback. Cheap, and stops a token minted for a different request being replayed
            // into this one.
            string nonce = GenerateOpaqueToken();

            StashCognitoState(http, state, verifier, nonce);

            string authorize =
                $"{cognito.Domain.TrimEnd('/')}/oauth2/authorize" +
                $"?response_type=code" +
                $"&client_id={Uri.EscapeDataString(cognito.ClientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(CognitoCallbackUri(http))}" +
                // Cognito is not a standard OIDC provider here: it has no `offline_access`
                // scope, and asking for one fails the whole authorize request with
                // error=invalid_request / error_description=invalid_scope before any login page is
                // shown. The refresh token this flow stores arrives with the code grant regardless
                // — it is governed by the client's RefreshTokenValidity, not by a requested scope.
                // These two must also stay a subset of the app client's AllowedOAuthScopes.
                $"&scope={Uri.EscapeDataString("openid email")}" +
                $"&state={Uri.EscapeDataString(state)}" +
                $"&nonce={Uri.EscapeDataString(nonce)}" +
                $"&code_challenge={Uri.EscapeDataString(challenge)}" +
                $"&code_challenge_method=S256";

            return Results.Redirect(authorize);
        }).RequireAuthorization();

        // GET /api/v1/auth/cloud/cognito/callback — Cognito OAuth2 callback.
        group.MapGet("/cognito/callback", async (
            HttpContext http,
            [FromQuery] string? code,
            [FromQuery] string? state,
            [FromQuery] string? error,
            [FromQuery(Name = "error_description")] string? errorDescription,
            [FromServices] IOptionsMonitor<CognitoSettings> settings,
            [FromServices] AwsIdentityLinkStore linkStore,
            [FromServices] IHttpClientFactory httpClientFactory,
            [FromServices] ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("CloudIdentityEndpoints.Cognito");

            var expectedState = http.Request.Cookies[CognitoStateCookieName];
            var codeVerifier = http.Request.Cookies[CognitoPkceCookieName];
            var expectedNonce = http.Request.Cookies[CognitoNonceCookieName];

            // Rule: clear the stashed state either way — a callback is one-shot regardless of
            // whether it succeeds.
            var deleteCookieOptions = new CookieOptions { Path = CognitoCookiePath };
            http.Response.Cookies.Delete(CognitoStateCookieName, deleteCookieOptions);
            http.Response.Cookies.Delete(CognitoPkceCookieName, deleteCookieOptions);
            http.Response.Cookies.Delete(CognitoNonceCookieName, deleteCookieOptions);

            // Rule: a provider-side error (the user declined consent, or Cognito rejected the
            // request before ever issuing a code) arrives with `error` set and no `code` at all.
            // code/state used to be non-nullable [FromQuery] parameters, so minimal API's model
            // binding rejected this shape before the handler ever ran, leaving the user with a
            // raw 400 and the three cookies just cleared above stuck in the browser until they
            // expired on their own. Handle it the same way every other failure path here does:
            // cookies are already gone, so just redirect with a fixed reason. The provider's raw
            // error string is never echoed into the redirect — only ever one of our own values.
            if (!string.IsNullOrEmpty(error))
            {
                logger.LogWarning(
                    "Cognito callback reported a provider-side error: {Error} ({Description})",
                    LogSanitizer.Sanitize(error),
                    LogSanitizer.Sanitize(errorDescription ?? "no description"));
                var reason = error == "access_denied" ? "cognito_user_cancelled" : "cognito_provider_error";
                return Results.Redirect($"/profile/integrations?error={reason}");
            }

            // Rule: validate state before anything else. A callback whose state does not match is
            // not a connection.
            if (string.IsNullOrEmpty(expectedState) || string.IsNullOrEmpty(state) || expectedState != state)
                return Results.BadRequest(new { error = "invalid_state", message = "Invalid or expired state parameter." });

            if (string.IsNullOrEmpty(codeVerifier))
                return Results.BadRequest(new { error = "invalid_pkce", message = "Missing PKCE code verifier." });

            if (string.IsNullOrEmpty(code))
            {
                // No provider error was reported, yet there is still no code to exchange. Not a
                // shape Cognito is documented to produce, but code below assumes a non-empty code,
                // so this is handled the same way as the explicit-error branch above rather than
                // let a null reach the token exchange.
                logger.LogWarning("Cognito callback carried no error but was also missing an authorization code");
                return Results.Redirect("/profile/integrations?error=cognito_provider_error");
            }

            var userId = GetUserId(http);
            if (userId is null) return Results.Unauthorized();

            // Rule: the flow is bound to whoever started it, not whoever happens to be signed in
            // when Cognito redirects back. State, verifier and nonce are browser-scoped cookies
            // with no session identity of their own, so without this check a session that ended
            // (or was switched) mid-flow on the same browser would silently link the verified AWS
            // email to whoever is signed in now instead of who clicked Connect.
            var initiatingUserId = ParseInitiatingUserId(expectedState);
            if (initiatingUserId is null || initiatingUserId != userId.Value)
            {
                logger.LogWarning("Cognito callback rejected: the signed-in user did not match who started the flow");
                return Results.Redirect("/profile/integrations?error=cognito_user_mismatch");
            }

            var cognito = settings.CurrentValue;
            if (!cognito.IsConfigured)
                return Results.Problem(
                    "Cognito is not configured. An administrator sets it up under Settings.",
                    statusCode: StatusCodes.Status409Conflict);

            var redirectUri = CognitoCallbackUri(http);
            var httpClient = httpClientFactory.CreateClient();
            // A browser is waiting on this request. Don't rely on HttpClient's 100-second default —
            // an unreachable or slow pool should redirect with an error well before the user gives up.
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var tokenParams = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = cognito.ClientId,
                ["client_secret"] = cognito.ClientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = codeVerifier,
            };

            HttpResponseMessage tokenResponse;
            try
            {
                // Never log tokenParams above — it carries the authorization code, the PKCE
                // verifier and the client secret.
                tokenResponse = await httpClient.PostAsync(
                    $"{cognito.Domain.TrimEnd('/')}/oauth2/token",
                    new FormUrlEncodedContent(tokenParams), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // TaskCanceledException is what HttpClient throws on its own timeout above (and
                // what a client-disconnect cancellation looks like) — neither carries a token or
                // secret, so nothing more than the exception type is worth knowing here.
                logger.LogError("Cognito token exchange could not reach the pool in time");
                return Results.Redirect("/profile/integrations?error=cognito_exchange_failed");
            }

            if (!tokenResponse.IsSuccessStatusCode)
            {
                logger.LogError("Cognito token exchange failed with status {StatusCode}", tokenResponse.StatusCode);
                return Results.Redirect("/profile/integrations?error=cognito_exchange_failed");
            }

            string? idToken;
            string? refreshToken;
            try
            {
                var responseBody = await tokenResponse.Content.ReadAsStringAsync(ct);
                var tokenJson = JsonSerializer.Deserialize<JsonElement>(responseBody);
                idToken = tokenJson.TryGetProperty("id_token", out var idTokenProp) ? idTokenProp.GetString() : null;
                refreshToken = tokenJson.TryGetProperty("refresh_token", out var refreshTokenProp) ? refreshTokenProp.GetString() : null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // InvalidOperationException is what JsonElement.GetString() throws when a property
                // is present but not a string (e.g. the pool returned id_token as a number/object) —
                // a malformed response, not a parse failure, but the same "give up cleanly" outcome.
                logger.LogError("Cognito token response was not valid JSON");
                return Results.Redirect("/profile/integrations?error=cognito_exchange_failed");
            }

            if (string.IsNullOrEmpty(idToken) || string.IsNullOrEmpty(refreshToken))
            {
                logger.LogError("Cognito token response was missing id_token or refresh_token");
                return Results.Redirect("/profile/integrations?error=cognito_exchange_failed");
            }

            // Rule: validate the ID token — signature against the pool's JWKS, issuer, audience
            // and lifetime — before reading any claim from it.
            OpenIdConnectConfiguration openIdConfig;
            try
            {
                var configManager = GetCognitoConfigManager(cognito.IssuerUrl);
                openIdConfig = await configManager.GetConfigurationAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                logger.LogError(ex, "Could not fetch the Cognito pool's discovery document");
                return Results.Redirect("/profile/integrations?error=cognito_token_invalid");
            }

            var validationParameters = CognitoIdTokenValidator.BuildValidationParameters(cognito, openIdConfig.SigningKeys);

            var result = CognitoIdTokenValidator.Validate(idToken, validationParameters, expectedNonce);
            if (!result.Success)
            {
                // result.FailureReason is a fixed code, never token or claim content.
                logger.LogWarning("Cognito ID token rejected: {Reason}", result.FailureReason);
                return Results.Redirect($"/profile/integrations?error=cognito_{result.FailureReason}");
            }

            // Normalized because this is the join key into an IAM Identity Center lookup — a case
            // difference between what Cognito emits and what the directory holds would fail the
            // match at resolution time, long after this connection looked like it succeeded.
            var normalizedEmail = result.Email!.ToLowerInvariant();
            await linkStore.SaveAsync(userId.Value, normalizedEmail, refreshToken, ct);

            return Results.Redirect("/profile/integrations");
        }).RequireAuthorization();

        // DELETE /api/v1/auth/cloud/{provider} — disconnect a cloud identity
        group.MapDelete("/{provider}", async (
            string provider,
            HttpContext httpContext,
            [FromServices] ICloudIdentityService service,
            [FromServices] IConnectorScopeCache scopeCache,
            [FromServices] ISourceStore sourceStore,
            [FromServices] IConnectionStore connectionStore,
            CancellationToken ct) =>
        {
            var userId = GetUserId(httpContext);
            if (userId is null) return Results.Unauthorized();

            if (!Enum.TryParse<CloudProvider>(provider, ignoreCase: true, out var cloudProvider))
                return Results.BadRequest(new { error = "invalid_provider", message = $"Unknown provider: {provider}. Valid values: AWS, Azure." });

            var deleted = await service.DisconnectAsync(userId.Value, cloudProvider, ct);

            // Evict cached scope entries for this user + provider
            if (deleted)
            {
                // Sources, not containers (#353). The scope cache is keyed on the thing whose
                // access was cached, and that is now a source: containers are managed storage
                // and were never cloud-scoped. A source's provider lives on its connection,
                // so the match is made there.
                var targetProvider = cloudProvider == CloudProvider.AWS
                    ? ConnectionProvider.S3
                    : ConnectionProvider.AzureBlob;

                try
                {
                    var connections = await connectionStore.ListAsync(take: int.MaxValue, ct: ct);
                    var matching = connections
                        .Where(c => c.Provider == targetProvider)
                        .Select(c => c.Id)
                        .ToHashSet();

                    if (matching.Count > 0)
                    {
                        var sources = await sourceStore.ListAsync(take: int.MaxValue, ct: ct);
                        foreach (var s in sources.Where(s => matching.Contains(s.ConnectionId)))
                            scopeCache.Invalidate(userId.Value, s.Id);
                    }
                }
                catch { /* Best-effort eviction — cache will expire naturally */ }
            }

            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization();

        return app;
    }

    private static Guid? GetUserId(HttpContext httpContext)
    {
        var idClaim = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idClaim, out var id) ? id : null;
    }

    // --- Cognito helpers ---

    private static ConfigurationManager<OpenIdConnectConfiguration> GetCognitoConfigManager(string issuerUrl) =>
        CognitoConfigManagers.GetOrAdd(issuerUrl, iss =>
            new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{iss.TrimEnd('/')}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever()));

    private static void StashCognitoState(HttpContext http, string state, string verifier, string nonce)
    {
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10),
            Path = CognitoCookiePath
        };

        http.Response.Cookies.Append(CognitoStateCookieName, state, cookieOptions);
        http.Response.Cookies.Append(CognitoPkceCookieName, verifier, cookieOptions);
        http.Response.Cookies.Append(CognitoNonceCookieName, nonce, cookieOptions);
    }

    /// <summary>
    /// The <c>redirect_uri</c> sent to Cognito, which must equal the callback registered in the
    /// pool's app client character for character or Cognito refuses the request.
    /// </summary>
    /// <remarks>
    /// The path comes from <see cref="CognitoRedirect.CallbackPath"/>, which is also what the
    /// settings form shows an administrator to paste into AWS. Two literals would have been one
    /// edit away from disagreeing, and the resulting error names neither of them.
    /// </remarks>
    private static string CognitoCallbackUri(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}{CognitoRedirect.CallbackPath}";

    /// <summary>
    /// Splits the initiating user's id off the front of a stashed Cognito state value (format
    /// <c>"{userId}:{random}"</c>), or null when the state does not have that shape at all — e.g.
    /// an old-format state left over from before this existed, which must not be trusted as
    /// belonging to anyone.
    /// </summary>
    private static Guid? ParseInitiatingUserId(string state)
    {
        var separatorIndex = state.IndexOf(':');
        if (separatorIndex <= 0) return null;
        return Guid.TryParse(state[..separatorIndex], out var id) ? id : null;
    }

    private static string GenerateOpaqueToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static string GenerateCodeVerifier() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string ComputeCodeChallenge(string codeVerifier) =>
        Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
