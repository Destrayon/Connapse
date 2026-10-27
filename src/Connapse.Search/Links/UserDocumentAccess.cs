using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Search.Links;

/// <summary>
/// What one user may read anywhere, for reads that are not a search (agent memory's sources): the user's scopes
/// resolved afresh on every call, then the same rule and per-hit verifier a search over every container and source
/// applies. A resolver that fails leaves only documents with no cloud address, as it does for a search.
/// </summary>
public sealed class UserDocumentAccess(
    IDbContextFactory<KnowledgeDbContext> contextFactory,
    ISearchScopeResolver resolver,
    ISearchResultVerifier verifier) : IDocumentAccess
{
    public async Task<IReadOnlySet<string>> ReadableAsync(Guid? userId, IReadOnlyCollection<string> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        SearchScopes scopes;
        try
        {
            scopes = await resolver.ResolveAsync(userId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scopes = SearchScopes.Failed;
        }
        scopes = ScopeResolution.Guard(scopes, userId);

        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        return await new SearchDocumentAccess(context, verifier, new SearchOptions(UserId: userId), scopes)
            .ReadableAsync(userId, documentIds, ct);
    }
}
