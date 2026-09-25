using Connapse.Eval.Model;

namespace Connapse.Eval.Metrics;

public static class MetricNames
{
    public const string Mrr10 = "MRR@10";
    public const string Ndcg10 = "nDCG@10";
    public const string Recall5 = "Recall@5";
    public const string Recall10 = "Recall@10";
    public const string P5 = "P@5";
    public const string Hit1 = "hit@1";
    public const string Hit3 = "hit@3";
    public const string Judged10 = "judged@10";

    /// <summary>Metrics that measure ranking quality; judged@10 describes coverage instead.</summary>
    public static readonly IReadOnlyList<string> Quality = [Mrr10, Ndcg10, Recall5, Recall10, P5, Hit1, Hit3];

    public static readonly IReadOnlyList<string> All = [.. Quality, Judged10];
}

public sealed class DuplicateDocumentException(string docId)
    : Exception($"Document '{docId}' appears more than once in one ranked list.");

/// <summary>
/// Per-query ranking metrics, following trec_eval's conventions: order by score descending with
/// ties broken by document ID descending (ordinal), relevant means grade ≥ 1, linear nDCG gain.
/// </summary>
public static class RankingMetrics
{
    private const int K = 10;

    public static IReadOnlyList<RankedDoc> Order(IReadOnlyList<RankedDoc> ranked)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (RankedDoc doc in ranked)
            if (!seen.Add(doc.DocId))
                throw new DuplicateDocumentException(doc.DocId);

        return ranked
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.DocId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Returns null when the judgments contain no relevant document (a no-answer query).</summary>
    public static IReadOnlyDictionary<string, double>? Score(
        IReadOnlyList<RankedDoc> ranked, IReadOnlyDictionary<string, int> judgments)
    {
        IReadOnlyList<RankedDoc> ordered = Order(ranked);
        int totalRelevant = judgments.Values.Count(g => g >= 1);
        if (totalRelevant == 0)
            return null;

        int depth = Math.Min(K, ordered.Count);
        double reciprocalRank = 0;
        double dcg = 0;
        int relevantIn5 = 0;
        int relevantIn10 = 0;
        int judged = 0;
        bool hit1 = false;
        bool hit3 = false;

        for (int i = 0; i < depth; i++)
        {
            int rank = i + 1;
            if (!judgments.TryGetValue(ordered[i].DocId, out int grade))
                continue;
            judged++;
            if (grade < 1)
                continue;

            dcg += grade / Math.Log2(rank + 1);
            if (reciprocalRank == 0)
                reciprocalRank = 1.0 / rank;
            if (rank <= 5)
                relevantIn5++;
            relevantIn10++;
            hit1 |= rank == 1;
            hit3 |= rank <= 3;
        }

        double idealDcg = judgments.Values
            .Where(g => g >= 1)
            .OrderByDescending(g => g)
            .Take(K)
            .Select((g, i) => g / Math.Log2(i + 2))
            .Sum();

        return new Dictionary<string, double>
        {
            [MetricNames.Mrr10] = reciprocalRank,
            [MetricNames.Ndcg10] = dcg / idealDcg,
            [MetricNames.Recall5] = (double)relevantIn5 / totalRelevant,
            [MetricNames.Recall10] = (double)relevantIn10 / totalRelevant,
            [MetricNames.P5] = relevantIn5 / 5.0,
            [MetricNames.Hit1] = hit1 ? 1 : 0,
            [MetricNames.Hit3] = hit3 ? 1 : 0,
            [MetricNames.Judged10] = depth == 0 ? 0 : (double)judged / depth,
        };
    }
}
