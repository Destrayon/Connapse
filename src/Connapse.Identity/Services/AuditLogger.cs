using System.Security.Claims;
using System.Text.Json;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Connapse.Identity.Services;

/// <summary>
/// Writes an audit row with the acting user. A Blazor Server page action has no request of its
/// own, so <see cref="IHttpContextAccessor"/> may hold nothing (or not the actor) there; the
/// circuit's <see cref="AuthenticationStateProvider"/> names the user instead. A REST request has
/// no authentication state set, so its user comes from the request as before.
/// </summary>
public class AuditLogger(
    ConnapseIdentityDbContext dbContext,
    IHttpContextAccessor httpContextAccessor,
    ILogger<AuditLogger> logger,
    AuthenticationStateProvider? authenticationState = null) : IAuditLogger
{
    public async Task LogAsync(
        string action,
        string? resourceType = null,
        string? resourceId = null,
        object? details = null,
        CancellationToken cancellationToken = default)
    {
        var httpContext = httpContextAccessor.HttpContext;

        Guid? userId = await CircuitUserIdAsync() ?? UserIdOf(httpContext?.User);

        var ipAddress = httpContext?.Connection.RemoteIpAddress?.ToString();

        JsonDocument? detailsJson = null;
        if (details is not null)
        {
            var json = JsonSerializer.Serialize(details);
            detailsJson = JsonDocument.Parse(json);
        }

        var entry = new AuditLogEntity
        {
            Action = action,
            UserId = userId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Details = detailsJson,
            IpAddress = ipAddress,
            CreatedAt = DateTime.UtcNow,
        };

        try
        {
            dbContext.AuditLogs.Add(entry);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Audit logging should never fail the primary operation
            logger.LogError(ex, "Failed to write audit log entry: {Action}", action);
        }
    }

    private static Guid? UserIdOf(ClaimsPrincipal? user) =>
        Guid.TryParse(user?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>The signed-in user of the Blazor circuit or page render this runs in, if any.</summary>
    private async Task<Guid?> CircuitUserIdAsync()
    {
        if (authenticationState is null)
            return null;
        try
        {
            var state = await authenticationState.GetAuthenticationStateAsync();
            return UserIdOf(state.User);
        }
        catch (InvalidOperationException)
        {
            // Not in a component scope (a REST request or a background job): no state was set.
            return null;
        }
    }
}
