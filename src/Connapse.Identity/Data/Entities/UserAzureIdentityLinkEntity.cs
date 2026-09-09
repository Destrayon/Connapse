namespace Connapse.Identity.Data.Entities;

/// <summary>A Connapse user's linked Microsoft Entra identity (oid + tid). Holds no token —
/// Entra attests the identity once at link time; permissions are later read with Connapse's own
/// identity. One row per user (unique index); connecting again replaces the row.</summary>
public class UserAzureIdentityLinkEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Entra object id (oid) — immutable per-tenant user identifier; the permanent key.</summary>
    public string ObjectId { get; set; } = string.Empty;
    /// <summary>Entra tenant id (tid). Stored with ObjectId as the fully-qualified key.</summary>
    public string TenantId { get; set; } = string.Empty;
    /// <summary>Display name/UPN from the id_token — display only, mutable, never a key.</summary>
    public string DisplayName { get; set; } = string.Empty;
    public DateTime ConnectedAt { get; set; }
    public ConnapseUser User { get; set; } = null!;
}
