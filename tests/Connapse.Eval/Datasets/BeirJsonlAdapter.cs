using System.Text.Json;
using Connapse.Eval.Model;

namespace Connapse.Eval.Datasets;

/// <summary>mteb/BEIR JSONL layout: corpus.jsonl, queries.jsonl, qrels.tsv with a header row.</summary>
public sealed class BeirJsonlAdapter : IDatasetAdapter
{
    public string Name => "beir-jsonl";

    public async Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct)
    {
        List<(string Id, string? Title, string Text)> corpus = [];
        await foreach (JsonElement row in ReadJsonlAsync(Path.Combine(directory, "corpus.jsonl"), ct))
            corpus.Add((row.GetProperty("_id").GetString()!, Optional(row, "title"), row.GetProperty("text").GetString() ?? ""));

        List<(string Id, string Text)> queries = [];
        await foreach (JsonElement row in ReadJsonlAsync(Path.Combine(directory, "queries.jsonl"), ct))
            queries.Add((row.GetProperty("_id").GetString()!, row.GetProperty("text").GetString() ?? ""));

        Qrels qrels = new();
        foreach (string line in (await File.ReadAllLinesAsync(Path.Combine(directory, "qrels.tsv"), ct)).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            string[] fields = line.Split('\t');
            qrels.Add(fields[0], fields[1], int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture));
        }

        return DatasetAdapters.BuildBeir(datasetName, entry, corpus, queries, qrels);
    }

    private static string? Optional(JsonElement row, string property) =>
        row.TryGetProperty(property, out JsonElement value) ? value.GetString() : null;

    private static async IAsyncEnumerable<JsonElement> ReadJsonlAsync(
        string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using StreamReader reader = new(path);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using JsonDocument doc = JsonDocument.Parse(line);
            yield return doc.RootElement.Clone();
        }
    }
}
