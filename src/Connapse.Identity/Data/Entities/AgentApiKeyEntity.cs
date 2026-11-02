namespace Connapse.Identity.Data.Entities;

public class AgentApiKeyEntity
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string TokenPrefix { get; set; } = string.Empty;
    public string Scopes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    /// <summary>Whether a high-impact MCP call from this key waits for a person to approve it (#782). On unless turned off at creation.</summary>
    public bool RequiresApproval { get; set; } = true;

    public AgentEntity Agent { get; set; } = null!;
}
