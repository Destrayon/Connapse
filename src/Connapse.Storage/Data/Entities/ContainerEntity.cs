namespace Connapse.Storage.Data.Entities;

public class ContainerEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int ConnectorType { get; set; } = 0; // 0 = MinIO; maps to ConnectorType enum
    public string? ConnectorConfig { get; set; } // JSONB: connector-specific config blob
    public bool IsEphemeral { get; set; } = false; // true for InMemory containers
    public string? SettingsOverridesJson { get; set; } // JSONB: per-container settings overrides
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public List<DocumentEntity> Documents { get; set; } = [];
    public List<FolderEntity> Folders { get; set; } = [];
}
