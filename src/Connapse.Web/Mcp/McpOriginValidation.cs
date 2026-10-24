using System.Net;

namespace Connapse.Web.Mcp;

/// <summary>
/// DNS-rebinding protection for /mcp (#846): the MCP spec says servers MUST validate the Origin header.
/// A rebinding attack runs in a browser, which always sends Origin, and makes the attacker's site and
/// Connapse look same-origin, so CORS alone can't stop it. A request with no Origin (every non-browser
/// MCP client) passes. A browser's request passes only from a loopback origin or one listed in
/// Cors:AllowedOrigins; any other gets 403 with a JSON-RPC error, as the Streamable HTTP transport says.
/// </summary>
public static class McpOriginValidation
{
    public static IApplicationBuilder UseMcpOriginValidation(this IApplicationBuilder app, IConfiguration configuration)
    {
        var allowed = (configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
            .Select(o => o.TrimEnd('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/mcp"),
            mcp => mcp.Use(async (context, next) =>
            {
                string? origin = context.Request.Headers.Origin;
                if (string.IsNullOrEmpty(origin) || IsAllowed(origin, allowed))
                {
                    await next(context);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    jsonrpc = "2.0",
                    error = new { code = -32600, message = "Origin not allowed. Add it to Cors:AllowedOrigins to call /mcp from that site." },
                });
            }));
    }

    /// <summary>A loopback origin (any port), or one configured in Cors:AllowedOrigins.</summary>
    public static bool IsAllowed(string origin, IReadOnlySet<string> configured)
    {
        if (configured.Contains(origin.TrimEnd('/')))
            return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;
        return uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
    }
}
