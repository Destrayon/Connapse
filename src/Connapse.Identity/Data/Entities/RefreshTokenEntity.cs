namespace Connapse.Identity.Data.Entities;

public class RefreshTokenEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }
    public string? ClientId { get; set; }

    // RFC 8707 resource indicator carried through the refresh chain so that
    // refreshed access tokens keep the same `aud` binding the MCP client
    // originally asked for at /oauth/authorize.
    public string? Resource { get; set; }

    // The scopes an OAuth client was granted, space-separated, carried through the refresh chain so a
    // refreshed access token keeps exactly them (#934). Null for a sign-in's token, which has none.
    public string? Scope { get; set; }

    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public ConnapseUser User { get; set; } = null!;
}
