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
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }

    public ConnapseUser User { get; set; } = null!;
}
