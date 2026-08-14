using System.Text.Json;

namespace Connapse.Storage.Data.Entities;

public class SourceEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid ConnectionId { get; set; }
    public JsonDocument ScopeJson { get; set; } = null!; // JSONB: bucket prefix, root subpath, space key
    public JsonDocument? SettingsOverridesJson { get; set; }
    public bool Enabled { get; set; } = true;

    // Sync state
    public string? SyncCursor { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public int LastSyncStatus { get; set; } // maps to SyncStatus enum
    public string? LastSyncError { get; set; }
    public int? SyncIntervalSeconds { get; set; } // null inherits the connection default

    // Auto-generated summary (agent-optimized prose for routing)
    public string? Summary { get; set; }
    public DateTime? SummaryGeneratedAt { get; set; }
    public string? SummaryDocSetHash { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public ConnectionEntity Connection { get; set; } = null!;
    public List<DocumentEntity> Documents { get; set; } = [];
}
