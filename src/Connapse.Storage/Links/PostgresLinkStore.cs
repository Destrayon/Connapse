using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Storage.Links;

/// <summary>
/// The link layer's write side and node/document lookups, in plain PostgreSQL tables. Reads that show links go
/// through <see cref="PostgresLinkReader"/>, which checks access.
/// </summary>
public sealed class PostgresLinkStore(IDbContextFactory<KnowledgeDbContext> contextFactory, IMemoryCache cache)
    : ILinkWriter, ILinkIndex
{
    private const string AnyKey = "link-layer:any";

    /// <summary>How long <see cref="AnyAsync"/> may answer from the cache.</summary>
    internal static readonly TimeSpan AnyCacheFor = TimeSpan.FromSeconds(10);

    // Bumped by every write, so an answer read while a write committed is not cached over it.
    private static long _writes;

    public async Task<bool> AnyAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(AnyKey, out bool any))
            return any;

        long writes = Interlocked.Read(ref _writes);
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        any = await context.LinkNodeDocuments.AnyAsync(ct);
        if (Interlocked.Read(ref _writes) == writes)
            cache.Set(AnyKey, any, AnyCacheFor);
        return any;
    }

    public async Task ReplaceAsync(IReadOnlyList<DocumentLinks> documents, CancellationToken ct = default)
    {
        if (documents.Count == 0)
            return;

        string[] ids = documents.Select(d => d.DocumentId).Distinct(StringComparer.Ordinal).ToArray();
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        await context.LinkEdges.Where(e => ids.Contains(e.DocumentId)).ExecuteDeleteAsync(ct);
        await context.LinkNodeFacts.Where(f => ids.Contains(f.DocumentId)).ExecuteDeleteAsync(ct);
        await context.LinkNodeDocuments.Where(n => ids.Contains(n.DocumentId)).ExecuteDeleteAsync(ct);

        foreach (DocumentLinks document in documents)
        {
            if (document.NodeId is { } nodeId)
                context.LinkNodeDocuments.Add(new LinkNodeDocumentEntity { NodeId = nodeId, DocumentId = document.DocumentId });

            context.LinkNodeFacts.AddRange(document.Facts.Select((f, i) => new LinkNodeFactEntity
            {
                NodeId = f.NodeId,
                Field = (short)f.Field,
                Text = f.Text,
                DocumentId = document.DocumentId,
                Ordinal = i,
            }));

            context.LinkEdges.AddRange(document.Links.Select((l, i) => new LinkEdgeEntity
            {
                EdgeId = l.EdgeId,
                FromNode = l.FromId,
                ToNode = l.ToId,
                Type = l.Type,
                DocumentId = document.DocumentId,
                Evidence = l.Evidence,
                Ordinal = i,
            }));
        }

        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        Interlocked.Increment(ref _writes);
        cache.Remove(AnyKey);
    }

    public async Task<ILookup<string, string>> NodesOfAsync(IReadOnlyCollection<string> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0)
            return Array.Empty<LinkNodeDocumentEntity>().ToLookup(r => r.DocumentId, r => r.NodeId);

        string[] ids = [.. documentIds];
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        List<LinkNodeDocumentEntity> rows = await context.LinkNodeDocuments.AsNoTracking()
            .Where(r => ids.Contains(r.DocumentId))
            .ToListAsync(ct);
        return rows.ToLookup(r => r.DocumentId, r => r.NodeId, StringComparer.Ordinal);
    }

    public async Task<ILookup<string, string>> DocumentsOfAsync(IReadOnlyCollection<string> nodeIds, CancellationToken ct = default)
    {
        if (nodeIds.Count == 0)
            return Array.Empty<LinkNodeDocumentEntity>().ToLookup(r => r.NodeId, r => r.DocumentId);

        string[] ids = [.. nodeIds];
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        List<LinkNodeDocumentEntity> rows = await context.LinkNodeDocuments.AsNoTracking()
            .Where(r => ids.Contains(r.NodeId))
            .ToListAsync(ct);
        return rows.ToLookup(r => r.NodeId, r => r.DocumentId, StringComparer.Ordinal);
    }
}
