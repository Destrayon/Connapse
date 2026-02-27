namespace Connapse.Core.Interfaces;

public interface IAuditLogger
{
    Task LogAsync(
        string action,
        string? resourceType = null,
        string? resourceId = null,
        object? details = null,
        CancellationToken cancellationToken = default);
}
