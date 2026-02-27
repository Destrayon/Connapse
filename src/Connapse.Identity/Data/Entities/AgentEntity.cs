namespace Connapse.Identity.Data.Entities;

public class AgentEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ConnapseUser CreatedByUser { get; set; } = null!;
    public List<AgentApiKeyEntity> ApiKeys { get; set; } = [];
}
