using System.Globalization;
using System.Text.Json;
using Connapse.Eval.Metrics;
using Connapse.Eval.Model;

namespace Connapse.Eval.Runs;

/// <summary>
/// eval/runs/{timestamp}-{sha7}-{suite}-{system}-{config}/ — self-contained: manifest, per-dataset
/// results, qrels and titles copies, TREC run files, and reports.
/// </summary>
public sealed class RunFolder
{
    private RunFolder(string path) => Path = path;

    public string Path { get; }

    public string Name => new DirectoryInfo(Path).Name;

    private string ManifestPath => System.IO.Path.Combine(Path, "manifest.json");

    public static RunFolder Create(string runsRoot, string suite, string system, string config, string gitSha, DateTimeOffset now)
    {
        string sha7 = gitSha.Length > 7 ? gitSha[..7] : gitSha;
        string path = System.IO.Path.Combine(runsRoot, $"{now.UtcDateTime:yyyyMMdd-HHmmss}-{sha7}-{suite}-{system}-{config}");
        Directory.CreateDirectory(path);
        return new RunFolder(path);
    }

    public static RunFolder Open(string path)
    {
        RunFolder run = new(System.IO.Path.GetFullPath(path));
        if (!System.IO.File.Exists(run.ManifestPath))
            throw new DirectoryNotFoundException($"{path} is not a run folder (no manifest.json).");
        return run;
    }

    public void WriteManifest(RunManifest manifest) =>
        System.IO.File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, EvalJson.Options) + "\n");

    public RunManifest ReadManifest() =>
        JsonSerializer.Deserialize<RunManifest>(System.IO.File.ReadAllText(ManifestPath), EvalJson.Options)
        ?? throw new InvalidOperationException($"{ManifestPath} is empty.");

    public bool IsDatasetComplete(string dataset) => System.IO.File.Exists(File("results", dataset + ".done"));

    public void WriteDataset(string dataset, Qrels qrels, IReadOnlyDictionary<string, string> titles, IReadOnlyList<QueryResult> results)
    {
        using (StreamWriter writer = new(File("results", dataset + ".jsonl")))
            foreach (QueryResult result in results)
                writer.Write(JsonSerializer.Serialize(result, EvalJson.Line) + "\n");

        using (StreamWriter writer = new(File("qrels", dataset + ".tsv")))
            qrels.WriteTrec(writer);

        System.IO.File.WriteAllText(File("titles", dataset + ".json"), JsonSerializer.Serialize(titles, EvalJson.Options));

        using StreamWriter trec = new(File("trec", dataset + ".trec"));
        foreach (QueryResult result in results)
        {
            IReadOnlyList<RankedDoc> ordered = RankingMetrics.Order(result.Ranked);
            for (int i = 0; i < ordered.Count; i++)
                trec.Write($"{result.QueryId} Q0 {ordered[i].DocId} {i + 1} {ordered[i].Score.ToString("R", CultureInfo.InvariantCulture)} eval\n");
        }
    }

    /// <summary>Written after the manifest entry, so resume redoes any dataset that did not finish.</summary>
    public void MarkComplete(string dataset) => System.IO.File.WriteAllText(File("results", dataset + ".done"), "");

    public IReadOnlyList<QueryResult> ReadResults(string dataset) =>
        System.IO.File.ReadLines(File("results", dataset + ".jsonl"))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<QueryResult>(l, EvalJson.Options)!)
            .ToList();

    public Qrels ReadQrels(string dataset)
    {
        using StreamReader reader = new(File("qrels", dataset + ".tsv"));
        return Qrels.ParseTrec(reader);
    }

    public IReadOnlyDictionary<string, string> ReadTitles(string dataset) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(File("titles", dataset + ".json")), EvalJson.Options)
        ?? new Dictionary<string, string>();

    public void WriteText(string fileName, string content) => System.IO.File.WriteAllText(System.IO.Path.Combine(Path, fileName), content);

    private string File(string folder, string name)
    {
        string directory = System.IO.Path.Combine(Path, folder);
        Directory.CreateDirectory(directory);
        return System.IO.Path.Combine(directory, name);
    }
}
