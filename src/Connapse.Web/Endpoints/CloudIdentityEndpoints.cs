using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.Web.Endpoints;

public static class CloudIdentityEndpoints
{
    private const string AzureStateCookieName = "__connapse_az_state";
    private const string AzurePkceCookieName = "__connapse_az_pkce";

    private const string CognitoStateCookieName = "__connapse_cog_state";
    private const string CognitoPkceCookieName = "__connapse_cog_pkce";
    private const string CognitoNonceCookieName = "__connapse_cog_nonce";
    private const string CognitoCookiePath = "/api/cloud-identity/cognito";

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
                awsSsoConfigured = service.IsAwsSsoConfigured(),
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
        // A separate route group and a separate table (AwsIdentityLinkStore /
        // UserAwsIdentityLinkEntity) from the Azure/AWS-SSO pair above, which write to
        // UserCloudIdentityEntity via ICloudIdentityService. This link exists so per-user AWS
        // permissions can later be resolved for search — it is not a connector credential.
        var cognitoGroup = app.MapGroup("/api/cloud-identity").WithTags("Cloud Identity");

        // GET /api/cloud-identity/cognito/connect — redirect to the pool's authorize endpoint.
        // Mirrors /azure/connect deliberately. A second convention for the same job in the same
        // file costs more than reusing an imperfect one.
        cognitoGroup.MapGet("/cognito/connect", (
            HttpContext http,
            IOptionsMonitor<CognitoSettings> settings) =>
        {
            var cognito = settings.CurrentValue;
            if (!cognito.IsConfigured)
                return Results.Problem(
                    "Cognito is not configured. An administrator sets it up under Settings.",
                    statusCode: StatusCodes.Status409Conflict);

            // PKCE: the verifier never leaves this deployment, and the challenge is what Cognito
            // holds until the callback proves possession of the verifier that produced it.
            string verifier = GenerateCodeVerifier();
            string challenge = ComputeCodeChallenge(verifier);
            string state = GenerateOpaqueToken();
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
                $"&scope={Uri.EscapeDataString("openid email offline_access")}" +
                $"&state={Uri.EscapeDataString(state)}" +
                $"&nonce={Uri.EscapeDataString(nonce)}" +
                $"&code_challenge={Uri.EscapeDataString(challenge)}" +
                $"&code_challenge_method=S256";

            return Results.Redirect(authorize);
        }).RequireAuthorization();

        // GET /api/cloud-identity/cognito/callback — Cognito OAuth2 callback.
        cognitoGroup.MapGet("/cognito/callback", async (
            HttpContext http,
            [FromQuery] string code,
            [FromQuery] string state,
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

            // Rule: validate state before anything else. A callback whose state does not match is
            // not a connection.
            if (string.IsNullOrEmpty(expectedState) || expectedState != state)
                return Results.BadRequest(new { error = "invalid_state", message = "Invalid or expired state parameter." });

            if (string.IsNullOrEmpty(codeVerifier))
                return Results.BadRequest(new { error = "invalid_pkce", message = "Missing PKCE code verifier." });

            var userId = GetUserId(http);
            if (userId is null) return Results.Unauthorized();

            var cognito = settings.CurrentValue;
            if (!cognito.IsConfigured)
                return Results.Problem(
                    "Cognito is not configured. An administrator sets it up under Settings.",
                    statusCode: StatusCodes.Status409Conflict);

            var redirectUri = CognitoCallbackUri(http);
            var httpClient = httpClientFactory.CreateClient();

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
            catch (HttpRequestException)
            {
                logger.LogError("Cognito token exchange could not reach the pool");
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
            catch (JsonException)
            {
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

            var validationParameters = new TokenValidationParameters
            {
                ValidIssuer = cognito.IssuerUrl,
                ValidAudience = cognito.ClientId,
                IssuerSigningKeys = openIdConfig.SigningKeys,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.FromMinutes(2),
            };

            var result = CognitoIdTokenValidator.Validate(idToken, validationParameters, expectedNonce);
            if (!result.Success)
            {
                // result.FailureReason is a fixed code, never token or claim content.
                logger.LogWarning("Cognito ID token rejected: {Reason}", result.FailureReason);
                return Results.Redirect($"/profile/integrations?error=cognito_{result.FailureReason}");
            }

            await linkStore.SaveAsync(userId.Value, result.Email!, refreshToken, ct);

            return Results.Redirect("/profile/integrations");
        }).RequireAuthorization();

        // --- AWS SSO (Device Authorization Flow) ---

        // POST /api/v1/auth/cloud/aws/device-auth — start device authorization
        group.MapPost("/aws/device-auth", async (
            HttpContext httpContext,
            [FromServices] ICloudIdentityService service,
            CancellationToken ct) =>
        {
            if (!service.IsAwsSsoConfigured())
                return Results.BadRequest(new
                {
                    error = "aws_sso_not_configured",
                    message = "AWS IAM Identity Center SSO is not configured. An admin must set the Issuer URL and Region in settings."
                });

            try
            {
                var result = await service.StartAwsDeviceAuthAsync(ct);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "aws_device_auth_failed", message = ex.Message });
            }
        }).RequireAuthorization();

        // POST /api/v1/auth/cloud/aws/device-auth/poll — poll for device authorization completion
        group.MapPost("/aws/device-auth/poll", async (
            HttpContext httpContext,
            [FromBody] AwsDevicePollRequest request,
            [FromServices] ICloudIdentityService service,
            CancellationToken ct) =>
        {
            var userId = GetUserId(httpContext);
            if (userId is null) return Results.Unauthorized();

            try
            {
                var identity = await service.PollAwsDeviceAuthAsync(userId.Value, request.DeviceCode, ct);

                if (identity is null)
                    return Results.Ok(new { status = "pending" });

                return Results.Ok(new { status = "complete", identity });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "aws_poll_failed", message = ex.Message });
            }
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

    private static string CognitoCallbackUri(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}/api/cloud-identity/cognito/callback";

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

public record AwsDevicePollRequest(string DeviceCode);
