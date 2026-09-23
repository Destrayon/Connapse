using System.Security.Claims;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.GitHub;
using Connapse.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public static class GitHubAppEndpoints
{
    /// <summary>Where the administrator lands, with the outcome in the query string.</summary>
    private const string ProviderPage = "/admin/providers/github";

    public static IEndpointRouteBuilder MapGitHubAppEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/providers/github").WithTags("Providers");

        // GET /api/v1/providers/github/manifest/callback — GitHub's redirect after the
        // administrator confirms the App on github.com.
        //
        // An admin-only, top-level GET: the session cookie travels on it (SameSite=Lax permits a
        // top-level navigation), so the endpoint can require the same administrator who started
        // the flow rather than parking the result anonymously. `state` must name a request that
        // administrator started within the hour; anything else — a replayed, planted, or expired
        // redirect — is refused before the one-time code is spent.
        group.MapGet("/manifest/callback", async (
            HttpContext http,
            [FromQuery] string? code,
            [FromQuery] string? state,
            [FromServices] GitHubManifestRequests pending,
            [FromServices] ConnapseGitHubApp gitHubApp,
            [FromServices] IProviderCredentialStore credentials,
            [FromServices] IAuditLogger audit,
            [FromServices] ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            var logger = loggers.CreateLogger("Connapse.GitHubApp");

            if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                return Results.Unauthorized();

            if (!pending.TryComplete(state, userId))
                return Results.Redirect(ProviderPage + "?github_error=expired");

            if (string.IsNullOrWhiteSpace(code))
                return Results.Redirect(ProviderPage + "?github_error=no_code");

            try
            {
                var created = await gitHubApp.ConvertManifestAsync(code, ct);
                await credentials.SaveGitHubAppAsync(
                    created.App, created.PrivateKeyPem, created.ClientSecret, userId, ct);
                gitHubApp.ClearCache();

                await audit.LogAsync("provider.credential.saved", "provider", "github",
                    new { created.App.AppId, created.App.Slug, created.App.OwnerLogin, Via = "manifest" });

                return Results.Redirect(ProviderPage + "?github_created=1");
            }
            catch (Exception ex)
            {
                // Logged, not echoed: the query string is not a place for exception text. The code
                // is not logged either — until it is spent it is a credential for the new App.
                logger.LogError(ex, "Creating the GitHub App from its manifest failed");
                return Results.Redirect(ProviderPage + "?github_error=exchange_failed");
            }
        }).RequireAuthorization("RequireAdmin");

        // GET /api/v1/providers/github/installed — GitHub's redirect after the App is installed
        // (or an install is requested for an organisation's owners to approve). Installing is a
        // connection's business, so this forwards to the Connections page with the installation.
        // The id is only a hint: that page selects it only if the App's own list includes it.
        group.MapGet("/installed", (
            [FromQuery(Name = "installation_id")] long? installationId,
            [FromQuery(Name = "setup_action")] string? setupAction) =>
        {
            if (setupAction == "request")
                return Results.Redirect("/connections?github_install=requested");

            return Results.Redirect(installationId is > 0
                ? $"/connections?github_installation={installationId}"
                : "/connections?new=github");
        }).RequireAuthorization("RequireAdmin");

        return app;
    }
}
