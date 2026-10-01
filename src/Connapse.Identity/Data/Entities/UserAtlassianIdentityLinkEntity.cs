namespace Connapse.Identity.Data.Entities;

/// <summary>A Connapse user's linked Atlassian account. Holds no token — Atlassian attests the account
/// once at link time. One row per user (unique index); linking again replaces the row.</summary>
public class UserAtlassianIdentityLinkEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Atlassian's account id — permanent, survives a rename or email change; the key.</summary>
    public string AccountId { get; set; } = string.Empty;
    /// <summary>The display name at link time. Display only.</summary>
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateTime ConnectedAt { get; set; }
    public ConnapseUser User { get; set; } = null!;
}
