using Connapse.Application;
using Connapse.Application.Providers;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public static class GitHubAppEndpoints
{
    /// <summary>Where the administrator lands, with the outcome in the query string.</summary>
    private const string ProviderPage = "/admin/providers/github";

    public static IEndpointRouteBuilder MapGitHubAppEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/providers/github").WithTags("Providers")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // GET /api/v1/providers/github/app — the stored App, without its key or secret
        group.MapGet("/app", async ([FromServices] GetGitHubApp read, HttpContext http, CancellationToken ct) =>
        {
            var stored = await read.RunAsync(Rest(http), ct);
            return stored.Succeeded ? Results.Ok(stored.Result) : OperationProblems.Result(stored);
        }).RequireAuthorization("RequireSettingsAdmin")
        .Produces<GitHubAppView>()
        .WithName("providers.github.app.get")
        .WithDescription("Read the stored GitHub App: its id, slug, client id, owner and address, with when it was saved and last verified. "
            + "The private key and client secret are never returned. Both fields are null when no App is stored.");

        // PUT /api/v1/providers/github/app — check a hand-registered App with GitHub, then store it
        group.MapPut("/app", async ([FromServices] SetGitHubApp set, [FromBody] GitHubAppRequest request, HttpContext http, CancellationToken ct) =>
        {
            var saved = await set.RunAsync(Rest(http), request, ct);
            return saved.Succeeded ? Results.Ok(saved.Result) : OperationProblems.Result(saved);
        }).RequireAuthorization("RequireSettingsAdmin")
        .Produces<GitHubAppSaved>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .WithName("providers.github.app")
        .WithDescription("Store a GitHub App registered by hand: appId, privateKeyPem and, for people to link their GitHub accounts, "
            + "clientSecret. GitHub is asked who the id and key belong to, and whether the secret is the App's, before anything is stored. "
            + "A blank key or secret keeps the stored one for the same App; replacing a different App needs replaceExisting true. "
            + "Secrets are never returned.");

        // DELETE /api/v1/providers/github/app — remove the stored App
        group.MapDelete("/app", async ([FromServices] RemoveGitHubApp remove, HttpContext http, CancellationToken ct) =>
        {
            var removed = await remove.RunAsync(Rest(http), ct);
            return removed.Succeeded ? Results.Ok(removed.Result) : OperationProblems.Result(removed);
        }).RequireAuthorization("RequireSettingsAdmin")
        .Produces<GitHubAppRemoved>()
        .WithName("providers.github.app.remove")
        .WithDescription("Remove the GitHub App's key and secret from Connapse. Every GitHub connection and source stops syncing, and "
            + "linked GitHub accounts no longer unlock private repositories. The App itself stays on GitHub.");

        // POST /api/v1/providers/github/manifest — start creating the App; the rest happens in a browser
        group.MapPost("/manifest", async ([FromServices] StartGitHubAppCreation create, [FromBody] GitHubAppCreationRequest? request,
            HttpContext http, CancellationToken ct) =>
        {
            string baseUrl = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/";
            var started = await create.RunAsync(Rest(http), baseUrl, request ?? new GitHubAppCreationRequest(), ct);
            return started.Succeeded ? Results.Ok(started.Result) : OperationProblems.Result(started);
        }).RequireAuthorization("RequireSettingsAdmin")
        .Produces<GitHubAppCreation>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .WithName("providers.github.createApp")
        .WithDescription("Start creating Connapse's GitHub App through GitHub's manifest flow, owned by the caller's account or by "
            + "organisation, which GitHub must confirm is one. Creating it needs a browser: a person signed in to both Connapse and GitHub "
            + "opens setupPageUrl and clicks once, or a browser posts a form with one field, manifest, to formAction. GitHub then sends that "
            + "person back to Connapse, which stores the App. A stored App is replaced only with replaceExisting true. Each start is good for an hour, once, and only for the person who made it. "
            + "Poll GET /api/v1/providers until the github requirement leaves Missing.");

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
            [FromServices] CompleteGitHubAppCreation complete,
            CancellationToken ct) =>
        {
            var created = await complete.RunAsync(Rest(http), code, state, ct);
            return created.Status switch
            {
                GitHubAppStatus.Success => Results.Redirect(ProviderPage + "?github_created=1"),
                GitHubAppStatus.Expired => Results.Redirect(ProviderPage + "?github_error=expired"),
                GitHubAppStatus.NoCode => Results.Redirect(ProviderPage + "?github_error=no_code"),
                // An App stored since the start is kept: the new one, created on GitHub, isn't collected either way.
                GitHubAppStatus.Unavailable or GitHubAppStatus.ReplaceUnconfirmed or GitHubAppStatus.Changed => Results.Redirect(ProviderPage + "?github_error=exchange_failed"),
                _ => OperationProblems.Result(created),
            };
        }).RequireAuthorization("RequireSettingsAdmin")
        .Produces(StatusCodes.Status302Found)
        .WithName("providers.github.createApp.callback");

        // GET /api/v1/providers/github/installed — GitHub's redirect after the App is installed
        // (or an install is requested for an organisation's owners to approve). Installing is a
        // connection's business, so this forwards to the Connections page with the installation.
        group.MapGet("/installed", async (
            HttpContext http,
            [FromQuery(Name = "installation_id")] long? installationId,
            [FromQuery(Name = "setup_action")] string? setupAction,
            [FromServices] RecordGitHubAppInstalled installed,
            CancellationToken ct) =>
        {
            var next = await installed.RunAsync(Rest(http), installationId, setupAction, ct);
            return next.Succeeded ? Results.Redirect(next.Result!) : OperationProblems.Result(next);
        }).RequireAuthorization("RequireAdmin")
        .Produces(StatusCodes.Status302Found)
        .WithName("providers.github.installed");

        return app;
    }

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);
}
