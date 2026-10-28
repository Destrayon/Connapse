using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Connections;
using Connapse.Application.Sources;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Web.Components.Providers;
using Connapse.Web.Problems;
using Connapse.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public sealed record ResolveAtlassianSiteRequest(string? SiteUrl);

/// <param name="SaveAnyway">Save even though the site has no page or no other user yet to check administrator access against.</param>
/// <param name="Product">What the connection reads: <c>confluence</c> (when left out) or <c>jira</c>.</param>
public sealed record CreateAtlassianSiteRequest(
    string? SiteUrl, string? ClientId, string? ClientSecret, bool? SaveAnyway = null, string? Product = null)
{
    // The generated ToString would print the secret into any log line or message it reached.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"SiteUrl = {SiteUrl}, ClientId = {ClientId}, ClientSecret = ***, SaveAnyway = {SaveAnyway}, Product = {Product}");
        return true;
    }
}

/// <summary>A new service-account credential for a saved site, tested before it replaces the stored one.</summary>
public sealed record ReplaceAtlassianCredentialsRequest(string? ClientId, string? ClientSecret, bool? SaveAnyway = null)
{
    // The generated ToString would print the secret into any log line or message it reached.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"ClientId = {ClientId}, ClientSecret = ***, SaveAnyway = {SaveAnyway}");
        return true;
    }
}

/// <summary>A new name for a saved site: the Name field on the Connections page.</summary>
public sealed record RenameAtlassianSiteRequest(string? Name);

/// <summary>Which of a paused site's sources to turn back on; all of them when <c>sourceIds</c> is left out.</summary>
public sealed record ResumeAtlassianSiteRequest(IReadOnlyList<Guid>? SourceIds = null);

/// <summary>The linking app's client ID and secret, checked with Atlassian before they are stored.</summary>
/// <param name="ReplaceExisting">
/// Must be true when an app is already stored: the confirmation the Providers page asks for with
/// "Check and replace", since every new sign-in then goes through the new app.
/// </param>
public sealed record SaveAtlassianLinkAppRequest(string? ClientId, string? ClientSecret, bool? ReplaceExisting = null)
{
    // The generated ToString would print the secret into any log line or message it reached.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"ClientId = {ClientId}, ClientSecret = ***, ReplaceExisting = {ReplaceExisting}");
        return true;
    }
}

/// <summary>Turns Atlassian sign-in on or off, as the Linking app card's toggle does.</summary>
public sealed record SetAtlassianSignInRequest(bool? SignInEnabled);

/// <summary>
/// A saved site as the REST API returns it: everything the Connections page shows, never the secret.
/// <c>scopes</c> is included only for a single site. <c>product</c> is what the connection reads:
/// <c>confluence</c> or <c>jira</c>.
/// </summary>
public sealed record AtlassianSiteView(
    Guid Id,
    string Name,
    string? SiteUrl,
    string? CloudId,
    string? ClientId,
    string Product,
    bool HasSecret,
    int SourceCount,
    bool SyncPaused,
    AtlassianSiteHealth? Health,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<AtlassianScopeView>? Scopes = null);

/// <summary>A saved site's last check, as the Connections page shows it.</summary>
public sealed record AtlassianSiteHealth(
    DateTime CheckedAt,
    bool Succeeded,
    bool Warning,
    string? Step,
    string? StepLabel,
    string? Message,
    string Origin,
    bool CredentialRejected,
    DateTime? LastSucceededAt,
    IReadOnlyList<string>? ConfirmedScopes);

/// <summary>A permission a site's credential needs, and why.</summary>
public sealed record AtlassianScopeView(string Name, string Reason, bool Optional, string Product);

public sealed record AtlassianSiteResolved(string? SiteUrl, string? CloudId);

/// <summary>A site just saved: the connection, and the test that ran before it was.</summary>
public sealed record AtlassianSiteCreated(
    Guid Id, string Name, string? SiteUrl, string? CloudId, string? ClientId, string Product, AtlassianTestReport Test);

/// <summary>A site's new credential, stored after it passed the test.</summary>
public sealed record AtlassianCredentialsReplaced(Guid Id, string Name, string? ClientId, AtlassianTestReport Test);

