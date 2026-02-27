using System.Text.Json;

namespace Connapse.Identity.Data.Entities;

public class AuditLogEntity
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public JsonDocument? Details { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public ConnapseUser? User { get; set; }
}
