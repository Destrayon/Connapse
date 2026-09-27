using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Keyword;

/// <summary>
/// Gives chunks written before the AddBm25Statistics migration their BM25 frequency markers and
/// length, a batch at a time, so the migration itself never rewrites the table. Each batch is an
/// ordinary UPDATE: the chunks statistics trigger counts the chunks as it gives them a length, and
/// BM25 ranks an owner once none of its chunks is left waiting.
/// </summary>
public class Bm25Backfiller(IDbContextFactory<KnowledgeDbContext> contextFactory)
{
    internal const int BatchSize = 2_000;

    // SKIP LOCKED: two app instances share the work instead of queueing behind each other.
    private const string BatchSql = """
        UPDATE chunks c
        SET search_vector = bm25_search_vector(c.content),
            bm25_length = bm25_text_length(c.content)
        WHERE c.id IN (
            SELECT id FROM chunks WHERE bm25_length IS NULL
            ORDER BY owner_id, id LIMIT {0}
            FOR UPDATE SKIP LOCKED)
        """;

    /// <summary>Backfills one batch; returns how many chunks it filled (0 when none are waiting).</summary>
    public async Task<int> BackfillBatchAsync(CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        context.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        return await context.Database.ExecuteSqlRawAsync(BatchSql, [BatchSize], ct);
    }
}
