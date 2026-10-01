using System.Security.Claims;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.Atlassian;
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
            [FromServices] IHttpClientFactory httpClients,
            CancellationToken ct) =>
        {
            string? siteUrl = AtlassianSiteResolver.NormalizeSiteUrl(request.SiteUrl);
            if (siteUrl is null)
                return Results.BadRequest(new { error = "The site address must look like https://your-site.atlassian.net." });

            using var http = httpClients.CreateClient(AtlassianApiClient.HttpClientName);
            string? cloudId = await AtlassianSiteResolver.ResolveCloudIdAsync(http, siteUrl, ct);
            return cloudId is null
                ? Results.NotFound(new { error = $"Couldn't find an Atlassian Cloud site at {siteUrl}." })
                : Results.Ok(new { siteUrl, cloudId });
        }).RequireAuthorization("RequireAdmin");

        // POST /api/v1/atlassian/sites — tests the service account, and only when every probe
        // passes stores the site as a connection (the secret encrypted, never echoed back).
        group.MapPost("/", async (
            HttpContext http,
            [FromBody] CreateAtlassianSiteRequest request,
            [FromServices] AtlassianConnectionTester tester,
            [FromServices] IConnectionStore connections,
            [FromServices] IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.SiteUrl)
                || string.IsNullOrWhiteSpace(request.ClientId)
                || string.IsNullOrWhiteSpace(request.ClientSecret))
                return Results.BadRequest(new { error = "siteUrl, clientId and clientSecret are required." });

            var result = await tester.TestConnectionAsync(
                new AtlassianSiteTestRequest(request.SiteUrl, request.ClientId, request.ClientSecret), ct: ct);

            if (!result.Success || result.Details is null
                || !result.Details.TryGetValue("cloudId", out object? cloudIdValue)
                || !result.Details.TryGetValue("siteUrl", out object? siteUrlValue))
            {
                return Results.UnprocessableEntity(new
                {
                    success = false,
                    step = result.Details?.GetValueOrDefault("step"),
                    warning = result.Details?.ContainsKey("warning") ?? false,
                    message = result.Message,
                });
            }

            string siteUrl = (string)siteUrlValue;
            string cloudId = (string)cloudIdValue;
            string clientId = request.ClientId.Trim();
            string name = new Uri(siteUrl).Host;

            string configJson = JsonSerializer.Serialize(new { siteUrl, cloudId, clientId });
            Guid? userId = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid id) ? id : null;

            Connection connection;
            try
            {
                connection = await connections.CreateAsync(
                    new CreateConnectionRequest(name, ConnectionProvider.Atlassian, configJson, request.ClientSecret),
                    userId, ct);
            }
            catch (InvalidOperationException)
            {
                return Results.Conflict(new { error = $"A connection named '{name}' already exists." });
            }

            await audit.LogAsync("connection.created", "connection", connection.Id.ToString(),
                new { Provider = "Atlassian", SiteUrl = siteUrl, CloudId = cloudId, ClientId = clientId }, ct);

            return Results.Created($"/api/v1/atlassian/sites/{connection.Id}",
                new { id = connection.Id, name = connection.Name, siteUrl, cloudId, clientId });
        }).RequireAuthorization("RequireAdmin");

        return app;
    }
}
