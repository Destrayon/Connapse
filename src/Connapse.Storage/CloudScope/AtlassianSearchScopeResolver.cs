using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Atlassian;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Which Confluence sites a user's search may reach: every Atlassian site a connection reads, as
/// <c>atlassian://{cloudId}/</c> prefixes, but only for a user who has linked an Atlassian account.
/// An unlinked user gets nothing, so their search never reaches Confluence documents and costs no
/// API calls. This is a pre-filter only; each hit is still checked by
/// <see cref="AtlassianSearchResultVerifier"/>.
/// </summary>
public sealed class AtlassianSearchScopeResolver(
    IConnectionStore connections,
    IAtlassianIdentityLinkReader links) : ISearchScopeResolver
{
    public async Task<SearchScopes> ResolveAsync(Guid? userId, CancellationToken ct = default)
    {
        if (userId is null) return SearchScopes.NoPrincipal;

        if (await links.GetLinkAsync(userId.Value, ct) is null) return SearchScopes.None;

        var prefixes = (await connections.ListAsync(take: int.MaxValue, ct: ct))
            .Where(c => c.Provider == ConnectionProvider.Atlassian)
            .Select(c => AtlassianSite.FromConfigJson(c.ConfigJson))
            .OfType<AtlassianSite>()
            .Select(site => AtlassianUri.SitePrefix(site.CloudId))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return prefixes.Count == 0 ? SearchScopes.None : SearchScopes.OfPrefixes(prefixes);
    }
}
