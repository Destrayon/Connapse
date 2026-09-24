namespace Connapse.Core.Interfaces;

/// <summary>A Connapse user's linked GitHub account: the numeric id is the key, the login is what API calls name.</summary>
public sealed record GitHubIdentityRef(long GitHubUserId, string Login);

/// <summary>
/// Which GitHub account a Connapse user signed in as, for deciding what private repository content
/// they may search.
/// </summary>
/// <remarks>
/// Holds no token: GitHub attests the account once at link time, and permissions are read later with
/// the GitHub App's own installation tokens.
/// </remarks>
public interface IGitHubIdentityLinkReader
{
    /// <summary>The GitHub account linked to <paramref name="userId"/>, or null when they have linked none.</summary>
    Task<GitHubIdentityRef?> GetLinkAsync(Guid userId, CancellationToken ct = default);
}
