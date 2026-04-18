namespace Connapse.Identity.Data.Entities;

public class OAuthAuthCodeEntity
{
    public Guid Id { get; set; }
    public string CodeHash { get; set; } = "";
    public string ClientId { get; set; } = "";
    public Guid UserId { get; set; }
    public string RedirectUri { get; set; } = "";
    public string CodeChallenge { get; set; } = "";
    public string Scope { get; set; } = "";

    // RFC 8707 resource indicator. Captured at /oauth/authorize and replayed into
    // the access token's `aud` claim at /oauth/token so the MCP client can verify
    // the token was issued for the resource it asked for (MCP spec §Token Audience
    // Binding). Nullable for backwards compatibility with non-MCP OAuth flows that
    // omit the parameter.
    public string? Resource { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }

    public ConnapseUser User { get; set; } = null!;
}
