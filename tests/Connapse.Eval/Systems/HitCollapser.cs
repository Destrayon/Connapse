using Connapse.Core;
using Connapse.Eval.Model;

namespace Connapse.Eval.Systems;

public static class HitCollapser
{
    /// <summary>
    /// Collapses chunk hits, in Connapse's rank order, to documents by first (best) occurrence,
    /// translating Connapse document IDs to dataset IDs. Hits for unknown documents are skipped.
    /// </summary>
    public static IReadOnlyList<RankedDoc> Collapse(
        IEnumerable<SearchHit> hits, IReadOnlyDictionary<string, string> connapseToDataset, int k)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<RankedDoc> ranked = [];
        foreach (SearchHit hit in hits)
        {
            if (!connapseToDataset.TryGetValue(hit.DocumentId, out string? docId) || !seen.Add(docId))
                continue;
            ranked.Add(new RankedDoc(docId, hit.Score));
            if (ranked.Count == k)
                break;
        }
        return ranked;
    }
}
