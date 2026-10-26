using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Search.Links;

/// <summary>
/// What one search may read, for the link walk: exactly the documents the search's own queries could admit (its
/// owner, filters and scopes, the same SQL rule the keyword search applies), then the per-hit verifier search runs
/// on its results, as if each document were a hit. Asked afresh on every call; nothing is kept.
/// </summary>
internal sealed class SearchDocumentAccess(
    KnowledgeDbContext context,
    ISearchResultVerifier verifier,
    SearchOptions options,
    SearchScopes scopes) : IDocumentAccess
{
    public async Task<IReadOnlySet<string>> ReadableAsync(Guid? userId, IReadOnlyCollection<string> documentIds, CancellationToken ct = default)
    {
        // The scopes were resolved for the searcher; an answer for anyone else would be a guess.
        if (userId != options.UserId)
            throw new InvalidOperationException("Document access was asked for a different user than the search's.");

        Guid[] ids = documentIds
            .Select(id => Guid.TryParse(id, out Guid g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        List<string> where = ["d.id = ANY({0})", ScopePredicate.NotRevoked];
        List<object> parameters = [ids];
        if (!string.IsNullOrEmpty(options.ContainerId))
        {
            where.Add($"d.owner_id = {{{parameters.Count}}}");
            parameters.Add(Guid.Parse(options.ContainerId));
        }
        if (options.Filters is not null && options.Filters.TryGetValue("documentId", out string? only) && Guid.TryParse(only, out Guid onlyId))
        {
            where.Add($"d.id = {{{parameters.Count}}}");
            parameters.Add(onlyId);
        }
        if (options.Filters is not null && options.Filters.TryGetValue("pathPrefix", out string? prefix) && !string.IsNullOrWhiteSpace(prefix))
        {
            where.Add($"d.path LIKE {{{parameters.Count}}}");
            parameters.Add(prefix + "%");
        }
        ScopePredicate.Append(where, parameters, scopes);

        List<AdmittedDocument> admitted = await context.Database
            .SqlQueryRaw<AdmittedDocument>(
                $"SELECT d.id AS \"Id\", d.resource_uri AS \"ResourceUri\" FROM documents d WHERE {string.Join(" AND ", where)}",
                [.. parameters])
            .ToListAsync(ct);

        HashSet<string> readable = admitted
            .Where(d => d.ResourceUri is null)
            .Select(d => d.Id.ToString())
            .ToHashSet(StringComparer.Ordinal);

        // A synced document is shown only if the verifier would show it as a search hit (as DocumentReadGuard does).
        List<SearchHit> governed = admitted
            .Where(d => d.ResourceUri is not null)
            .Select(d => new SearchHit("", d.Id.ToString(), "", 1f, []))
            .ToList();
        if (governed.Count > 0)
            readable.UnionWith((await verifier.VerifyAsync(governed, userId, governed.Count, ct)).Select(h => h.DocumentId));

        return readable;
    }

    private sealed class AdmittedDocument
    {
        public Guid Id { get; init; }
        public string? ResourceUri { get; init; }
    }
}
