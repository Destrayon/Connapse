using Connapse.Core;

namespace Connapse.Identity.Data.Entities;

public class UserCloudIdentityEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public CloudProvider Provider { get; set; }
    public string IdentityDataJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }

    // Navigation
    public ConnapseUser User { get; set; } = null!;
}
