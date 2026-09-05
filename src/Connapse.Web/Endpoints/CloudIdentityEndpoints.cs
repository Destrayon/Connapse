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
    /// <summary>Carries the one-time code that claims a validated assertion.</summary>
    /// <remarks>
    /// A cookie rather than a query parameter: it must not be readable by script, must not sit in
    /// browser history, and must not be in anything a person could paste to somebody else. It is
    /// set on the response to the cross-site POST from AWS — SameSite governs when a cookie is
    /// *sent*, not whether it may be stored — and read back on the same-site redirect that follows,
    /// which is a top-level GET and therefore does carry Lax cookies.
    /// </remarks>
    private const string SamlConfirmCookieName = "__connapse_aws_link";

    private const string SamlConfirmCookiePath = "/api/v1/auth/cloud/aws";

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
            return Results.Ok(new { identities });
        }).RequireAuthorization();

        // --- AWS (per-user identity link) ---
        //
        // A separate table (AwsIdentityLinkStore / UserAwsIdentityLinkEntity) from
        // UserCloudIdentityEntity / ICloudIdentityService. This link exists so per-user AWS
        // permissions can be resolved for search — it is not a connector credential, and it holds
        // no credential of its own. It shares the same versioned route group as the identities
        // route above: the assertion consumer URL is registered in the customer's Identity Center
        // application, so it must not need to change again after ship.

        // GET /api/v1/auth/cloud/aws/connect — send the browser to IAM Identity Center.
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
            var configuration = new Saml2Configuration
            {
                Issuer = saml.EntityId,
                SingleSignOnDestination = new Uri(saml.IdpSingleSignOnUrl),
            };

            // Built before the nonce, because the nonce records the id this request carries. The
            // assertion has to name it back in InResponseTo, which is what stops one minted for a
            // different sign-in — or for none at all — being posted into this one.
            var authnRequest = new Saml2AuthnRequest(configuration)
            {
                AssertionConsumerServiceUrl = new Uri(saml.AcsUrl),
            };

            var binding = new Saml2RedirectBinding
            {
                RelayState = pending.Start(userId.Value, authnRequest.Id.Value),
            };

            binding.Bind(authnRequest);

            return Results.Redirect(binding.RedirectLocation.OriginalString);
        }).RequireAuthorization();

        // POST /api/v1/auth/cloud/aws/acs — where IAM Identity Center posts the assertion.
        //
        // Anonymous, and it has to be: the browser arrives here from AWS, so no session cookie
        // comes with it. Nothing is trusted on that account — the assertion is signed, and
        // RelayState is matched against a sign-in this deployment started.
        //
        // This endpoint deliberately saves nothing. It knows which directory user signed the
        // assertion and which Connapse user started the sign-in, and nothing here ties those two to
        // the same person: anybody with an account can start a sign-in and send the Identity Center
        // URL to a colleague, whose genuine assertion then comes back carrying the starter's nonce.
        // The pairing is what would be forged, not the document, so every check below passes. The
        // outcome is therefore parked and claimed at /aws/confirm, where a session exists.
        group.MapPost("/aws/acs", async (
            HttpContext http,
            [FromServices] IOptionsMonitor<SamlSignInSettings> settings,
            [FromServices] SamlSignInRequests pending,
            [FromServices] SamlLinkConfirmations confirmations,
            [FromServices] ISamlReplayGuard replayGuard,
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
            var started = pending.Consume(form["RelayState"]);
            if (started is null)
            {
                logger.LogWarning("A SAML assertion arrived for a sign-in this deployment did not start");
                return Results.Redirect("/profile/integrations?error=aws_unknown_request");
            }

            var result = SamlAssertionValidator.Validate(
                form["SAMLResponse"].ToString(),
                settings.CurrentValue,
                replayGuard,
                timeProvider.GetUtcNow(),
                started.Value.AuthnRequestId);

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

            // Parked, not saved. Case intact: this identifier belongs to a directory Connapse does
            // not own, and folding its case would record one that may never have existed. The email
            // rides along for display and authorizes nothing.
            string code = confirmations.Start(new PendingIdentityLink(
                started.Value.UserId, directoryUserId, result.DirectoryUserName!, result.Email));

            http.Response.Cookies.Append(SamlConfirmCookieName, code, new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = SamlLinkConfirmations.Lifetime,
                Path = SamlConfirmCookiePath,
            });

            return Results.Redirect("/api/v1/auth/cloud/aws/confirm");
        }).AllowAnonymous();

        // GET /api/v1/auth/cloud/aws/confirm — where the link is actually saved.
        //
        // Reached by a same-site top-level redirect, so unlike the consumer this one gets the
        // session cookie. Two things must agree before anything is written: the browser must hold
        // the confirmation cookie the consumer set, and the signed-in user must be the one who
        // started the sign-in.
        //
        // That is what makes the attack fail. An attacker who starts a sign-in and has a victim
        // complete it never receives the cookie — it is HttpOnly in the victim's browser — and the
        // victim, who does hold it, is not the user the sign-in was started by. Neither of them can
        // save the link, which is the correct outcome for both.
        group.MapGet("/aws/confirm", async (
            HttpContext http,
            [FromServices] SamlLinkConfirmations confirmations,
            [FromServices] AwsIdentityLinkStore linkStore,
            [FromServices] ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("CloudIdentityEndpoints.Saml");

            string? code = http.Request.Cookies[SamlConfirmCookieName];
            http.Response.Cookies.Delete(
                SamlConfirmCookieName, new CookieOptions { Path = SamlConfirmCookiePath });

            var userId = GetUserId(http);
            if (userId is null) return Results.Unauthorized();

            // Single-use, so an interrupted confirmation cannot be retried from history.
            var link = confirmations.Consume(code);
            if (link is null)
            {
                logger.LogWarning("A SAML link confirmation arrived without a claim this deployment issued");
                return Results.Redirect("/profile/integrations?error=aws_unknown_request");
            }

            if (link.StartedByUserId != userId.Value)
            {
                // The cross-user case. Worth a warning rather than an information line: the
                // ordinary flow cannot produce it, so it means somebody completed a sign-in that
                // somebody else began.
                logger.LogWarning(
                    "A SAML sign-in was completed by a different user than the one who started it; refusing to link");
                return Results.Redirect("/profile/integrations?error=aws_wrong_user");
            }

            await linkStore.SaveAsync(
                userId.Value, link.DirectoryUserId, link.DirectoryUserName, link.Email, ct);

            return Results.Redirect("/profile/integrations");
        }).RequireAuthorization();

        group.MapDelete("/{provider}", async (
            string provider,
            HttpContext httpContext,
            [FromServices] IAwsIdentityLinkService awsLinks,
            [FromServices] IConnectorScopeCache scopeCache,
            [FromServices] ISourceStore sourceStore,
            [FromServices] IConnectionStore connectionStore,
            CancellationToken ct) =>
        {
            var userId = GetUserId(httpContext);
            if (userId is null) return Results.Unauthorized();

            if (!Enum.TryParse<CloudProvider>(provider, ignoreCase: true, out var cloudProvider) || cloudProvider != CloudProvider.AWS)
                return Results.BadRequest(new { error = "invalid_provider", message = $"Unknown provider: {provider}. Valid values: AWS." });

            // AWS links live in their own store, the one the integrations page reads and
            // deletes through; the cloud-identity table this route used to consult never holds
            // a SAML link, so deleting there answered 404 and left the link in force.
            var result = await awsLinks.DisconnectAsync(userId.Value, ct);
            if (result.LinkChangedDuringDisconnect)
            {
                return Results.Conflict(new
                {
                    error = "link_changed",
                    message = "The AWS identity link changed while disconnecting. Try again."
                });
            }

            bool deleted = result.Deleted;

            // Evict cached scope entries for this user + provider
            if (deleted)
            {
                // Sources, not containers (#353). The scope cache is keyed on the thing whose
                // access was cached, and that is now a source: containers are managed storage
                // and were never cloud-scoped. A source's provider lives on its connection,
                // so the match is made there.
                try
                {
                    var connections = await connectionStore.ListAsync(take: int.MaxValue, ct: ct);
                    var matching = connections
                        .Where(c => c.Provider == ConnectionProvider.S3)
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
