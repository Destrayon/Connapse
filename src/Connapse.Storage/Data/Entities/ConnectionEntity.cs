using System.Text.Json;

namespace Connapse.Storage.Data.Entities;

public class ConnectionEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Provider { get; set; } // maps to ConnectionProvider enum
    public JsonDocument? ConfigJson { get; set; } // JSONB: non-secret provider settings
    public string? SecretProtected { get; set; } // DataProtection ciphertext, purpose "Connection.v1"
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public List<SourceEntity> Sources { get; set; } = [];
}
