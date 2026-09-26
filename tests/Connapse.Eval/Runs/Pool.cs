using Connapse.Eval.Metrics;
using Connapse.Eval.Model;

namespace Connapse.Eval.Runs;

public sealed record PoolItem(string Dataset, string QueryId, string QueryText, string DocId);

public static class Pool
{
    /// <summary>Top-10 documents, across all given runs, that have no judgment for their query.</summary>
    public static IReadOnlyList<PoolItem> Unjudged(IEnumerable<RunFolder> runs)
    {
        HashSet<PoolItem> items = [];
        foreach (RunFolder run in runs)
            foreach (RunDatasetInfo info in run.ReadManifest().Datasets.Where(d => run.IsDatasetComplete(d.Name)))
            {
                Qrels qrels = run.ReadQrels(info.Name);
                foreach (QueryResult result in run.ReadResults(info.Name))
                    foreach (RankedDoc doc in RankingMetrics.Order(result.Ranked).Take(EvalRunner.K))
                        if (!qrels.For(result.QueryId).ContainsKey(doc.DocId))
                            items.Add(new PoolItem(info.Name, result.QueryId, result.QueryText, doc.DocId));
            }
        return items.OrderBy(i => i.Dataset, StringComparer.Ordinal)
            .ThenBy(i => i.QueryId, StringComparer.Ordinal)
            .ThenBy(i => i.DocId, StringComparer.Ordinal)
            .ToList();
    }
}
