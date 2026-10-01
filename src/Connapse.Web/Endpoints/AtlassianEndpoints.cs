using System.Security.Claims;
using Connapse.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public sealed record ResolveAtlassianSiteRequest(string? SiteUrl);

public sealed record CreateAtlassianSiteRequest(string? SiteUrl, string? ClientId, string? ClientSecret);

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
        // passes stores the site as a connection (the secret encrypted, never echoed back).
        group.MapPost("/", async (
            HttpContext http,
            [FromBody] CreateAtlassianSiteRequest request,
            [FromServices] AtlassianSiteService sites,
            CancellationToken ct) =>
        {
            Guid? userId = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid id) ? id : null;
            var added = await sites.AddAsync(request.SiteUrl, request.ClientId, request.ClientSecret, userId, ct);

            return added.Outcome switch
            {
                AtlassianAddOutcome.MissingFields =>
                    Results.BadRequest(new { error = "siteUrl, clientId and clientSecret are required." }),
                AtlassianAddOutcome.TestFailed => Results.UnprocessableEntity(new
                {
                    success = false,
                    step = added.Test?.Details?.GetValueOrDefault("step"),
                    warning = added.Test?.Details?.ContainsKey("warning") ?? false,
                    message = added.Test?.Message,
                }),
                AtlassianAddOutcome.Conflict =>
                    Results.Conflict(new { error = $"A connection named '{new Uri(added.SiteUrl!).Host}' already exists." }),
                _ => Results.Created($"/api/v1/atlassian/sites/{added.Connection!.Id}",
                    new { id = added.Connection.Id, name = added.Connection.Name, siteUrl = added.SiteUrl, cloudId = added.CloudId, clientId = added.ClientId }),
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
}
