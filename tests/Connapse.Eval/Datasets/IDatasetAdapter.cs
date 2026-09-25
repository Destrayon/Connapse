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
        HashSet<string> judged = qrels.QueryIds.ToHashSet(StringComparer.Ordinal);
        return new EvalDataset(
            name,
            entry.Version,
            entry.Tags,
            corpus.Select(c => new EvalDocument(
                c.Id, DocumentKind.Text, string.IsNullOrWhiteSpace(c.Title) ? null : c.Title, c.Text, null,
                new Dictionary<string, string>())).ToList(),
            queries.Where(q => judged.Contains(q.Id)).Select(q => new EvalQuery(q.Id, q.Text, Split.Test, [])).ToList(),
            qrels);
    }
}
