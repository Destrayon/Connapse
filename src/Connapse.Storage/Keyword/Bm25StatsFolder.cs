using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Keyword;

/// <summary>
/// Folds the signed rows the chunks triggers append to bm25_delta into bm25_term_stats and
/// bm25_owner_stats (see the AddBm25Statistics migration). Only for speed: readers add unfolded
/// deltas, so statistics are exact whether or not a fold has run.
/// </summary>
public class Bm25StatsFolder(IDbContextFactory<KnowledgeDbContext> contextFactory)
{
    // One folder at a time, across app instances. Upserts go in (owner, term) order so a fold
    // never deadlocks with another writer of the same rows.
    private const string FoldSql = """
        SELECT pg_advisory_xact_lock(hashtext('connapse_bm25_fold'));

        WITH d AS (DELETE FROM bm25_delta RETURNING owner_id, term, d_df, d_n, d_len),
        terms AS (
            INSERT INTO bm25_term_stats AS s (owner_id, term, df)
            SELECT owner_id, term, sum(d_df) FROM d WHERE term IS NOT NULL
            GROUP BY owner_id, term ORDER BY owner_id, term
            ON CONFLICT (owner_id, term) DO UPDATE SET df = s.df + EXCLUDED.df)
        INSERT INTO bm25_owner_stats AS o (owner_id, n_docs, total_len)
        SELECT owner_id, sum(d_n), sum(d_len) FROM d WHERE term IS NULL
        GROUP BY owner_id ORDER BY owner_id
        ON CONFLICT (owner_id) DO UPDATE SET
            n_docs = o.n_docs + EXCLUDED.n_docs,
            total_len = o.total_len + EXCLUDED.total_len;

        DELETE FROM bm25_term_stats WHERE df <= 0;
        DELETE FROM bm25_owner_stats WHERE n_docs <= 0;
        """;

    public async Task FoldAsync(CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await context.Database.ExecuteSqlRawAsync(FoldSql, ct);
        await transaction.CommitAsync(ct);
    }
}
