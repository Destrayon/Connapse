using Connapse.Core;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Documents;

/// <summary>A source holding documents whose external address was never recorded.</summary>
public sealed record UnlocatedSource(Guid SourceId, string SourceName, int DocumentCount);

/// <summary>
/// Reports documents that no cloud permission rule can be checked against.
/// </summary>
/// <remarks>
/// A document with a null <c>resource_uri</c> is not denied by cloud permission filtering — it
/// falls outside it entirely, governed instead by Connapse's own access control (#421). So the
/// consequence here is not that these documents become unreachable; it is that they will not be
/// permission-filtered by cloud grants, a permissive gap rather than a destructive one. This
/// exists so an operator can close that gap by re-syncing the sources involved, where re-syncing
/// can actually produce a coordinate.
/// <para>
/// Only the S3 connector ever reports a coordinate at sync time — SFTP, filesystem,
/// and MinIO sources never do, by design, so a null coordinate there is not a defect and re-sync
/// advice for it would never resolve. The query below is restricted to sources backed by a
/// connection whose provider is S3 for that reason.
/// </para>
/// <para>
/// A SQL backfill is deliberately not offered. Deriving the URI from a source's scope and a
/// document's stored path is silently wrong for a source that has been re-pointed since ingestion,
/// and a document attributed to the wrong key is worse than one attributed to none.
/// </para>
/// </remarks>
public sealed class DocumentCoordinateReport(IDbContextFactory<KnowledgeDbContext> factory)
{
    private static readonly int[] CoordinateCapableProviders =
        [(int)ConnectionProvider.S3];

    /// <summary>
    /// Sources — backed by a connection that can report a coordinate (S3) — with at
    /// least one document that has no recorded coordinate.
    /// </summary>
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

        // Restrict to sources whose connection provider can ever produce a coordinate. This is a
        // plain join (no GroupBy), so it translates and runs server-side unlike the aggregate above.
        Dictionary<Guid, string> names = await db.Sources
            .Where(s => sourceIds.Contains(s.Id))
            .Join(
                db.Connections.Where(c => CoordinateCapableProviders.Contains(c.Provider)),
                s => s.ConnectionId,
                c => c.Id,
                (s, c) => new { s.Id, s.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        return counts
            .Where(c => names.ContainsKey(c.Key))
            .Select(c => new UnlocatedSource(c.Key, names[c.Key], c.Value))
            .OrderByDescending(r => r.DocumentCount)
            .ToList();
    }
}
