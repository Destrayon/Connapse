using System.Net;
using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Links;
using Connapse.Application.Ports;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Custom;
using Connapse.Web.Problems;
using Connapse.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Account linking for custom providers (protocol: Linking). Connapse starts the link with a signed token,
/// and finishes it only for the signed-in user: the middle man activates a pending link when Connapse names
/// the same user the token did, so a link one person started cannot be finished by another.
/// </summary>
public static class CustomLinkEndpoints
{
    public const string LinkStartedEvent = "identity.custom.link_started";
    public const string LinkedEvent = "identity.custom.linked";
    public const string LinkRejectedEvent = "identity.custom.link_rejected";
    public const string LinkFailedEvent = "identity.custom.link_failed";
    public const string UnlinkedEvent = UnlinkAccount.CustomUnlinkedEvent;

    public static string ReturnPath(Guid providerId) => $"/api/v1/auth/custom/{providerId}/return";

    public static IEndpointRouteBuilder MapCustomLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth/custom/{providerId:guid}").WithTags("Cloud Identity");

        // GET /api/v1/auth/custom/{providerId}/connect — a page that posts the link token to the middle man's
        // linking page. A form POST, not a query string, keeps the token out of logs, history and Referer.
        group.MapGet("/connect", async (
            Guid providerId,
            HttpContext http,
            [FromServices] CustomLinkService links,
            [FromServices] IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (UserIdOf(http) is not { } userId) return Results.Unauthorized();

            CustomLinkTarget? target = await links.TargetAsync(providerId, ct);
            if (target is null) return Results.Redirect("/profile/integrations?error=custom_link_unavailable");

            string issuer = ConnapseAddress(http);
            string token = links.CreateLinkToken(target, userId, issuer, issuer + ReturnPath(providerId));
            await audit.LogAsync(LinkStartedEvent, "user", userId.ToString(), new { ProviderId = providerId }, ct);

            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            return Results.Content(AutoPostPage(target.Provider.Name, target.LinkUrl, token), "text/html; charset=utf-8");
        }).RequireAuthorization();

        // GET /api/v1/auth/custom/{providerId}/return?code=… — the middle man's redirect after the user signed in
        // there. The session cookie (SameSite=Lax) travels on this top-level GET; without a session the user signs
        // in first and lands back here.
        group.MapGet("/return", async (
            Guid providerId,
            string? code,
            HttpContext http,
            [FromServices] CustomLinkService links,
            [FromServices] IAuditLogger audit,
            [FromServices] ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (UserIdOf(http) is not { } userId) return Results.Unauthorized();
            if (string.IsNullOrEmpty(code))
                return await FailAsync(audit, userId, providerId, "custom_link_failed", ct);

            CustomLinkStatus? status;
            try
            {
                status = await links.ConfirmAsync(providerId, code, userId, ct);
            }
            catch (Exception ex) when (ex is CustomSourceException or HttpRequestException)
            {
                // Logged, never echoed: the code is a credential until spent.
                loggerFactory.CreateLogger("CustomLinkEndpoints").LogWarning(ex, "Confirming a link with custom provider {ProviderId} failed", providerId);
                return await FailAsync(audit, userId, providerId, "custom_link_failed", ct);
            }

            if (status is null)
            {
                await audit.LogAsync(LinkRejectedEvent, "user", userId.ToString(), new { ProviderId = providerId }, ct);
                return Results.Redirect("/profile/integrations?error=custom_link_rejected");
            }

            await audit.LogAsync(LinkedEvent, "user", userId.ToString(), new { ProviderId = providerId, status.DisplayName }, ct);
            return Results.Redirect("/profile/integrations?linked=custom");
        }).RequireAuthorization();

        // DELETE /api/v1/auth/custom/{providerId} — removes the signed-in user's link from the middle man.
        group.MapDelete("/", async (
            Guid providerId,
            HttpContext http,
            [FromServices] UnlinkAccount unlink,
            CancellationToken ct) =>
        {
            // The same links.unlink the provider's card on the profile page runs (#866).
            var unlinked = await unlink.RunAsync(new OperationContext(http.User, OperationSurface.Rest), new UnlinkAccountRequest("custom", providerId), ct);
            return unlinked.Succeeded ? Results.NoContent() : OperationProblems.Result(unlinked);
        }).RequireAuthorization(Policies.Person);

        return app;
    }

    private static async Task<IResult> FailAsync(IAuditLogger audit, Guid userId, Guid providerId, string reason, CancellationToken ct)
    {
        await audit.LogAsync(LinkFailedEvent, "user", userId.ToString(), new { ProviderId = providerId, Reason = reason }, ct);
        return Results.Redirect($"/profile/integrations?error={reason}");
    }

    private static Guid? UserIdOf(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid id) ? id : null;

    /// <summary>This Connapse's address as the browser reaches it: the <c>iss</c> of link tokens.</summary>
    private static string ConnapseAddress(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";

    private static string AutoPostPage(string providerName, string linkUrl, string token)
    {
        string name = WebUtility.HtmlEncode(providerName);
        return $$"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><title>Linking {{name}} - Connapse</title></head>
            <body>
            <form id="link" method="post" action="{{WebUtility.HtmlEncode(linkUrl)}}">
            <input type="hidden" name="token" value="{{WebUtility.HtmlEncode(token)}}">
            <p>Taking you to {{name}} to link your account…</p>
            <noscript><button type="submit">Continue to {{name}}</button></noscript>
            </form>
            <script>document.getElementById('link').submit();</script>
            </body>
            </html>
            """;
    }
}
