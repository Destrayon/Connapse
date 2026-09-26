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
        string[] lines = await File.ReadAllLinesAsync(Path.Combine(directory, "qrels.tsv"), ct);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;
            int lineNumber = i + 1;
            string[] fields = line.Split('\t');
            if (fields.Length != 3
                || !int.TryParse(fields[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int grade))
                throw new InvalidDataException($"qrels.tsv line {lineNumber}: expected 'query-id<TAB>corpus-id<TAB>score', got '{line}'.");
            if (qrels.For(fields[0]).ContainsKey(fields[1]))
                throw new InvalidDataException($"qrels.tsv line {lineNumber}: duplicate judgment for query '{fields[0]}' and document '{fields[1]}'.");
            qrels.Add(fields[0], fields[1], grade);
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
