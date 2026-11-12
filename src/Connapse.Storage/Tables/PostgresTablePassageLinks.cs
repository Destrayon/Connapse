using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Tables;

/// <summary>
/// The links from passages to the tables found in them (CON-312). Each link names a table of the passage's own
/// document; the foreign keys remove it with its table, its passage or its document.
/// </summary>
public class PostgresTablePassageLinks(IDbContextFactory<KnowledgeDbContext> factory) : ITablePassageLinks
{
    public async Task ReplaceAsync(
        Guid containerId, Guid documentId, IReadOnlyList<TablePassageLink> links, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(links);
        foreach (var link in links)
        {
            if (link.Start < 0 || link.Length <= 0)
                throw new ArgumentException("A link's span must start at 0 or later and be at least one character long.", nameof(links));
        }

        await using var context = await factory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        // A link may only join a passage and a table of this document, or a reader of one could be shown the other.
        HashSet<Guid> tables = [.. await context.ContainerTables.AsNoTracking()
            .Where(t => t.ContainerId == containerId && t.DocumentId == documentId)
            .Select(t => t.Id).ToListAsync(ct)];
        List<Guid> chunkIds = links.Select(l => l.ChunkId).Distinct().ToList();
        HashSet<Guid> chunks = [.. await context.Chunks.AsNoTracking()
            .Where(c => c.DocumentId == documentId && chunkIds.Contains(c.Id))
            .Select(c => c.Id).ToListAsync(ct)];
        var stray = links.FirstOrDefault(l => !tables.Contains(l.TableId) || !chunks.Contains(l.ChunkId));
        if (stray is not null)
            throw new ArgumentException(
                $"Link from passage {stray.ChunkId} to table {stray.TableId} doesn't join a passage and a table of document {documentId}.",
                nameof(links));

        await context.TablePassageLinks.Where(l => l.DocumentId == documentId).ExecuteDeleteAsync(ct);
        context.TablePassageLinks.AddRange(links.Select(l => new TablePassageLinkEntity
        {
            ContainerId = containerId,
            DocumentId = documentId,
            TableId = l.TableId,
            ChunkId = l.ChunkId,
            Start = l.Start,
            Length = l.Length,
        }));
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<TablePassageLink>>> ForChunksAsync(
        IReadOnlyCollection<Guid> chunkIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        if (chunkIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<TablePassageLink>>();

        List<Guid> ids = [.. chunkIds.Distinct()];
        await using var context = await factory.CreateDbContextAsync(ct);
        var rows = await context.TablePassageLinks.AsNoTracking()
            .Where(l => ids.Contains(l.ChunkId))
            .OrderBy(l => l.ChunkId).ThenBy(l => l.Start).ThenBy(l => l.Id)
            .ToListAsync(ct);
        return rows
            .GroupBy(l => l.ChunkId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TablePassageLink>)g.Select(l => new TablePassageLink(l.TableId, l.ChunkId, l.Start, l.Length)).ToList());
    }
}
