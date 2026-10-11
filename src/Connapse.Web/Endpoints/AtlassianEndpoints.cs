using System.Security.Claims;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public sealed record ResolveAtlassianSiteRequest(string? SiteUrl);

/// <param name="SaveAnyway">Save even though the site has no page or no other user yet to check administrator access against.</param>
public sealed record CreateAtlassianSiteRequest(string? SiteUrl, string? ClientId, string? ClientSecret, bool? SaveAnyway = null)
{
    // The generated ToString would print the secret into any log line or message it reached.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"SiteUrl = {SiteUrl}, ClientId = {ClientId}, ClientSecret = ***, SaveAnyway = {SaveAnyway}");
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
            var added = await sites.AddAsync(request.SiteUrl, request.ClientId, request.ClientSecret, UserIdOf(http),
                request.SaveAnyway ?? false, ct);

            return added.Outcome switch
            {
                AtlassianAddOutcome.MissingFields =>
                    Results.BadRequest(new { error = "siteUrl, clientId and clientSecret are required." }),
                AtlassianAddOutcome.TestFailed => Results.UnprocessableEntity(Report(added.Test!)),
                AtlassianAddOutcome.Conflict =>
                    Results.Conflict(new { error = $"A connection named '{new Uri(added.SiteUrl!).Host}' already exists." }),
                _ => Results.Created($"/api/v1/atlassian/sites/{added.Connection!.Id}",
                    new
                    {
                        id = added.Connection.Id, name = added.Connection.Name, siteUrl = added.SiteUrl, cloudId = added.CloudId,
                        clientId = added.ClientId, test = Report(added.Test!),
                    }),
            };
        }).RequireAuthorization("RequireAdmin");

        // GET /api/v1/atlassian/sites/{connectionId} — the site, its last check, and the scopes it needs.
        group.MapGet("/{connectionId:guid}", async (
            Guid connectionId,
            [FromServices] IConnectionStore connections,
            CancellationToken ct) =>
        {
            Connection? connection = await connections.GetAsync(connectionId, ct);
            if (connection is not { Provider: ConnectionProvider.Atlassian })
                return Results.NotFound(new { error = "No such Atlassian site." });

            AtlassianSite? site = AtlassianSite.FromConfigJson(connection.ConfigJson);
            return Results.Ok(new
            {
                id = connection.Id,
                name = connection.Name,
                siteUrl = site?.SiteUrl,
                cloudId = site?.CloudId,
                clientId = site?.ClientId,
                hasSecret = connection.HasSecret,
                sourceCount = connection.SourceCount,
                health = connection.Health is { } h
                    ? new
                    {
                        checkedAt = h.CheckedAt, succeeded = h.Succeeded, warning = h.Warning, step = h.Step,
                        stepLabel = h.Step is null ? null : AtlassianConnectionTester.StepLabel(h.Step),
                        message = h.Message, origin = h.Origin, credentialRejected = h.CredentialRejected,
                        lastSucceededAt = h.LastSucceededAt, confirmedScopes = h.ConfirmedScopes,
                    }
                    : null,
                scopes = AtlassianSiteScopes.All.Select(s => new { name = s.Name, reason = s.Reason, optional = s.Optional }),
            });
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
                AtlassianSpacesOutcome.Unavailable =>
                    Results.Json(new { error = listed.Error }, statusCode: StatusCodes.Status502BadGateway),
                _ => Results.Ok(listed.Spaces!.Select(s => new { id = s.Id, key = s.Key, name = s.Name, type = s.Type })),
            };
        }).RequireAuthorization("RequireAdmin");

        return app;
    }

    private static Guid? UserIdOf(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid id) ? id : null;

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
