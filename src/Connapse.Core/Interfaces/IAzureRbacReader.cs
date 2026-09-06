namespace Connapse.Core.Interfaces;

/// <summary>
/// Resolves a searcher's effective RBAC-readable <c>azblob://</c> scope set from Azure Resource
/// Manager, reading with Connapse's own Azure identity. Fails closed.
/// </summary>
public interface IAzureRbacReader
{
    /// <summary>
    /// The <c>azblob://</c> prefixes <paramref name="primaryOid"/> may read via Storage Blob Data
    /// roles (transitive over groups, minus deny assignments), plus any tag-conditioned residue.
    /// </summary>
    Task<AzureRbacScopes> ResolveAsync(string primaryOid, CancellationToken ct = default);
}
