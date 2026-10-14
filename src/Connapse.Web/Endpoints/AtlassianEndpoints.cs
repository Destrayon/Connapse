using System.Security.Claims;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Web.Components.Providers;
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
    object? Health,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    object? Scopes = null);

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
        var group = app.MapGroup("/api/v1/atlassian/linking-app").WithTags("Providers");

        // GET /api/v1/atlassian/linking-app — the stored app: client ID, whether sign-in is on, the
        // last check, the last sign-in through it and how many people linked. Never the secret.
        group.MapGet("/", async ([FromServices] AtlassianLinkAppService linkApp, CancellationToken ct) =>
            await linkApp.GetAsync(ct) is { } stored
                ? Results.Ok(await ViewAsync(linkApp, stored, ct))
                : Results.NotFound(new { error = "No linking app is stored." }))
            .RequireAuthorization("RequireAdmin");

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
                return Results.Conflict(new
                {
                    error = $"A linking app is already stored (client ID {stored.ClientId}). Replacing it sends every new "
                            + $"sign-in through the new app; everyone who linked an Atlassian account{count} keeps their link. "
                            + "Send replaceExisting: true to replace it.",
                    field = "replaceExisting",
                });
            }

            var saved = await linkApp.CheckAndSaveAsync(request.ClientId, request.ClientSecret,
                CloudIdentityEndpoints.AtlassianCallbackUrl(http), UserIdOf(http), ct);
            // detail is Atlassian's own answer (status and error code), as the card's Details shows it; never the secret.
            return saved.Outcome switch
            {
                AtlassianLinkAppSaveOutcome.MissingFields =>
                    Results.BadRequest(new { error = "clientId and clientSecret are required." }),
                AtlassianLinkAppSaveOutcome.Rejected => Results.UnprocessableEntity(new
                {
                    error = "Atlassian rejected this client ID and secret, so nothing was saved. Copy both again from Settings on the app in the developer console.",
                    troubleshootingUrl = AtlassianLinkAppFieldSpecs.TroubleshootingRejected,
                    detail = saved.Detail,
                }),
                AtlassianLinkAppSaveOutcome.Unreachable => Results.Json(new
                {
                    error = "Couldn't reach Atlassian to check the app, so nothing was saved. Check that this server can reach auth.atlassian.com over HTTPS, then try again.",
                    troubleshootingUrl = AtlassianLinkAppFieldSpecs.Guide + "#couldnt-reach-atlassian",
                    detail = saved.Detail,
                }, statusCode: StatusCodes.Status502BadGateway),
                _ => Results.Ok(await ViewAsync(linkApp, (await linkApp.GetAsync(ct))!, ct)),
            };
        }).RequireAuthorization("RequireAdmin");

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
                null => Results.NotFound(new { error = "No linking app is stored." }),
                AtlassianLinkAppCheck.Accepted => Results.Ok(new
                {
                    result = "accepted",
                    message = "Atlassian accepted the stored client ID and secret.",
                    troubleshootingUrl = (string?)null,
                    detail = check.Detail,
                }),
                AtlassianLinkAppCheck.Rejected => Results.Ok(new
                {
                    result = "rejected",
                    message = "Atlassian rejected the stored client ID or secret, so nobody new can link an account. Save a new client ID and secret.",
                    troubleshootingUrl = (string?)AtlassianLinkAppFieldSpecs.TroubleshootingRejected,
                    detail = check.Detail,
                }),
                _ => Results.Ok(new
                {
                    result = "unknown",
                    message = "Couldn't reach Atlassian to check the app. Nothing was changed. Check that this server can reach auth.atlassian.com over HTTPS, then try again.",
                    troubleshootingUrl = (string?)(AtlassianLinkAppFieldSpecs.Guide + "#couldnt-reach-atlassian"),
                    detail = check.Detail,
                }),
            };
        }).RequireAuthorization("RequireAdmin");

        // PUT /api/v1/atlassian/linking-app/sign-in — turns Atlassian sign-in off (no new links;
        // existing ones kept) or back on, without touching the stored app.
        group.MapPut("/sign-in", async (
            [FromBody] SetAtlassianSignInRequest request,
            [FromServices] AtlassianLinkAppService linkApp,
            CancellationToken ct) =>
        {
            if (request.SignInEnabled is not { } enabled)
                return Results.BadRequest(new { error = "signInEnabled is required." });
            if (!await linkApp.SetSignInEnabledAsync(enabled, ct))
                return Results.NotFound(new { error = "No linking app is stored." });
            return Results.Ok(await ViewAsync(linkApp, (await linkApp.GetAsync(ct))!, ct));
        }).RequireAuthorization("RequireAdmin");

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
                AtlassianLinkAppRemoveOutcome.NotStored => Results.NotFound(new { error = "No linking app is stored." }),
                AtlassianLinkAppRemoveOutcome.ConfirmMismatch => Results.BadRequest(new
                {
                    error = "confirmClientId must be the stored app's client ID.",
                    field = "confirmClientId",
                }),
                _ => Results.NoContent(),
            };
        }).RequireAuthorization("RequireAdmin");

        return app;
    }

    private static async Task<object> ViewAsync(
        AtlassianLinkAppService linkApp, AtlassianLinkAppRegistration stored, CancellationToken ct) => new
    {
        clientId = stored.ClientId,
        permission = "read:me",
        signInEnabled = stored.SignInEnabled,
        lastCheckedAt = stored.CheckedAt,
        lastCheckAccepted = stored.CheckAccepted,
        lastUsedForSignInAt = stored.VerifiedAt,
        peopleLinked = await linkApp.CountLinkedPeopleAsync(ct),
    };

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
        var group = app.MapGroup("/api/v1/atlassian/sites").WithTags("Providers");

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
                    Results.BadRequest(new { error = "The site address must look like https://your-site.atlassian.net." }),
                AtlassianResolveOutcome.NotFound =>
                    Results.NotFound(new { error = $"Couldn't find an Atlassian Cloud site at {resolved.SiteUrl}." }),
                _ => Results.Ok(new { siteUrl = resolved.SiteUrl, cloudId = resolved.CloudId }),
            };
        }).RequireAuthorization("RequireAdmin");

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
                return Results.BadRequest(new { error = "product must be \"confluence\" or \"jira\".", field = "product" });

            var added = await sites.AddAsync(request.SiteUrl, request.ClientId, request.ClientSecret, UserIdOf(http),
                request.SaveAnyway ?? false, product.Value, ct);

            return added.Outcome switch
            {
                AtlassianAddOutcome.MissingFields =>
                    Results.BadRequest(new { error = "siteUrl, clientId and clientSecret are required." }),
                AtlassianAddOutcome.TestFailed => Results.UnprocessableEntity(Report(added.Test!)),
                AtlassianAddOutcome.Conflict =>
                    Results.Conflict(new { error = $"A connection named '{added.Name}' already exists." }),
                AtlassianAddOutcome.NameTaken =>
                    Results.Conflict(new { error = $"Another connection is already named '{added.Name}'. Rename it, then try again." }),
                _ => Results.Created($"/api/v1/atlassian/sites/{added.Connection!.Id}",
                    new
                    {
                        id = added.Connection.Id, name = added.Connection.Name, siteUrl = added.SiteUrl, cloudId = added.CloudId,
                        clientId = added.ClientId, product = AtlassianSite.ProductValue(product.Value), test = Report(added.Test!),
                    }),
            };
        }).RequireAuthorization("RequireAdmin");

        // GET /api/v1/atlassian/sites — every saved site, as the Connections page lists them.
        group.MapGet("/", async (
            [FromServices] IConnectionStore connections,
            [FromServices] ConnectionAdminService admin,
            CancellationToken ct) =>
        {
            var all = await connections.ListAsync(0, int.MaxValue, ct);
            var paused = await admin.PausedConnectionIdsAsync(ct);
            return Results.Ok(all
                .Where(c => c.Provider == ConnectionProvider.Atlassian)
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => SiteView(c, paused.Contains(c.Id))));
        }).RequireAuthorization("RequireAdmin");

        // GET /api/v1/atlassian/sites/{connectionId} — the site, its last check, and the scopes it needs.
        group.MapGet("/{connectionId:guid}", async (
            Guid connectionId,
            [FromServices] IConnectionStore connections,
            [FromServices] ConnectionAdminService admin,
            CancellationToken ct) =>
        {
            Connection? connection = await connections.GetAsync(connectionId, ct);
            if (connection is not { Provider: ConnectionProvider.Atlassian })
                return Results.NotFound(new { error = "No such Atlassian site." });

            // A connection's credential needs its own product's scopes only.
            AtlassianSiteView view = SiteView(connection, await admin.IsPausedAsync(connectionId, ct));
            return Results.Ok(view with
            {
                Scopes = AtlassianSiteScopes.Of(view.Product).Select(s => new { name = s.Name, reason = s.Reason, optional = s.Optional, product = s.Product }),
            });
        }).RequireAuthorization("RequireAdmin");

        // PATCH /api/v1/atlassian/sites/{connectionId} — renames the site, as the Name field on the
        // Connections page does. Audited as connection.updated.
        group.MapPatch("/{connectionId:guid}", async (
            Guid connectionId,
            [FromBody] RenameAtlassianSiteRequest request,
            [FromServices] IConnectionStore connections,
            [FromServices] ConnectionAdminService admin,
            CancellationToken ct) =>
        {
            if (await connections.GetAsync(connectionId, ct) is not { Provider: ConnectionProvider.Atlassian })
                return Results.NotFound(new { error = "No such Atlassian site." });

            var renamed = await admin.RenameAsync(connectionId, request.Name, ct);
            return renamed.Outcome switch
            {
                ConnectionRenameOutcome.InvalidName => Results.BadRequest(new
                {
                    error = $"name must be 1 to {ConnectionAdminService.MaxNameLength} characters.", field = "name",
                }),
                ConnectionRenameOutcome.NameTaken => Results.Conflict(new
                {
                    error = ConnectionAdminService.NameTakenMessage(request.Name!), field = "name",
                }),
                ConnectionRenameOutcome.NotFound => Results.NotFound(new { error = "No such Atlassian site." }),
                _ => Results.Ok(SiteView(renamed.Connection!, await admin.IsPausedAsync(connectionId, ct))),
            };
        }).RequireAuthorization("RequireAdmin");

        // POST /api/v1/atlassian/sites/{connectionId}/pause — stops syncing every source on the site.
        // Nothing is deleted, and the documents stay searchable.
        group.MapPost("/{connectionId:guid}/pause", async (
            Guid connectionId,
            [FromServices] IConnectionStore connections,
            [FromServices] ConnectionAdminService admin,
            CancellationToken ct) =>
        {
            if (await connections.GetAsync(connectionId, ct) is not { Provider: ConnectionProvider.Atlassian } connection)
                return Results.NotFound(new { error = "No such Atlassian site." });
            if (connection.SourceCount == 0)
                return Results.Conflict(new { error = "This site has no sources, so there is no syncing to pause." });

            int count = await admin.SetSyncingAsync(connection, enabled: false, ct: ct);
            return Results.Ok(new { syncPaused = true, sources = count });
        }).RequireAuthorization("RequireAdmin");

        // POST /api/v1/atlassian/sites/{connectionId}/resume — turns syncing back on for the sources
        // named in sourceIds, or every source on the site when it is left out.
        group.MapPost("/{connectionId:guid}/resume", async (
            Guid connectionId,
            [FromBody] ResumeAtlassianSiteRequest? request,
            [FromServices] IConnectionStore connections,
            [FromServices] ISourceStore sources,
            [FromServices] ConnectionAdminService admin,
            CancellationToken ct) =>
        {
            if (await connections.GetAsync(connectionId, ct) is not { Provider: ConnectionProvider.Atlassian } connection)
                return Results.NotFound(new { error = "No such Atlassian site." });
            if (connection.SourceCount == 0)
                return Results.Conflict(new { error = "This site has no sources, so there is no syncing to resume." });

            HashSet<Guid>? only = request?.SourceIds is { } ids ? ids.ToHashSet() : null;
            if (only is not null)
            {
                var onSite = (await sources.ListByConnectionAsync(connectionId, ct)).Select(s => s.Id).ToHashSet();
                if (only.Count == 0 || !only.IsSubsetOf(onSite))
                    return Results.BadRequest(new
                    {
                        error = "sourceIds must name at least one source, each of them on this site.", field = "sourceIds",
                    });
            }

            int count = await admin.SetSyncingAsync(connection, enabled: true, only, ct);
            return Results.Ok(new { syncPaused = await admin.IsPausedAsync(connectionId, ct), sources = count });
        }).RequireAuthorization("RequireAdmin");

        // DELETE /api/v1/atlassian/sites/{connectionId}?confirmName=... — deletes the site. Refused
        // while any source uses it; confirmName must be the site's name, the value the page asks an
        // administrator to type. Audited as connection.deleted.
        group.MapDelete("/{connectionId:guid}", async (
            Guid connectionId,
            [FromQuery] string? confirmName,
            [FromServices] IConnectionStore connections,
            [FromServices] ConnectionAdminService admin,
            CancellationToken ct) =>
        {
            if (await connections.GetAsync(connectionId, ct) is not { Provider: ConnectionProvider.Atlassian })
                return Results.NotFound(new { error = "No such Atlassian site." });

            var deleted = await admin.DeleteAsync(connectionId, confirmName, ct);
            return deleted.Outcome switch
            {
                ConnectionDeleteOutcome.ConfirmMismatch => Results.BadRequest(new
                {
                    error = $"confirmName must be the site's name, '{deleted.Connection!.Name}'.", field = "confirmName",
                }),
                ConnectionDeleteOutcome.HasSources => Results.Conflict(new
                {
                    error = ConnectionAdminService.InUseMessage(deleted.Connection!.Name, deleted.Sources!),
                    sources = deleted.Sources!.Select(s => new { id = s.Id, name = s.Name, url = $"/sources/{s.Id}" }),
                }),
                ConnectionDeleteOutcome.NotFound => Results.NotFound(new { error = "No such Atlassian site." }),
                _ => Results.NoContent(),
            };
        }).RequireAuthorization("RequireAdmin");

        // POST /api/v1/atlassian/sites/{connectionId}/test — every probe again with the stored
        // secret. The result is recorded as the site's health either way.
        group.MapPost("/{connectionId:guid}/test", async (
            Guid connectionId,
            [FromServices] AtlassianSiteService sites,
            CancellationToken ct) =>
        {
            var result = await sites.RetestAsync(connectionId, ct);
            return result is null
                ? Results.NotFound(new { error = "No such Atlassian site, or its stored credential can't be read." })
                : Results.Ok(Report(result));
        }).RequireAuthorization("RequireAdmin");

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
            var replaced = await sites.ReplaceCredentialsAsync(connectionId, request.ClientId, request.ClientSecret,
                UserIdOf(http), request.SaveAnyway ?? false, ct);

            return replaced.Outcome switch
            {
                AtlassianReplaceOutcome.NotFound => Results.NotFound(new { error = "No such Atlassian site." }),
                AtlassianReplaceOutcome.MissingFields =>
                    Results.BadRequest(new { error = "clientId and clientSecret are required." }),
                AtlassianReplaceOutcome.TestFailed => Results.UnprocessableEntity(Report(replaced.Test!)),
                AtlassianReplaceOutcome.SiteChanged => Results.Conflict(new
                {
                    error = "The site at this address has a different Cloud ID than when it was added, so it is a different site. "
                            + "Add it as a new connection.",
                }),
                _ => Results.Ok(new
                {
                    id = replaced.Connection!.Id, name = replaced.Connection.Name,
                    clientId = AtlassianSite.FromConfigJson(replaced.Connection.ConfigJson)?.ClientId,
                    test = Report(replaced.Test!),
                }),
            };
        }).RequireAuthorization("RequireAdmin");

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
                AtlassianSpacesOutcome.NotFound => Results.NotFound(new { error = "No such connection." }),
                AtlassianSpacesOutcome.NotAtlassian =>
                    Results.BadRequest(new { error = "That connection is not an Atlassian site." }),
                AtlassianSpacesOutcome.WrongProduct =>
                    Results.BadRequest(new { error = listed.Error, field = "connectionId" }),
                AtlassianSpacesOutcome.Unavailable =>
                    Results.Json(new { error = listed.Error }, statusCode: StatusCodes.Status502BadGateway),
                _ => Results.Ok(listed.Spaces!.Select(s => new { id = s.Id, key = s.Key, name = s.Name, type = s.Type })),
            };
        }).RequireAuthorization("RequireAdmin");

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
                AtlassianSpacesOutcome.NotFound => Results.NotFound(new { error = "No such connection." }),
                AtlassianSpacesOutcome.NotAtlassian =>
                    Results.BadRequest(new { error = "That connection is not an Atlassian site." }),
                AtlassianSpacesOutcome.WrongProduct =>
                    Results.BadRequest(new { error = listed.Error, field = "connectionId" }),
                AtlassianSpacesOutcome.Unavailable =>
                    Results.Json(new { error = listed.Error }, statusCode: StatusCodes.Status502BadGateway),
                _ => Results.Ok(listed.Projects!.Select(p => new { id = p.Id, key = p.Key, name = p.Name, projectTypeKey = p.ProjectTypeKey })),
            };
        }).RequireAuthorization("RequireAdmin");

        return app;
    }

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
                ? new
                {
                    checkedAt = h.CheckedAt, succeeded = h.Succeeded, warning = h.Warning, step = h.Step,
                    stepLabel = h.Step is null ? null : AtlassianConnectionTester.StepLabel(h.Step),
                    message = h.Message, origin = h.Origin, credentialRejected = h.CredentialRejected,
                    lastSucceededAt = h.LastSucceededAt, confirmedScopes = h.ConfirmedScopes,
                }
                : null);
    }

    private static object Report(ConnectionTestResult result)
    {
        var report = AtlassianTestReport.From(result);
        return new
        {
            success = report.Success,
            warning = report.Warning,
            message = report.Message,
            step = report.Step,
            stepLabel = report.StepLabel,
            troubleshootingUrl = report.TroubleshootingUrl,
            detail = report.Detail,
            scopes = report.Scopes,
        };
    }
}
