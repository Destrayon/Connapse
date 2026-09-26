using Connapse.Eval.Model;
using Parquet.Serialization;

namespace Connapse.Eval.Datasets;

/// <summary>NanoBEIR layout: corpus/queries/qrels Parquet files; qrels may have no score column.</summary>
public sealed class BeirParquetAdapter : IDatasetAdapter
{
    public string Name => "beir-parquet";

    public async Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct)
    {
        IList<BeirCorpusRow> corpus = await ReadAsync<BeirCorpusRow>(directory, "corpus.parquet", ct);
        IList<BeirQueryRow> queries = await ReadAsync<BeirQueryRow>(directory, "queries.parquet", ct);
        IList<BeirQrelRow> qrelRows = await ReadAsync<BeirQrelRow>(directory, "qrels.parquet", ct);

        Qrels qrels = new();
        foreach (BeirQrelRow row in qrelRows)
            qrels.Add(row.QueryId, row.CorpusId, (int)(row.Score ?? 1));

        return DatasetAdapters.BuildBeir(
            datasetName, entry,
            corpus.Select(c => (c.Id, c.Title, c.Text)),
            queries.Select(q => (q.Id, q.Text)),
            qrels);
    }

    private static async Task<IList<T>> ReadAsync<T>(string directory, string file, CancellationToken ct)
        where T : class, new() =>
        (await ParquetSerializer.DeserializeAsync<T>(Path.Combine(directory, file), cancellationToken: ct)).Data;
}
