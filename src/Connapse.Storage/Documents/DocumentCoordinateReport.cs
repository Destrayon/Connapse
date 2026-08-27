using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Documents;

/// <summary>A source holding documents whose external address was never recorded.</summary>
public sealed record UnlocatedSource(Guid SourceId, string SourceName, int DocumentCount);

/// <summary>
/// Reports documents that no permission rule can be checked against.
/// </summary>
/// <remarks>
/// Both search predicates require <c>resource_uri IS NOT NULL</c>, so a document indexed before that
/// column existed becomes unreachable the moment per-user filtering is switched on — and
/// indistinguishably from a denial. This exists so an operator is told which sources to re-sync
/// beforehand, rather than discovering it as a support ticket afterwards.
/// <para>
/// A SQL backfill is deliberately not offered. Deriving the URI from a source's scope and a
/// document's stored path is silently wrong for a source that has been re-pointed since ingestion,
/// and a document attributed to the wrong key is worse than one attributed to none.
/// </para>
/// </remarks>
public sealed class DocumentCoordinateReport(IDbContextFactory<KnowledgeDbContext> factory)
{
    /// <summary>Sources with at least one document that has no recorded coordinate.</summary>
    public async Task<IReadOnlyList<UnlocatedSource>> UnlocatedBySourceAsync(
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // Uploads have no external address and never will, so only source-backed documents count.
        // The GroupBy's Count() is materialized on its own before joining against Sources: folding
        // the Join (or an OrderBy referencing the joined shape) into the same query does not
        // translate under Npgsql's EF Core provider and throws at query-compile time — it does not
        // silently fall back to client evaluation — so the aggregate has to round-trip as a plain
        // value first, and the join against the (small) set of affected sources happens in memory.
        List<KeyValuePair<Guid, int>> counts = await db.Documents
            .Where(d => d.SourceId != null && d.ResourceUri == null)
            .GroupBy(d => d.SourceId!.Value)
            .Select(g => new KeyValuePair<Guid, int>(g.Key, g.Count()))
            .ToListAsync(ct);

        if (counts.Count == 0)
            return [];

        List<Guid> sourceIds = counts.Select(c => c.Key).ToList();
        Dictionary<Guid, string> names = await db.Sources
            .Where(s => sourceIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        return counts
            .Where(c => names.ContainsKey(c.Key))
            .Select(c => new UnlocatedSource(c.Key, names[c.Key], c.Value))
            .OrderByDescending(r => r.DocumentCount)
            .ToList();
    }
}
