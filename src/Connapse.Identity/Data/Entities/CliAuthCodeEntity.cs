namespace Connapse.Identity.Data.Entities;

public class CliAuthCodeEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>SHA-256 hex of the raw auth code (never stored plain).</summary>
    public string CodeHash { get; set; } = "";

    /// <summary>BASE64URL(SHA-256(code_verifier)) — used to verify PKCE on exchange.</summary>
    public string CodeChallenge { get; set; } = "";

    /// <summary>The exact redirect URI registered at initiation time.</summary>
    public string RedirectUri { get; set; } = "";

    /// <summary>CLI machine name — used to name the resulting PAT.</summary>
    public string MachineName { get; set; } = "";

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>Set on first successful exchange; prevents replay.</summary>
    public DateTime? UsedAt { get; set; }

    public ConnapseUser User { get; set; } = null!;
}
