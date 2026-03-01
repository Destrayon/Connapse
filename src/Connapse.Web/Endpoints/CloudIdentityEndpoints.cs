using System.Security.Claims;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public static class CloudIdentityEndpoints
{
    private const string StateCookieName = "__connapse_az_state";

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
                rs256Enabled = service.IsRs256Enabled(),
                azureAdConfigured = service.IsAzureAdConfigured()
            });
        }).RequireAuthorization();

        // GET /api/v1/auth/cloud/azure/connect — redirect to Azure AD authorize endpoint
        group.MapGet("/azure/connect", (
            HttpContext httpContext,
            [FromServices] ICloudIdentityService service) =>
        {
            if (!service.IsAzureAdConfigured())
                return Results.BadRequest(new { error = "azure_ad_not_configured", message = "Azure AD is not configured. An admin must set ClientId and TenantId in settings." });

            var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
            var result = service.GetAzureConnectUrl(baseUrl);

            // Set state cookie for CSRF protection
            httpContext.Response.Cookies.Append(StateCookieName, result.State, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(10),
                Path = "/api/v1/auth/cloud/azure"
            });

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
            // Validate state against cookie
            var expectedState = httpContext.Request.Cookies[StateCookieName];
            if (string.IsNullOrEmpty(expectedState) || expectedState != state)
                return Results.BadRequest(new { error = "invalid_state", message = "Invalid or expired state parameter." });

            // Clear the state cookie
            httpContext.Response.Cookies.Delete(StateCookieName, new CookieOptions
            {
                Path = "/api/v1/auth/cloud/azure"
            });

            var userId = GetUserId(httpContext);
            if (userId is null) return Results.Unauthorized();

            var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
            var redirectUri = $"{baseUrl}/api/v1/auth/cloud/azure/callback";

            try
            {
                await service.HandleAzureCallbackAsync(userId.Value, code, redirectUri, ct);
                return Results.Redirect("/profile");
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = "azure_callback_failed", message = ex.Message });
            }
        }).RequireAuthorization();

        // POST /api/v1/auth/cloud/aws/connect — AWS OIDC connect
        group.MapPost("/aws/connect", async (
            HttpContext httpContext,
            [FromServices] ICloudIdentityService service,
            CancellationToken ct) =>
        {
            var userId = GetUserId(httpContext);
            if (userId is null) return Results.Unauthorized();

            var result = await service.ConnectAwsAsync(userId.Value, ct);
            if (!result.Success)
                return Results.BadRequest(new { error = "aws_connect_failed", message = result.Error });

            return Results.Ok(result.Identity);
        }).RequireAuthorization();

        // DELETE /api/v1/auth/cloud/{provider} — disconnect a cloud identity
        group.MapDelete("/{provider}", async (
            string provider,
            HttpContext httpContext,
            [FromServices] ICloudIdentityService service,
            [FromServices] IConnectorScopeCache scopeCache,
            [FromServices] IContainerStore containerStore,
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
                var targetConnectorType = cloudProvider == CloudProvider.AWS
                    ? ConnectorType.S3
                    : ConnectorType.AzureBlob;

                try
                {
                    var containers = await containerStore.ListAsync(ct);
                    foreach (var c in containers.Where(c => c.ConnectorType == targetConnectorType))
                        scopeCache.Invalidate(userId.Value, Guid.Parse(c.Id));
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
