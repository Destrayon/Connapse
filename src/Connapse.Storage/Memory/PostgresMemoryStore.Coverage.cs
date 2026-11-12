using Connapse.Core;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Memory;

// Whether the facts a search showed hold what was asked (CON-337). Ranking fills an answer with the nearest facts even
// when none holds the search's words together, and an agent shown near facts tends to cite them as the answer; this
// check lets memory_recall say plainly that the match is partial. A word is held by a whole-word or prefix match, the
// matches ranking trusts; its trigram look-alikes are not counted.
public sealed partial class PostgresMemoryStore
{
    public async Task<MemoryWordCoverage> CoverageAsync(
        Guid containerId, string text, IReadOnlyCollection<Guid> factIds, CancellationToken ct = default)
    {
        string[] words = [.. QueryWords(text)];
        if (words.Length == 0)
            return MemoryWordCoverage.From([]);
        Guid[] ids = [.. factIds.Distinct()];
        int minLength = MemoryWordCoverage.MinPrefixLength;

        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        List<CoverageRow> rows = await context.Database.SqlQuery<CoverageRow>($"""
            WITH w AS MATERIALIZED (
                SELECT word, ord,
                       to_tsquery('english', CASE WHEN length(word) >= {minLength} THEN word || ':*' ELSE word END) AS q
                FROM unnest({words}) WITH ORDINALITY AS t(word, ord))
            SELECT w.word AS "Word", w.ord AS "Order", numnode(w.q) = 0 AS "Stop", f.id AS "Fact"
            FROM w LEFT JOIN memory_facts f
              ON numnode(w.q) > 0 AND f.container_id = {containerId} AND f.id = ANY({ids}) AND f.search_vector @@ w.q
            """).ToListAsync(ct);
        return MemoryWordCoverage.From(rows.OrderBy(r => r.Order).Select(r => (r.Word, r.Stop, r.Fact)));
    }

    private sealed class CoverageRow
    {
        public string Word { get; init; } = "";
        public long Order { get; init; }
        public bool Stop { get; init; }
        public Guid? Fact { get; init; }
    }
}
