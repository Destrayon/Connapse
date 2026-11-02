using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Links;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Search.Links;

/// <summary>
/// One document's neighbours (CON-214), read one hop out from every node the document stands for, through the link
/// reader and so under its permission contract, with access checked by search's rule scoped to the one container.
/// <para>
/// A link between two documents is shown when one of the two states it, so a partial type defined across several files
/// does not credit each file with the calls the others make. "defines" links are left out: they tie a file to its own
/// definitions, which is structure, not a link a reader follows. A check that throws denies the whole answer.
/// </para>
/// </summary>
public sealed class DocumentLinkReader(
    IDbContextFactory<KnowledgeDbContext> contextFactory,
    ILinkIndex index,
    ISearchScopeResolver scopeResolver,
    ISearchResultVerifier verifier,
    ILogger<DocumentLinkReader> logger) : IDocumentLinkReader
{
    /// <summary>The most nodes one read walks; a file of thousands of definitions and their neighbours fits.</summary>
    internal const int MaxNodes = 20_000;

    private const string Defines = "defines";

    public async Task<DocumentLinkSet> ReadAsync(Guid? userId, Guid containerId, string documentId, CancellationToken ct = default)
    {
        try
        {
            return await ReadCoreAsync(userId, containerId, documentId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A check that cannot answer has not said "readable": nothing is shown rather than a guess.
            logger.LogWarning(ex, "A document's links could not be read; returning nothing");
            return DocumentLinkSet.DeniedResult;
        }
    }

    private async Task<DocumentLinkSet> ReadCoreAsync(Guid? userId, Guid containerId, string documentId, CancellationToken ct)
    {
        SearchScopes scopes;
        try
        {
            scopes = await scopeResolver.ResolveAsync(userId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scopes = SearchScopes.Failed;
        }
        scopes = ScopeResolution.Guard(scopes, userId);

        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        var access = new SearchDocumentAccess(context, verifier, new SearchOptions(ContainerId: containerId.ToString(), UserId: userId), scopes);

        HashSet<string> seeds = new((await index.NodesOfAsync([documentId], ct))[documentId], StringComparer.Ordinal);
        if (seeds.Count == 0)
            return DocumentLinkSet.Empty;

        LinkExpansion expansion = await new PostgresLinkReader(contextFactory, access)
            .ExpandAsync(new LinkExpandRequest(userId, [.. seeds], MaxHops: 1, MaxNodes: MaxNodes), ct);
        if (expansion.Denied)
            return DocumentLinkSet.DeniedResult;

        // Each link with exactly one end here: its far end, which way it points, and who stated it.
        var crossing = expansion.Edges
            .Where(e => e.Type != Defines && seeds.Contains(e.FromId) != seeds.Contains(e.ToId))
            .Select(e => (Edge: e, Outgoing: seeds.Contains(e.FromId), Far: seeds.Contains(e.FromId) ? e.ToId : e.FromId))
            .ToList();
        if (crossing.Count == 0)
            return DocumentLinkSet.Empty;

        ILookup<string, string> owners = await index.DocumentsOfAsync([.. crossing.Select(c => c.Far).Distinct(StringComparer.Ordinal)], ct);
        var pairs = new List<(bool Outgoing, string Other, string Type)>();
        foreach (var (edge, outgoing, far) in crossing)
        {
            foreach (string other in owners[far].Distinct(StringComparer.Ordinal))
            {
                if (other != documentId && (edge.Sources.Contains(documentId) || edge.Sources.Contains(other)))
                    pairs.Add((outgoing, other, edge.Type));
            }
        }
        if (pairs.Count == 0)
            return DocumentLinkSet.Empty;

        IReadOnlySet<string> readable = await access.ReadableAsync(userId, [.. pairs.Select(p => p.Other).Distinct(StringComparer.Ordinal)], ct);
        Guid[] ids = [.. readable.Select(Guid.Parse)];
        Dictionary<string, (string FileName, string Path)> named = await context.Documents.AsNoTracking()
            .Where(d => ids.Contains(d.Id) && d.ContainerId == containerId)
            .Select(d => new { d.Id, d.FileName, d.Path })
            .ToDictionaryAsync(d => d.Id.ToString(), d => (d.FileName, d.Path), ct);

        List<LinkedDocument> List(bool outgoing) => [.. pairs
            .Where(p => p.Outgoing == outgoing && named.ContainsKey(p.Other))
            .GroupBy(p => p.Other, StringComparer.Ordinal)
            .Select(g => new LinkedDocument(g.Key, named[g.Key].FileName, named[g.Key].Path,
                [.. g.Select(p => p.Type).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]))
            .OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Id, StringComparer.Ordinal)];

        return new DocumentLinkSet(List(outgoing: true), List(outgoing: false));
    }
}