/// <summary>Whether a site's syncing is paused now, and how many of its sources the change touched.</summary>
public sealed record AtlassianSyncState(bool SyncPaused, int Sources);

/// <summary>The stored linking app, as the Linking app card shows it. Never the secret.</summary>
public sealed record AtlassianLinkAppView(
    string ClientId,
    string Permission,
    bool SignInEnabled,
    DateTime? LastCheckedAt,
    bool? LastCheckAccepted,
    DateTime? LastUsedForSignInAt,
    int? PeopleLinked);

/// <summary>What Atlassian said of the stored linking app: <c>accepted</c>, <c>rejected</c> or <c>unknown</c>.</summary>
public sealed record AtlassianLinkAppCheckResponse(string Result, string Message, string? TroubleshootingUrl, string? Detail);

/// <summary>A linking app Atlassian rejected or couldn't be asked about; <c>detail</c> is Atlassian's own answer.</summary>
public sealed record AtlassianCheckFailure(string Error, string TroubleshootingUrl, string? Detail);

/// <summary>A connection that can't be deleted, and the sources still using it.</summary>
public sealed record ConnectionInUseFailure(string Error, IReadOnlyList<SourceLink> Sources);

public sealed record SourceLink(Guid Id, string Name, string Url);

// The spec's shapes for two problems that carry more than ProblemDetails' members. Documentation only: the
// bodies are built from AtlassianTestReport and ConnectionInUseFailure, and spec conformance checks them.

/// <summary>A site whose test failed: the problem, and the test report's members beside it.</summary>
public sealed record AtlassianTestFailedProblem(
    string? Type,
    string? Title,
    int? Status,
    string? Detail,
    string Error,
    bool Success,
    bool Warning,
    string Message,
    string? Step,
    string? StepLabel,
    string? TroubleshootingUrl,
    IReadOnlyList<string> Scopes,
    string? TraceId);

/// <summary>A connection still in use: the problem, and the sources using it.</summary>
public sealed record ConnectionInUseProblem(
    string? Type, string? Title, int? Status, string? Detail, string Error, IReadOnlyList<SourceLink> Sources, string? TraceId);

