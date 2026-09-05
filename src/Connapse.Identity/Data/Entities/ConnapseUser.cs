using Microsoft.AspNetCore.Identity;

namespace Connapse.Identity.Data.Entities;

public class ConnapseUser : IdentityUser<Guid>
{
    public string? DisplayName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public bool IsSystemAdmin { get; set; }

    // Navigation properties
    public List<PersonalAccessTokenEntity> PersonalAccessTokens { get; set; } = [];
    public List<RefreshTokenEntity> RefreshTokens { get; set; } = [];
    public List<AuditLogEntity> AuditLogs { get; set; } = [];
}
