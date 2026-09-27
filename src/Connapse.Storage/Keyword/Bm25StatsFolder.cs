using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Keyword;

/// <summary>
/// Folds the signed rows the chunks triggers append to bm25_delta into bm25_term_stats and
/// bm25_owner_stats (see the AddBm25Statistics migration). Only for speed: readers add unfolded
/// deltas, so statistics are exact whether or not a fold has run.
/// <para>
/// The backlog is folded in batches, each its own transaction, so a large ingest never turns into
/// one statement whose memory and running time grow with the backlog: that version could not
/// finish within the command timeout, so it retried forever while the backlog, and every search
/// that has to add it up, kept growing.
/// </para>
/// </summary>
public class Bm25StatsFolder(IDbContextFactory<KnowledgeDbContext> contextFactory)
{
    internal const int BatchSize = 50_000;

    // One folder at a time, across app instances. Upserts go in (owner, term) order so a fold
    // never deadlocks with another writer of the same rows. A term whose df falls to 0 keeps its
    // row: readers skip df <= 0, and deleting it would mean a table scan per fold.
    private const string FoldBatchSql = """
        WITH d AS (
            DELETE FROM bm25_delta
            WHERE id IN (SELECT id FROM bm25_delta ORDER BY id LIMIT {0})
            RETURNING owner_id, term, d_df, d_n, d_len, tf, len),
        terms AS (
            INSERT INTO bm25_term_stats AS s (owner_id, term, df, max_tf, min_len)
            SELECT owner_id, term, sum(d_df), coalesce(max(tf), 0), coalesce(min(len), 2147483647)
            FROM d WHERE term IS NOT NULL
            GROUP BY owner_id, term ORDER BY owner_id, term
            ON CONFLICT (owner_id, term) DO UPDATE SET
                df = s.df + EXCLUDED.df,
                max_tf = greatest(s.max_tf, EXCLUDED.max_tf),
                min_len = least(s.min_len, EXCLUDED.min_len)),
        owners AS (
            INSERT INTO bm25_owner_stats AS o (owner_id, n_docs, total_len)
            SELECT owner_id, sum(d_n), sum(d_len) FROM d WHERE term IS NULL
            GROUP BY owner_id ORDER BY owner_id
            ON CONFLICT (owner_id) DO UPDATE SET
                n_docs = o.n_docs + EXCLUDED.n_docs,
                total_len = o.total_len + EXCLUDED.total_len)
        SELECT count(*) AS "Value" FROM d
        """;

    public async Task FoldAsync(CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        context.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));

        long folded;
        do
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('connapse_bm25_fold'))", ct);
            // ToList, not Single: EF would wrap the statement in a subquery, where PostgreSQL does not
            // allow a data-modifying WITH.
            folded = (await context.Database.SqlQueryRaw<long>(FoldBatchSql, BatchSize).ToListAsync(ct)).Single();
            await transaction.CommitAsync(ct);
        }
        while (folded == BatchSize);
    }
}
