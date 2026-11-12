using Connapse.Core;

namespace Connapse.Application.Tables;

/// <summary>
/// Ranks a container's tables against a search phrase (CON-310), for containers with too many tables to read through.
/// Each table's <see cref="TableCard"/> is scored by BM25 on its words and, when given, by its embedding's similarity
/// to the phrase; the two orders are merged by reciprocal rank fusion. Every table is ranked and none is dropped:
/// tables that match neither way come last, by name.
/// </summary>
public static class TableSearch
{
    /// <summary>Above this many visible tables, a search ranks them instead of filtering by substring.</summary>
    public const int ManyTables = 50;

    /// <summary>Reciprocal rank fusion's constant, from Cormack, Clarke and Büttcher (2009).</summary>
    public const int FusionK = 60;

    private const double K1 = 1.2;
    private const double B = 0.75;

    /// <summary>
    /// Ranks <paramref name="cards"/> for <paramref name="query"/>. <paramref name="similarities"/> holds each embedded
    /// card's similarity by table id; null or empty ranks by keywords alone. Ties keep the order of
    /// <paramref name="cards"/>, so callers pass them sorted by name.
    /// </summary>
    public static IReadOnlyList<Guid> Rank(
        IReadOnlyList<TableCard> cards, string query, IReadOnlyDictionary<Guid, double>? similarities = null)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(query);
        double[] keyword = Bm25(cards, query);
        var fused = new double[cards.Count];
        Fuse(fused, keyword, i => keyword[i] > 0);
        if (similarities is { Count: > 0 })
        {
            double[] vector = cards.Select(c => similarities.TryGetValue(c.TableId, out double s) ? s : double.NegativeInfinity).ToArray();
            Fuse(fused, vector, i => !double.IsNegativeInfinity(vector[i]));
        }

        return Enumerable.Range(0, cards.Count)
            .OrderByDescending(i => fused[i])
            .ThenBy(i => i)
            .Select(i => cards[i].TableId)
            .ToList();
    }

    /// <summary>
    /// The BM25 score of each card for <paramref name="query"/>, with k1 = 1.2 and b = 0.75, and the IDF of Lucene's
    /// BM25Similarity, which stays positive for a term in every card.
    /// </summary>
    public static double[] Bm25(IReadOnlyList<TableCard> cards, string query)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var scores = new double[cards.Count];
        HashSet<string> queryTerms = [.. Terms(query)];
        if (cards.Count == 0 || queryTerms.Count == 0)
            return scores;

        List<Dictionary<string, int>> counts = cards
            .Select(c => Terms(c.Text).GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal))
            .ToList();
        double[] lengths = counts.Select(c => (double)c.Values.Sum()).ToArray();
        double average = Math.Max(lengths.Average(), 1);
        foreach (string term in queryTerms)
        {
            int containing = counts.Count(c => c.ContainsKey(term));
            if (containing == 0)
                continue;
            double idf = Math.Log(1 + (cards.Count - containing + 0.5) / (containing + 0.5));
            for (int i = 0; i < cards.Count; i++)
            {
                if (!counts[i].TryGetValue(term, out int tf))
                    continue;
                scores[i] += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * lengths[i] / average));
            }
        }
        return scores;
    }

    /// <summary>
    /// The words of <paramref name="text"/> as search terms: runs of letters and digits, lowercased, with a light plural
    /// fold so "categories" matches "category" and "orders" matches "order".
    /// </summary>
    public static IEnumerable<string> Terms(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool word = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (word && start < 0)
                start = i;
            else if (!word && start >= 0)
            {
                yield return Fold(text[start..i].ToLowerInvariant());
                start = -1;
            }
        }
    }

    private static string Fold(string term)
    {
        if (term.Length > 4 && term.EndsWith("ies", StringComparison.Ordinal))
            return term[..^3] + "y";
        if (term.Length > 4 && term.EndsWith("sses", StringComparison.Ordinal))
            return term[..^2];
        if (term.Length > 3 && term.EndsWith('s') && !term.EndsWith("ss", StringComparison.Ordinal)
            && !term.EndsWith("us", StringComparison.Ordinal) && !term.EndsWith("is", StringComparison.Ordinal))
            return term[..^1];
        return term;
    }

    /// <summary>Adds each scored item's 1 / (k + rank) to <paramref name="fused"/>, ranking by score, ties by position.</summary>
    private static void Fuse(double[] fused, double[] scores, Func<int, bool> scored)
    {
        int rank = 0;
        foreach (int i in Enumerable.Range(0, scores.Length).Where(scored).OrderByDescending(i => scores[i]).ThenBy(i => i))
            fused[i] += 1.0 / (FusionK + ++rank);
    }
}