/// <summary>
/// The Atlassian linking app over REST, at parity with the Linking app card on the Providers page:
/// the same check with Atlassian before saving, the same typed confirmation to remove it, and the
/// same audit events, all through <see cref="AtlassianLinkAppService"/>. The secret is accepted on
/// the way in and never returned.
/// </summary>
public static class AtlassianLinkAppEndpoints
{
    public static IEndpointRouteBuilder MapAtlassianLinkAppEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/atlassian/linking-app").WithTags("Providers")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // GET /api/v1/atlassian/linking-app — the stored app: client ID, whether sign-in is on, the
        // last check, the last sign-in through it and how many people linked. Never the secret.
        group.MapGet("/", async ([FromServices] AtlassianLinkAppService linkApp, CancellationToken ct) =>
            await linkApp.GetAsync(ct) is { } stored
                ? Results.Ok(await ViewAsync(linkApp, stored, ct))
                : ApiProblem.NotFound("No linking app is stored."))
            .RequireAuthorization("RequireAdmin")
        .Produces<AtlassianLinkAppView>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        // PUT /api/v1/atlassian/linking-app — checks the client ID and secret with Atlassian and,
        // only when Atlassian accepts them, stores them. Replacing a stored app needs
        // replaceExisting: true, as the page needs "Check and replace".
        group.MapPut("/", async (
            HttpContext http,
            [FromBody] SaveAtlassianLinkAppRequest request,
            [FromServices] AtlassianLinkAppService linkApp,
            CancellationToken ct) =>
        {
            // Checked before Atlassian is asked, so an unconfirmed replace costs nothing and changes nothing.
            if (!string.IsNullOrWhiteSpace(request.ClientId) && !string.IsNullOrWhiteSpace(request.ClientSecret)
                && request.ReplaceExisting != true && await linkApp.GetAsync(ct) is { } stored)
            {
                int? linked = await linkApp.CountLinkedPeopleAsync(ct);
                string count = linked is { } n ? $" ({n})" : "";
                return ApiProblem.Conflict(
                    $"A linking app is already stored (client ID {stored.ClientId}). Replacing it sends every new "
                    + $"sign-in through the new app; everyone who linked an Atlassian account{count} keeps their link. "
                    + "Send replaceExisting: true to replace it.",
                    field: "replaceExisting");
            }

            var saved = await linkApp.CheckAndSaveAsync(request.ClientId, request.ClientSecret,
                CloudIdentityEndpoints.AtlassianCallbackUrl(http), UserIdOf(http), ct);
            // detail is Atlassian's own answer (status and error code), as the card's Details shows it; never the secret.
            return saved.Outcome switch
            {
                AtlassianLinkAppSaveOutcome.MissingFields =>
                    ApiProblem.BadRequest("clientId and clientSecret are required."),
                AtlassianLinkAppSaveOutcome.Rejected => Results.UnprocessableEntity(new AtlassianCheckFailure(
                    "Atlassian rejected this client ID and secret, so nothing was saved. Copy both again from Settings on the app in the developer console.",
                    AtlassianLinkAppFieldSpecs.TroubleshootingRejected,
                    saved.Detail)),
                AtlassianLinkAppSaveOutcome.Unreachable => Results.Json(new AtlassianCheckFailure(
                    "Couldn't reach Atlassian to check the app, so nothing was saved. Check that this server can reach auth.atlassian.com over HTTPS, then try again.",
                    AtlassianLinkAppFieldSpecs.Guide + "#couldnt-reach-atlassian",
                    saved.Detail), statusCode: StatusCodes.Status502BadGateway),
                _ => Results.Ok(await ViewAsync(linkApp, (await linkApp.GetAsync(ct))!, ct)),
            };
        }).RequireAuthorization("RequireAdmin")
        .Produces<AtlassianLinkAppView>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status502BadGateway);

        // POST /api/v1/atlassian/linking-app/check — asks Atlassian again whether it accepts the
        // stored client ID and secret, and records the answer as the last check.
        group.MapPost("/check", async (
            HttpContext http,
            [FromServices] AtlassianLinkAppService linkApp,
            CancellationToken ct) =>
        {
            var check = await linkApp.RecheckWithDetailAsync(CloudIdentityEndpoints.AtlassianCallbackUrl(http), ct);
            // detail is Atlassian's own answer (status and error code), as the card's Details shows it; never the secret.
            return check?.Result switch
            {
                null => ApiProblem.NotFound("No linking app is stored."),
                AtlassianLinkAppCheck.Accepted => Results.Ok(new AtlassianLinkAppCheckResponse(
                    "accepted",
                    "Atlassian accepted the stored client ID and secret.",
                    null,
                    check.Detail)),
                AtlassianLinkAppCheck.Rejected => Results.Ok(new AtlassianLinkAppCheckResponse(
                    "rejected",
                    "Atlassian rejected the stored client ID or secret, so nobody new can link an account. Save a new client ID and secret.",
                    AtlassianLinkAppFieldSpecs.TroubleshootingRejected,
                    check.Detail)),
                _ => Results.Ok(new AtlassianLinkAppCheckResponse(
                    "unknown",
                    "Couldn't reach Atlassian to check the app. Nothing was changed. Check that this server can reach auth.atlassian.com over HTTPS, then try again.",
                    AtlassianLinkAppFieldSpecs.Guide + "#couldnt-reach-atlassian",
                    check.Detail)),
            };
        }).RequireAuthorization("RequireAdmin")
        .Produces<AtlassianLinkAppCheckResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        // PUT /api/v1/atlassian/linking-app/sign-in — turns Atlassian sign-in off (no new links;
        // existing ones kept) or back on, without touching the stored app.
        group.MapPut("/sign-in", async (
            [FromBody] SetAtlassianSignInRequest request,
            [FromServices] AtlassianLinkAppService linkApp,
            CancellationToken ct) =>
        {
            if (request.SignInEnabled is not { } enabled)
                return ApiProblem.BadRequest("signInEnabled is required.");
            if (!await linkApp.SetSignInEnabledAsync(enabled, ct))
                return ApiProblem.NotFound("No linking app is stored.");
            return Results.Ok(await ViewAsync(linkApp, (await linkApp.GetAsync(ct))!, ct));
        }).RequireAuthorization("RequireAdmin")
        .Produces<AtlassianLinkAppView>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        // DELETE /api/v1/atlassian/linking-app?confirmClientId=... — removes the app. People who
        // linked keep their links. confirmClientId must be the stored client ID, the value the page
        // asks an administrator to type.
        group.MapDelete("/", async (
            [FromQuery] string? confirmClientId,
            [FromServices] AtlassianLinkAppService linkApp,
            CancellationToken ct) =>
        {
            return await linkApp.RemoveAsync(confirmClientId, ct) switch
            {
                AtlassianLinkAppRemoveOutcome.NotStored => ApiProblem.NotFound("No linking app is stored."),
                AtlassianLinkAppRemoveOutcome.ConfirmMismatch =>
                    ApiProblem.BadRequest("confirmClientId must be the stored app's client ID.", field: "confirmClientId"),
                _ => Results.NoContent(),
            };
        }).RequireAuthorization("RequireAdmin")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<AtlassianLinkAppView> ViewAsync(
        AtlassianLinkAppService linkApp, AtlassianLinkAppRegistration stored, CancellationToken ct) => new(
            stored.ClientId,
            "read:me",
            stored.SignInEnabled,
            stored.CheckedAt,
            stored.CheckAccepted,
            stored.VerifiedAt,
            await linkApp.CountLinkedPeopleAsync(ct));

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);

    private static Guid? UserIdOf(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid id) ? id : null;
}

