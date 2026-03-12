using System.Security.Claims;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public static class CloudIdentityEndpoints
{
    private const string AzureStateCookieName = "__connapse_az_state";
    private const string AzurePkceCookieName = "__connapse_az_pkce";

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
                awsSsoConfigured = service.IsAwsSsoConfigured(),
                azureAdConfigured = service.IsAzureAdConfigured()
            });
        }).RequireAuthorization();

        // --- Azure OAuth2 ---

        // GET /api/v1/auth/cloud/azure/connect — redirect to Azure AD authorize endpoint
        group.MapGet("/azure/connect", (
            HttpContext httpContext,
            [FromServices] ICloudIdentityService service) =>
        {
            if (!service.IsAzureAdConfigured())
                return Results.BadRequest(new { error = "azure_ad_not_configured", message = "Azure AD is not configured. An admin must set ClientId and TenantId in settings." });

            var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
            var result = service.GetAzureConnectUrl(baseUrl);

            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = httpContext.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(10),
                Path = "/api/v1/auth/cloud/azure"
            };

            httpContext.Response.Cookies.Append(AzureStateCookieName, result.State, cookieOptions);
            httpContext.Response.Cookies.Append(AzurePkceCookieName, result.CodeVerifier, cookieOptions);

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
            var expectedState = httpContext.Request.Cookies[AzureStateCookieName];
            if (string.IsNullOrEmpty(expectedState) || expectedState != state)
                return Results.BadRequest(new { error = "invalid_state", message = "Invalid or expired state parameter." });

            var codeVerifier = httpContext.Request.Cookies[AzurePkceCookieName];
            if (string.IsNullOrEmpty(codeVerifier))
                return Results.BadRequest(new { error = "invalid_pkce", message = "Missing PKCE code verifier." });

            var deleteCookieOptions = new CookieOptions { Path = "/api/v1/auth/cloud/azure" };
            httpContext.Response.Cookies.Delete(AzureStateCookieName, deleteCookieOptions);
            httpContext.Response.Cookies.Delete(AzurePkceCookieName, deleteCookieOptions);

            var userId = GetUserId(httpContext);
            if (userId is null) return Results.Unauthorized();

            var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
            var redirectUri = $"{baseUrl}/api/v1/auth/cloud/azure/callback";

            try
            {
                await service.HandleAzureCallbackAsync(userId.Value, code, codeVerifier, redirectUri, ct);
                return Results.Redirect("/profile");
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = "azure_callback_failed", message = ex.Message });
            }
        }).RequireAuthorization();

        // --- AWS SSO (Device Authorization Flow) ---

        // POST /api/v1/auth/cloud/aws/device-auth — start device authorization
        group.MapPost("/aws/device-auth", async (
            HttpContext httpContext,
            [FromServices] ICloudIdentityService service,
            CancellationToken ct) =>
        {
            if (!service.IsAwsSsoConfigured())
                return Results.BadRequest(new
                {
                    error = "aws_sso_not_configured",
                    message = "AWS IAM Identity Center SSO is not configured. An admin must set the Issuer URL and Region in settings."
                });

            try
            {
                var result = await service.StartAwsDeviceAuthAsync(ct);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "aws_device_auth_failed", message = ex.Message });
            }
        }).RequireAuthorization();

        // POST /api/v1/auth/cloud/aws/device-auth/poll — poll for device authorization completion
        group.MapPost("/aws/device-auth/poll", async (
            HttpContext httpContext,
            [FromBody] AwsDevicePollRequest request,
            [FromServices] ICloudIdentityService service,
            CancellationToken ct) =>
        {
            var userId = GetUserId(httpContext);
            if (userId is null) return Results.Unauthorized();

            try
            {
                var identity = await service.PollAwsDeviceAuthAsync(userId.Value, request.DeviceCode, ct);

                if (identity is null)
                    return Results.Ok(new { status = "pending" });

                return Results.Ok(new { status = "complete", identity });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "aws_poll_failed", message = ex.Message });
            }
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
                    var containers = await containerStore.ListAsync(take: int.MaxValue, ct: ct);
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

public record AwsDevicePollRequest(string DeviceCode);
