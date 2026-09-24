namespace Connapse.Identity.Data.Entities;

/// <summary>A Connapse user's linked GitHub account. Holds no token — GitHub attests the account once
/// at link time; permissions are later read with the GitHub App's installation tokens. One row per
/// user (unique index); linking again replaces the row.</summary>
public class UserGitHubIdentityLinkEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>GitHub's numeric user id — permanent, survives a rename; the key.</summary>
    public long GitHubUserId { get; set; }
    /// <summary>The login at link time. Display and API path only: a rename changes it, the id does not.</summary>
    public string Login { get; set; } = string.Empty;
    public DateTime ConnectedAt { get; set; }
    public ConnapseUser User { get; set; } = null!;
}
