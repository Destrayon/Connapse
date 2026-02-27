using System.Security.Claims;
using System.Text.Json;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Connapse.Identity.Services;

public class AuditLogger(
    ConnapseIdentityDbContext dbContext,
    IHttpContextAccessor httpContextAccessor,
    ILogger<AuditLogger> logger) : IAuditLogger
{
    public async Task LogAsync(
        string action,
        string? resourceType = null,
        string? resourceId = null,
        object? details = null,
        CancellationToken cancellationToken = default)
    {
        var httpContext = httpContextAccessor.HttpContext;

        Guid? userId = null;
        var userIdClaim = httpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(userIdClaim, out var parsedUserId))
            userId = parsedUserId;

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
}
