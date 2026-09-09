using Connapse.Core;

namespace Connapse.Identity.Services;

/// <summary>
/// The read/store/disconnect side of a user's Entra identity link, for the integrations page and
/// any other caller that needs the link's state without touching <see cref="AzureIdentityLinkStore"/>
/// directly.
/// </summary>
public interface IAzureIdentityLinkService
{
    /// <summary>The link's display state, or null when the user has not connected one.</summary>
    Task<AzureIdentityLinkDto?> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Stores a user's link, replacing any existing one.</summary>
    Task StoreAsync(Guid userId, string oid, string tid, string displayName, CancellationToken ct = default);

    /// <summary>Removes a user's link. False when there was nothing to remove.</summary>
    Task<bool> DisconnectAsync(Guid userId, CancellationToken ct = default);
}
