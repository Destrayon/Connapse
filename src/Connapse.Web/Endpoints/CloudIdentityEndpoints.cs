using System.Security.Claims;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using ITfoxtec.Identity.Saml2;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Endpoints;

public static class CloudIdentityEndpoints
{
    private const string AzureStateCookieName = "__connapse_az_state";
    private const string AzurePkceCookieName = "__connapse_az_pkce";

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

        // --- AWS (per-user identity link) ---
        //
        // A separate table (AwsIdentityLinkStore / UserAwsIdentityLinkEntity) from the
        // Azure connect/callback above, which write to UserCloudIdentityEntity via
        // ICloudIdentityService. This link exists so per-user AWS permissions can be resolved for
        // search — it is not a connector credential, and it holds no credential of its own. It
        // shares the same versioned route group as the Azure endpoints above: the assertion
        // consumer URL is registered in the customer's Identity Center application, so it must not
        // need to change again after ship.

        // GET /api/v1/auth/cloud/aws/connect — send the browser to IAM Identity Center.
        // Mirrors /azure/connect deliberately. A second convention for the same job in the same
        // file costs more than reusing an imperfect one.
        group.MapGet("/aws/connect", (
            HttpContext http,
            [FromServices] IOptionsMonitor<SamlSignInSettings> settings,
            [FromServices] SamlSignInRequests pending) =>
        {
            var userId = GetUserId(http);
            if (userId is null) return Results.Unauthorized();

            var saml = settings.CurrentValue;
            if (!saml.IsConfigured)
                return Results.Problem(
                    "AWS sign-in is not configured. An administrator sets it up under Providers.",
                    statusCode: StatusCodes.Status409Conflict);

            // Who is connecting travels as a nonce in RelayState rather than being read from the
            // session at the other end. The assertion arrives on a cross-site POST from AWS, and a
            // SameSite=Lax cookie is not sent on one — so the consumer endpoint cannot see who is
            // signed in, however plainly the browser is theirs.
            //
            // It also answers, with one value, what the OIDC flow this replaced needed three
            // cookies for. The nonce names nobody on its own, is single-use, and the user it
            // belongs to never leaves this process.
            var binding = new Saml2RedirectBinding { RelayState = pending.Start(userId.Value) };

            var configuration = new Saml2Configuration
            {
                Issuer = saml.EntityId,
                SingleSignOnDestination = new Uri(saml.IdpSingleSignOnUrl),
            };

            binding.Bind(new Saml2AuthnRequest(configuration)
            {
                AssertionConsumerServiceUrl = new Uri(saml.AcsUrl),
            });

            return Results.Redirect(binding.RedirectLocation.OriginalString);
        }).RequireAuthorization();

        // POST /api/v1/auth/cloud/aws/acs — where IAM Identity Center posts the assertion.
        //
        // Anonymous, and it has to be: the browser arrives here from AWS, so no session cookie
        // comes with it. Nothing is trusted on that account — the assertion is signed, and
        // RelayState is matched against a sign-in this deployment started.
        group.MapPost("/aws/acs", async (
            HttpContext http,
            [FromServices] IOptionsMonitor<SamlSignInSettings> settings,
            [FromServices] SamlSignInRequests pending,
            [FromServices] ISamlReplayGuard replayGuard,
            [FromServices] AwsIdentityLinkStore linkStore,
            [FromServices] IDirectoryUserLookup directoryUsers,
            [FromServices] TimeProvider timeProvider,
            [FromServices] ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("CloudIdentityEndpoints.Saml");

            if (!http.Request.HasFormContentType)
                return Results.Redirect("/profile/integrations?error=aws_response_malformed");

            var form = await http.Request.ReadFormAsync(ct);

            // Consumed before the assertion is examined, and single-use. A replayed RelayState
            // resolves to nobody here, which is the cheapest of the several places this stops.
            var userId = pending.Consume(form["RelayState"]);
            if (userId is null)
            {
                logger.LogWarning("A SAML assertion arrived for a sign-in this deployment did not start");
                return Results.Redirect("/profile/integrations?error=aws_unknown_request");
            }

            var result = SamlAssertionValidator.Validate(
                form["SAMLResponse"].ToString(),
                settings.CurrentValue,
                replayGuard,
                timeProvider.GetUtcNow());

            if (!result.Success)
            {
                // result.FailureReason is a fixed code, never assertion content.
                logger.LogWarning("SAML assertion rejected: {Reason}", result.FailureReason);
                return Results.Redirect($"/profile/integrations?error=aws_{result.FailureReason}");
            }

            // The asserted name is only half an identity. Access grants are held against the
            // identity store's own id, so it is resolved once here rather than on every search —
            // and because it is the stable identifier, a later rename in the directory does not
            // force this person to connect again.
            string? directoryUserId =
                await directoryUsers.FindUserIdAsync(result.DirectoryUserName!, ct);
            if (string.IsNullOrWhiteSpace(directoryUserId))
            {
                logger.LogWarning("The directory has no user matching the name an assertion carried");
                return Results.Redirect("/profile/integrations?error=aws_no_directory_user");
            }

            // Stored with its case intact: this identifier belongs to a directory Connapse does not
            // own, and folding its case would record one that may never have existed. The email
            // rides along for display and authorizes nothing.
            await linkStore.SaveAsync(
                userId.Value, directoryUserId, result.DirectoryUserName!, result.Email, ct);

            return Results.Redirect("/profile/integrations");
        }).AllowAnonymous();

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
}