/// <summary>
/// An Atlassian site over REST, at parity with the Connections page: the same fields (siteUrl,
/// clientId, clientSecret), the same tests before anything is saved, and the same step labels and
/// troubleshooting links in a failure. The secret is accepted on the way in and never returned.
/// </summary>
public static class AtlassianEndpoints
{
    public static IEndpointRouteBuilder MapAtlassianEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/atlassian/sites").WithTags("Providers")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // POST /api/v1/atlassian/sites/resolve — a site address to its cloud id, through the site's
        // own unauthenticated tenant_info. The address is validated as *.atlassian.net first, so
        // this cannot be pointed at another host.
        group.MapPost("/resolve", async (
            [FromBody] ResolveAtlassianSiteRequest request,
            [FromServices] AtlassianSiteService sites,
            CancellationToken ct) =>
        {
            var resolved = await sites.ResolveAsync(request.SiteUrl, ct);
            return resolved.Outcome switch
            {
                AtlassianResolveOutcome.InvalidAddress =>
                    ApiProblem.BadRequest("The site address must look like https://your-site.atlassian.net."),
                AtlassianResolveOutcome.NotFound =>
                    ApiProblem.NotFound($"Couldn't find an Atlassian Cloud site at {resolved.SiteUrl}."),
                _ => Results.Ok(new AtlassianSiteResolved(resolved.SiteUrl, resolved.CloudId)),
            };
        }).RequireAuthorization("RequireAdmin")
        .Produces<AtlassianSiteResolved>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        // POST /api/v1/atlassian/sites — tests the service account, and only when every probe
        // passes (or saveAnyway covers a warning) stores the site as a connection (the secret
        // encrypted, never echoed back).
        group.MapPost("/", async (
            HttpContext http,
            [FromBody] CreateAtlassianSiteRequest request,
            [FromServices] AtlassianSiteService sites,
            CancellationToken ct) =>
        {
            AtlassianProduct? product = string.IsNullOrWhiteSpace(request.Product)
                ? AtlassianProduct.Confluence
                : AtlassianSite.ProductOf(request.Product.Trim());
            if (product is null)
                return ApiProblem.BadRequest("product must be \"confluence\" or \"jira\".", field: "product");

            var added = await sites.AddAsync(Rest(http), request.SiteUrl, request.ClientId, request.ClientSecret,
                request.SaveAnyway ?? false, product.Value, ct);

            return added.Outcome switch
            {
                AtlassianAddOutcome.MissingFields =>
                    ApiProblem.BadRequest("siteUrl, clientId and clientSecret are required."),
                AtlassianAddOutcome.TestFailed => Results.UnprocessableEntity(Report(added.Test!)),
                AtlassianAddOutcome.Conflict =>
                    ApiProblem.Conflict($"A connection named '{added.Name}' already exists."),
                AtlassianAddOutcome.NameTaken => ApiProblem.Conflict(added.Refusal!),
                AtlassianAddOutcome.Forbidden => ApiProblem.Forbidden(added.Refusal!),
                _ => Results.Created($"/api/v1/atlassian/sites/{added.Connection!.Id}",
                    new AtlassianSiteCreated(
                        added.Connection.Id,
                        added.Connection.Name,
                        added.SiteUrl,
                        added.CloudId,
                        added.ClientId,
                        AtlassianSite.ProductValue(product.Value),
                        Report(added.Test!))),
            };
        }).RequireAuthorization("RequireAdmin")
        .Produces<AtlassianSiteCreated>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .Produces<AtlassianTestFailedProblem>(StatusCodes.Status422UnprocessableEntity, "application/problem+json");

