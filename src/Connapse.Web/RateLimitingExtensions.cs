using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Connapse.Web;

public sealed class RateLimitingSettings
{
    public int AuthPermitLimit { get; set; } = 10;
    public int AuthWindowSeconds { get; set; } = 60;
    public int ApiPermitLimit { get; set; } = 200;
    public int ApiWindowSeconds { get; set; } = 60;
    public int McpPermitLimit { get; set; } = 600;
    public int McpWindowSeconds { get; set; } = 60;
}

public static class RateLimitingExtensions
{
    public const string AuthPolicy = "auth";
    public const string ApiPolicy = "api";
    public const string McpPolicy = "mcp";

    public static IServiceCollection AddConnapseRateLimiting(
        this IServiceCollection services, IConfiguration configuration)
    {
        var settings = new RateLimitingSettings();
        configuration.GetSection("RateLimiting").Bind(settings);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/json";

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);
                }

                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Connapse.RateLimiting");

                logger.LogWarning(
                    "Rate limit exceeded for {RemoteIp} on {Path}",
                    context.HttpContext.Connection.RemoteIpAddress,
                    context.HttpContext.Request.Path);

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Rate limit exceeded. Try again later." },
                    cancellationToken);
            };

            // Auth policy: strict per-IP limit for anonymous auth endpoints (login, register, token)
            options.AddFixedWindowLimiter(AuthPolicy, opt =>
            {
                opt.PermitLimit = settings.AuthPermitLimit;
                opt.Window = TimeSpan.FromSeconds(settings.AuthWindowSeconds);
                opt.QueueLimit = 0;
                opt.AutoReplenishment = true;
            });

            // API policy: per-user limit for authenticated endpoints
            options.AddPolicy(ApiPolicy, httpContext =>
            {
                var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

                if (!string.IsNullOrEmpty(userId))
                {
                    // Authenticated: partition by user ID
                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: userId,
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = settings.ApiPermitLimit,
                            Window = TimeSpan.FromSeconds(settings.ApiWindowSeconds),
                            QueueLimit = 0,
                            AutoReplenishment = true
                        });
                }

                // Unauthenticated: partition by IP with the same strict limit as auth endpoints.
                // Intentionally shares AuthPermitLimit — anonymous API hits are equally untrusted.
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: $"anon:{ip}",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.AuthPermitLimit,
                        Window = TimeSpan.FromSeconds(settings.AuthWindowSeconds),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });

            // MCP policy: generous per-agent limit for tool calls
            options.AddPolicy(McpPolicy, httpContext =>
            {
                var agentId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? httpContext.Connection.RemoteIpAddress?.ToString()
                    ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: $"mcp:{agentId}",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.McpPermitLimit,
                        Window = TimeSpan.FromSeconds(settings.McpWindowSeconds),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });
        });

        return services;
    }
}
