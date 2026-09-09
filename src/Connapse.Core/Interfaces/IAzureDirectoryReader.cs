namespace Connapse.Core.Interfaces;

/// <summary>
/// Resolves a linked Entra searcher into their identity set, reading the directory with Connapse's
/// own Azure identity. Mirrors <c>IDirectoryUserLookup</c> for AWS: rather than acting as the user,
/// Connapse asks the directory about them.
/// </summary>
public interface IAzureDirectoryReader
{
    /// <summary>
    /// The searcher's identity set (object id ∪ transitive security-group object ids), or a
    /// fail-closed denial. Applies the deprovisioning gate first — a gone/disabled account resolves
    /// to <see cref="AzureIdentityOutcome.Deprovisioned"/>.
    /// </summary>
    Task<AzureIdentitySet> ResolveAsync(AzureIdentityRef link, CancellationToken ct = default);
}