        // GET /api/v1/atlassian/sites — every saved site, as the Connections page lists them.
        group.MapGet("/", async (
            [FromServices] ListAtlassianSites list,
            HttpContext http,
            CancellationToken ct) =>
        {
            var listed = await list.RunAsync(Rest(http), ct);
            return listed.Succeeded
                ? Results.Ok(listed.Result!.Items.Select(v => SiteView(v.Connection, v.SyncPaused)))
                : OperationProblems.Result(listed);
        }).RequireAuthorization("RequireSourcesAdmin")
        .Produces<IReadOnlyList<AtlassianSiteView>>();

        // GET /api/v1/atlassian/sites/{connectionId} — the site, its last check, and the scopes it needs.
        group.MapGet("/{connectionId:guid}", async (
            Guid connectionId,
            [FromServices] GetAtlassianSite get,
            HttpContext http,
            CancellationToken ct) =>
        {
            var read = await get.RunAsync(Rest(http), connectionId, ct);
            if (!read.Succeeded)
                return OperationProblems.Result(read);

            // A connection's credential needs its own product's scopes only.
            AtlassianSiteView view = SiteView(read.Result!.Connection, read.Result.SyncPaused);
            return Results.Ok(view with
            {
                Scopes = AtlassianSiteScopes.Of(view.Product).Select(s => new AtlassianScopeView(s.Name, s.Reason, s.Optional, s.Product)).ToList(),
            });
        }).RequireAuthorization("RequireSourcesAdmin")
        .Produces<AtlassianSiteView>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        // PATCH /api/v1/atlassian/sites/{connectionId} — renames the site, as the Name field on the
        // Connections page does. Audited as connection.updated.
        group.MapPatch("/{connectionId:guid}", async (
            Guid connectionId,
            [FromBody] RenameAtlassianSiteRequest request,
            [FromServices] RenameAtlassianSite rename,
            HttpContext http,
            CancellationToken ct) =>
        {
            var renamed = await rename.RunAsync(Rest(http), connectionId, request.Name, ct);
            return renamed.Succeeded
                ? Results.Ok(SiteView(renamed.Result!.Connection, renamed.Result.SyncPaused))
                : OperationProblems.Result(renamed);
        }).RequireAuthorization("RequireSourcesAdmin")
        .Produces<AtlassianSiteView>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // POST /api/v1/atlassian/sites/{connectionId}/pause — stops syncing every source on the site.
        // Nothing is deleted, and the documents stay searchable.
        group.MapPost("/{connectionId:guid}/pause", async (
            Guid connectionId,
            [FromServices] PauseAtlassianSite pause,
            HttpContext http,
            CancellationToken ct) =>
        {
            var paused = await pause.RunAsync(Rest(http), connectionId, ct);
            return paused.Succeeded
                ? Results.Ok(new AtlassianSyncState(paused.Result!.SyncPaused, paused.Result.Sources))
                : OperationProblems.Result(paused);
        }).RequireAuthorization("RequireSourcesAdmin")
        .Produces<AtlassianSyncState>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // POST /api/v1/atlassian/sites/{connectionId}/resume — turns syncing back on for the sources
        // named in sourceIds, or every source on the site when it is left out.
        group.MapPost("/{connectionId:guid}/resume", async (
            Guid connectionId,
            [FromBody] ResumeAtlassianSiteRequest? request,
            [FromServices] ResumeAtlassianSite resume,
            HttpContext http,
            CancellationToken ct) =>
        {
            var resumed = await resume.RunAsync(Rest(http), connectionId, request?.SourceIds, ct);
            return resumed.Succeeded
                ? Results.Ok(new AtlassianSyncState(resumed.Result!.SyncPaused, resumed.Result.Sources))
                : OperationProblems.Result(resumed);
        }).RequireAuthorization("RequireSourcesAdmin")
        .Produces<AtlassianSyncState>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // DELETE /api/v1/atlassian/sites/{connectionId}?confirmName=... — deletes the site. Refused
        // while any source uses it; confirmName must be the site's name, the value the page asks an
        // administrator to type. Audited as connection.deleted.
        group.MapDelete("/{connectionId:guid}", async (
            Guid connectionId,
            [FromQuery] string? confirmName,
            [FromServices] DeleteAtlassianSite delete,
            [FromServices] ListSources sources,
            HttpContext http,
            CancellationToken ct) =>
        {
            var deleted = await delete.RunAsync(Rest(http), connectionId, confirmName, ct);
            return deleted.Succeeded
                ? Results.NoContent()
                : await ConnectionsEndpoints.Failed(deleted, connectionId, sources, http, ct);
        }).RequireAuthorization("RequireSourcesAdmin")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .Produces<ConnectionInUseProblem>(StatusCodes.Status409Conflict, "application/problem+json");

