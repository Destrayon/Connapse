namespace Connapse.Core;

public enum SyncStatus { Never = 0, Running = 1, Succeeded = 2, Failed = 3 }

public record Source(
    Guid Id,
    string Name,
    string? Description,
    Guid ConnectionId,
    string ScopeJson,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool Enabled = true,
    string? SyncCursor = null,
    DateTime? LastSyncedAt = null,
    SyncStatus LastSyncStatus = SyncStatus.Never,
    string? LastSyncError = null,
    int? SyncIntervalSeconds = null,
    ContainerSettingsOverrides? SettingsOverrides = null,
    string? Summary = null,
    DateTime? SummaryGeneratedAt = null,
    string? SummaryDocSetHash = null,
    int DocumentCount = 0);

public record CreateSourceRequest(
    string Name,
    Guid ConnectionId,
    string ScopeJson,
    string? Description = null,
    int? SyncIntervalSeconds = null);

public record UpdateSourceRequest(
    string? Name = null,
    string? Description = null,
    string? ScopeJson = null,
    int? SyncIntervalSeconds = null,
    bool? Enabled = null);
