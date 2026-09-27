namespace Connapse.Search.Keyword;

/// <summary>
/// Score bounds and candidate queries for exact top-k BM25 over the frequency markers in
/// search_vector (#548). See docs/research/bm25-sql-latency-parity-2026-09-27.md.
/// <para>
/// A chunk's "level" for a term is the highest tier its frequency reaches: level 0 means the bare
/// lexeme is present (frequency ≥ 1), level j means the marker for <see cref="Tiers"/>[j] is.
/// BM25's per-term contribution w·f/(f + k1·(1 − b + b·dl/avgdl)) rises with f and falls with dl,
/// so a chunk at level j contributes at most the value at the tier's highest frequency and the
/// term's shortest chunk. A chunk's score is at most the sum of its levels' bounds.
/// </para>
/// <para>
/// <see cref="Clauses"/> turns "every chunk whose bound exceeds θ" into an OR of AND-clauses over
/// lexemes and markers that the GIN index answers directly: the same effect as Block-Max WAND's
/// skipping, with the per-chunk tiers standing in for per-block maxima (Ding &amp; Suel, SIGIR 2011;
/// impact tiers after Anh &amp; Moffat, SIGIR 2006).
/// </para>
/// </summary>
internal static class Bm25Pruning
{
    /// <summary>
    /// Frequency tiers; index 0 is the bare lexeme. Must match the tiers of bm25_markers in the
    /// AddBm25Statistics migration.
    /// </summary>
    internal static readonly int[] Tiers = [1, 2, 3, 4, 5, 6, 8, 11, 16];

    /// <summary>A query term: its lexeme, BM25 weight (query frequency × IDF) and pruning statistics.</summary>
    internal sealed record Term(string Lexeme, double Weight, int MaxTf, int MinLen);

    /// <summary>
    /// The term's contribution bound for a chunk at each level it can reach, ascending.
    /// </summary>
    internal static double[] LevelBounds(Term term, double k1, double b, double avgdl)
    {
        int maxTf = Math.Max(term.MaxTf, 1);
        var bounds = new List<double>();
        for (int j = 0; j < Tiers.Length && Tiers[j] <= maxTf; j++)
        {
            int highest = j + 1 < Tiers.Length ? Math.Min(Tiers[j + 1] - 1, maxTf) : maxTf;
            bounds.Add(term.Weight * TfPart(highest, term.MinLen, k1, b, avgdl));
        }
        return [.. bounds];
    }

    internal static double TfPart(double f, double dl, double k1, double b, double avgdl) =>
        f / (f + k1 * (1 - b + b * dl / avgdl));

    /// <summary>
    /// Minimal level requirements (term index → level) such that every chunk whose score bound
    /// exceeds <paramref name="threshold"/> satisfies at least one of them. Terms whose combined
    /// maximum stays within <paramref name="slack"/> × threshold are not required by any clause
    /// (their maximum is assumed instead), which keeps near-zero-weight terms from multiplying the
    /// clauses. Null when the answer is "every chunk" or would need more than
    /// <paramref name="maxClauses"/> clauses; the caller then scores every match.
    /// </summary>
    internal static List<Dictionary<int, int>>? Clauses(
        IReadOnlyList<double[]> bounds, double threshold, double slack = 0.3, int maxClauses = 64)
    {
        int[] order = Enumerable.Range(0, bounds.Count)
            .Where(i => bounds[i].Length > 0)
            .OrderBy(i => bounds[i][^1])
            .ToArray();

        double slackSum = 0;
        int firstRequired = 0;
        while (firstRequired < order.Length && slackSum + bounds[order[firstRequired]][^1] <= slack * threshold)
            slackSum += bounds[order[firstRequired++]][^1];

        // Highest bound first, so the search closes clauses early.
        int[] required = order[firstRequired..].Reverse().ToArray();
        // Chunks within a hair of the threshold are fetched too: bounds here and scores in SQL are
        // computed separately, and a missed tie would be the only way to lose exactness.
        double target = (threshold - slackSum) * (1 - 1e-9);
        if (target <= 0 || required.Length == 0)
            return null;

        double[] suffixMax = new double[required.Length + 1];
        for (int i = required.Length - 1; i >= 0; i--)
            suffixMax[i] = suffixMax[i + 1] + bounds[required[i]][^1];

        var clauses = new List<Dictionary<int, int>>();
        var current = new Dictionary<int, int>();
        int budget = maxClauses * 8;

        bool Search(int i, double sum)
        {
            if (sum > target)
            {
                if (--budget < 0)
                    return false;
                clauses.Add(new Dictionary<int, int>(current));
                return true;
            }
            if (i == required.Length || sum + suffixMax[i] <= target)
                return true;

            if (!Search(i + 1, sum))
                return false;

            int term = required[i];
            double[] levels = bounds[term];
            for (int j = 0; j < levels.Length; j++)
            {
                current[term] = j;
                bool closes = sum + levels[j] > target;
                bool ok = Search(i + 1, sum + levels[j]);
                current.Remove(term);
                if (!ok)
                    return false;
                // A higher level with the same prefix is a stricter requirement: already covered.
                if (closes)
                    break;
            }
            return true;
        }

        if (!Search(0, 0))
            return null;

        List<Dictionary<int, int>> unique = clauses
            .DistinctBy(c => string.Join(",", c.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}")))
            .ToList();
        List<Dictionary<int, int>> minimal = unique
            .Where(c => !unique.Any(d => !ReferenceEquals(c, d) && Covers(d, c)))
            .ToList();
        return minimal.Count <= maxClauses ? minimal : null;
    }

    /// <summary>True when every chunk satisfying <paramref name="stricter"/> also satisfies <paramref name="looser"/>.</summary>
    private static bool Covers(Dictionary<int, int> looser, Dictionary<int, int> stricter) =>
        looser.All(p => stricter.TryGetValue(p.Key, out int level) && level >= p.Value);

    /// <summary>The clauses as tsquery text: an OR of AND-clauses over lexemes and markers.</summary>
    internal static string ToTsQuery(IReadOnlyList<Dictionary<int, int>> clauses, IReadOnlyList<Term> terms) =>
        string.Join(" | ", clauses.Select(clause =>
            "(" + string.Join(" & ", clause.OrderBy(p => p.Key).Select(p => Quote(Marker(terms[p.Key].Lexeme, p.Value)))) + ")"));

    internal static string Marker(string lexeme, int level) =>
        level == 0 ? lexeme : lexeme + '\u001F' + Tiers[level];

    /// <summary>A lexeme as a tsquery literal: quoted, with quotes doubled and backslashes escaped.</summary>
    internal static string Quote(string lexeme) =>
        "'" + lexeme.Replace("\\", "\\\\").Replace("'", "''") + "'";
}