        // POST /api/v1/atlassian/sites/{connectionId}/test — every probe again with the stored
        // secret. The result is recorded as the site's health either way.
        group.MapPost("/{connectionId:guid}/test", async (
            Guid connectionId,
            [FromServices] AtlassianSiteService sites,
            CancellationToken ct) =>
        {
            var result = await sites.RetestAsync(connectionId, ct);
            return result is null
                ? ApiProblem.NotFound("No such Atlassian site, or its stored credential can't be read.")
                : Results.Ok(Report(result));
        }).RequireAuthorization("RequireAdmin")
        .Produces<AtlassianTestReport>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        // PUT /api/v1/atlassian/sites/{connectionId}/credentials — tests a new client ID and secret
        // against the site and, only when they pass, stores them in place of the old ones. The
        // connection keeps its id, so its sources keep syncing.
        group.MapPut("/{connectionId:guid}/credentials", async (
            Guid connectionId,
            HttpContext http,
            [FromBody] ReplaceAtlassianCredentialsRequest request,
            [FromServices] AtlassianSiteService sites,
            CancellationToken ct) =>
        {
            var replaced = await sites.ReplaceCredentialsAsync(new OperationContext(http.User, OperationSurface.Rest), connectionId,
                request.ClientId, request.ClientSecret, request.SaveAnyway ?? false, ct);

            return replaced.Outcome switch
            {
                AtlassianReplaceOutcome.Forbidden => ApiProblem.Forbidden(replaced.Refusal!),
                AtlassianReplaceOutcome.Changed => ApiProblem.Conflict(replaced.Refusal!),
                AtlassianReplaceOutcome.NotFound => ApiProblem.NotFound("No such Atlassian site."),
                AtlassianReplaceOutcome.MissingFields =>
                    ApiProblem.BadRequest("clientId and clientSecret are required."),
                AtlassianReplaceOutcome.TestFailed => Results.UnprocessableEntity(Report(replaced.Test!)),
                AtlassianReplaceOutcome.SiteChanged => ApiProblem.Conflict(
                    "The site at this address has a different Cloud ID than when it was added, so it is a different site. "
                    + "Add it as a new connection."),
                _ => Results.Ok(new AtlassianCredentialsReplaced(
                    replaced.Connection!.Id,
                    replaced.Connection.Name,
                    AtlassianSite.FromConfigJson(replaced.Connection.ConfigJson)?.ClientId,
                    Report(replaced.Test!))),
            };
        }).RequireAuthorization("RequireSourcesOwner")
        .Produces<AtlassianCredentialsReplaced>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .Produces<AtlassianTestFailedProblem>(StatusCodes.Status422UnprocessableEntity, "application/problem+json");

