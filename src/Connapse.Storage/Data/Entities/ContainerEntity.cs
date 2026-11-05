using System.Text.Json;
using Connapse.Core;

namespace Connapse.Storage.Data.Entities;

public class ContainerEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ContainerKind Kind { get; set; } = ContainerKind.Documents;

    /// <summary>Team for every document container; personal only for a memory space with an owner.</summary>
    public MemoryScope Audience { get; set; } = MemoryScope.Team;
    public Guid? OwnerUserId { get; set; }
    public Guid? OwnerAgentId { get; set; }
    public JsonDocument? SettingsOverridesJson { get; set; } // JSONB: per-container settings overrides
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public List<DocumentEntity> Documents { get; set; } = [];
    public List<FolderEntity> Folders { get; set; } = [];
}
