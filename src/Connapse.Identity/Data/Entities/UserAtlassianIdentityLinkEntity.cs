namespace Connapse.Identity.Data.Entities;

/// <summary>A Connapse user's linked Atlassian account. Holds no token — Atlassian attests the account
/// once at link time. One row per user (unique index); linking again replaces the row.</summary>
/// <remarks>Only the account id is kept. A name or email would be personal data, which Atlassian requires
/// an app to report every week through its personal data reporting API (#695).</remarks>
public class UserAtlassianIdentityLinkEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Atlassian's account id — permanent, survives a rename or email change; the key.</summary>
    public string AccountId { get; set; } = string.Empty;
    public DateTime ConnectedAt { get; set; }
    public ConnapseUser User { get; set; } = null!;
}
