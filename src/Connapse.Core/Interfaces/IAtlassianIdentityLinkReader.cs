namespace Connapse.Core.Interfaces;

/// <summary>A Connapse user's linked Atlassian account: the account id is the key, the display name is for the UI.</summary>
public sealed record AtlassianIdentityRef(string AccountId, string DisplayName);

/// <summary>
/// Which Atlassian account a Connapse user signed in as, for deciding what Confluence content they
/// may search.
/// </summary>
/// <remarks>Holds no token: Atlassian attests the account once at link time.</remarks>
public interface IAtlassianIdentityLinkReader
{
    /// <summary>The Atlassian account linked to <paramref name="userId"/>, or null when they have linked none.</summary>
    Task<AtlassianIdentityRef?> GetLinkAsync(Guid userId, CancellationToken ct = default);
}
