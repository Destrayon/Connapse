using Connapse.Eval.Model;

namespace Connapse.Eval.Datasets;

public interface IDatasetAdapter
{
    string Name { get; }

    Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct);
}

public static class DatasetAdapters
{
    private static readonly Dictionary<string, IDatasetAdapter> All =
        new IDatasetAdapter[] { new BeirParquetAdapter(), new BeirJsonlAdapter(), new RagBenchAdapter() }
            .ToDictionary(a => a.Name, StringComparer.Ordinal);

    public static IDatasetAdapter Get(string name) =>
        All.TryGetValue(name, out IDatasetAdapter? adapter)
            ? adapter
            : throw new InvalidOperationException($"Unknown dataset adapter '{name}'. Known: {string.Join(", ", All.Keys)}.");

    /// <summary>BEIR-shaped sources: queries without judgments are dropped, every query is Test.</summary>
    internal static EvalDataset BuildBeir(
        string name,
        DatasetEntry entry,
        IEnumerable<(string Id, string? Title, string Text)> corpus,
        IEnumerable<(string Id, string Text)> queries,
        Qrels qrels)
    {
        List<(string Id, string? Title, string Text)> corpusList = corpus.ToList();
        List<(string Id, string Text)> queryList = queries.ToList();

        HashSet<string> corpusIds = new(StringComparer.Ordinal);
        foreach ((string Id, string? Title, string Text) document in corpusList)
            if (!corpusIds.Add(document.Id))
                throw new InvalidDataException($"Dataset '{name}' has duplicate corpus ID '{document.Id}'.");

        HashSet<string> queryIdSet = new(StringComparer.Ordinal);
        foreach ((string Id, string Text) query in queryList)
            if (!queryIdSet.Add(query.Id))
                throw new InvalidDataException($"Dataset '{name}' has duplicate query ID '{query.Id}'.");

        HashSet<string> judged = qrels.QueryIds.ToHashSet(StringComparer.Ordinal);
        return new EvalDataset(
            name,
            entry.Version,
            entry.Tags,
            corpusList.Select(c => new EvalDocument(
                c.Id, DocumentKind.Text, string.IsNullOrWhiteSpace(c.Title) ? null : c.Title, c.Text, null,
                new Dictionary<string, string>())).ToList(),
            queryList.Where(q => judged.Contains(q.Id)).Select(q => new EvalQuery(q.Id, q.Text, Split.Test, [])).ToList(),
            qrels);
    }
}
