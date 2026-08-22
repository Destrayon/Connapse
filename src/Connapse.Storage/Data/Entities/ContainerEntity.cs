using System.Text.Json;

namespace Connapse.Storage.Data.Entities;

public class ContainerEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public JsonDocument? SettingsOverridesJson { get; set; } // JSONB: per-container settings overrides
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Auto-generated container summary (agent-optimized prose for routing)
    public string? Summary { get; set; }
    public DateTime? SummaryGeneratedAt { get; set; }
    public string? SummaryDocSetHash { get; set; } // sha256 of sorted(doc_id||summary_text)

    // Navigation properties
    public List<DocumentEntity> Documents { get; set; } = [];
    public List<FolderEntity> Folders { get; set; } = [];
}