        // GET /api/v1/atlassian/sites/{connectionId}/spaces — the site's current Confluence spaces,
        // read as its service account, for choosing which to turn into sources. Personal spaces
        // are left out unless asked for.
        group.MapGet("/{connectionId:guid}/spaces", async (
            Guid connectionId,
            [FromQuery] bool? includePersonal,
            [FromServices] AtlassianSiteService sites,
            CancellationToken ct) =>
        {
            var listed = await sites.ListSpacesAsync(connectionId, includePersonal ?? false, ct);
            return listed.Outcome switch
            {
                AtlassianSpacesOutcome.NotFound => ApiProblem.NotFound("No such connection."),
                AtlassianSpacesOutcome.NotAtlassian =>
                    ApiProblem.BadRequest("That connection is not an Atlassian site."),
                AtlassianSpacesOutcome.WrongProduct =>
                    ApiProblem.BadRequest(listed.Error!, field: "connectionId"),
                AtlassianSpacesOutcome.Unavailable =>
                    ApiProblem.BadGateway(listed.Error!),
                _ => Results.Ok(listed.Spaces!),
            };
        }).RequireAuthorization("RequireAdmin")
        .Produces<IReadOnlyList<ConfluenceSpaceInfo>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status502BadGateway);

        // GET /api/v1/atlassian/sites/{connectionId}/projects — the site's live Jira projects the
        // service account can browse, for choosing which to turn into sources.
        group.MapGet("/{connectionId:guid}/projects", async (
            Guid connectionId,
            [FromServices] AtlassianSiteService sites,
            CancellationToken ct) =>
        {
            var listed = await sites.ListProjectsAsync(connectionId, ct);
            return listed.Outcome switch
            {
                AtlassianSpacesOutcome.NotFound => ApiProblem.NotFound("No such connection."),
                AtlassianSpacesOutcome.NotAtlassian =>
                    ApiProblem.BadRequest("That connection is not an Atlassian site."),
                AtlassianSpacesOutcome.WrongProduct =>
                    ApiProblem.BadRequest(listed.Error!, field: "connectionId"),
                AtlassianSpacesOutcome.Unavailable =>
                    ApiProblem.BadGateway(listed.Error!),
                _ => Results.Ok(listed.Projects!),
            };
        }).RequireAuthorization("RequireAdmin")
        .Produces<IReadOnlyList<JiraProjectInfo>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status502BadGateway);

        return app;
    }

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);

    private static Guid? UserIdOf(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid id) ? id : null;

    private static AtlassianSiteView SiteView(Connection connection, bool syncPaused)
    {
        AtlassianSite? site = AtlassianSite.FromConfigJson(connection.ConfigJson);
        return new AtlassianSiteView(
            connection.Id,
            connection.Name,
            site?.SiteUrl,
            site?.CloudId,
            site?.ClientId,
            AtlassianSite.ProductValue(site?.Product ?? AtlassianProduct.Confluence),
            connection.HasSecret,
            connection.SourceCount,
            syncPaused,
            connection.Health is { } h
                ? new AtlassianSiteHealth(
                    h.CheckedAt,
                    h.Succeeded,
                    h.Warning,
                    h.Step,
                    h.Step is null ? null : AtlassianConnectionTester.StepLabel(h.Step),
                    h.Message,
                    h.Origin,
                    h.CredentialRejected,
                    h.LastSucceededAt,
                    h.ConfirmedScopes)
                : null);
    }

    // The report itself: its members are exactly the ones the API has always returned, in the same order.
    private static AtlassianTestReport Report(ConnectionTestResult result) => AtlassianTestReport.From(result);
}
