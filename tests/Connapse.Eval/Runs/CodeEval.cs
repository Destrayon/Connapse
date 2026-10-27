using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using Connapse.Eval.Systems;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Eval.Runs;

/// <summary>
/// The code-questions gate (CON-37): each question asked of the code tools the way an agent would ask it, over the
/// four pinned repositories indexed as a synced repository's files would be (their paths kept, code chunked at its
/// definitions, links extracted). Ranked files are scored as <c>grep_baseline.py</c> scores grep: Recall@k is
/// |top-k ∩ key| / min(|key|, k), grouped the same way, and the bar in <c>gate.json</c> is checked on the held-out half.
/// <list type="bullet">
/// <item><b>definition</b>: <c>code_find_symbol</c>, the definitions' files first.</item>
/// <item><b>callers</b>: <c>code_references</c> with kind calls.</item>
/// <item><b>usages</b>: <c>code_references</c> with kind all.</item>
/// <item><b>how</b>: <c>code_find_symbol</c> with follow.</item>
/// </list>
/// </summary>
public sealed partial class CodeEval(RepoPaths paths, HttpClient http, TextWriter log)
{
    /// <summary>The manifest dataset whose pinned files this reads; code-v1 is its only suite.</summary>
    public const string Dataset = "code-questions";

    private static readonly int[] Ks = [5, 10, 20];

    /// <summary>The extensions the code parser reads; the answer keys hold only these.</summary>
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".mts", ".cts", ".tsx", ".py", ".pyi",
    };

    public sealed record Repo(string Name, string Url, string Commit);

    public sealed record Question(string Id, string Repo, string Kind, string Name, string Split, string[] Files);

    public async Task<int> RunAsync(string configName, string split, string? outPath, CancellationToken ct)
    {
        DatasetEntry entry = EvalManifest.Load(paths.ManifestPath).Datasets[Dataset];
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        await cache.EnsureAsync(Dataset, entry, allowUnpinned: false, ct);
        string root = cache.DirectoryFor(Dataset, entry);
        List<Question> questions = [.. ReadQuestions(Path.Combine(root, "questions.jsonl")).Where(q => q.Split == split)];
        Dictionary<string, Repo> repos = ReadRepos(Path.Combine(root, "repos.json"));
        JsonObject gate = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "gate.json")))!.AsObject();
        JsonObject grep = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "grep-baseline.json")))!["groups"]!.AsObject();

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", configName);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(
            config, paths.WebContentRoot, embeddings, log, new HashingEmbeddingProvider(), ct);

        Dictionary<string, Dictionary<int, double>> recalls = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> rankedOf = new(StringComparer.Ordinal);
        Dictionary<string, string> crowdOf = new(StringComparer.Ordinal);
        Dictionary<string, List<double>> latencyMs = new(StringComparer.Ordinal);
        foreach (string name in questions.Select(q => q.Repo).Distinct().Order(StringComparer.Ordinal))
        {
            string tree = await CheckoutAsync(repos[name], ct);
            Dictionary<string, int> filesHolding = WordFileCounts(tree);
            string dataset = $"code-{name}";
            List<EvalDocument> corpus = [.. CodeFiles(tree)];
            IndexReport report = await system.IndexAsync(new EvalDataset(dataset, repos[name].Commit, [], corpus, [], new Qrels()), ct);
            log.WriteLine($"{name}: {corpus.Count} code files indexed, {report.Failed} failed");
            (Guid container, _) = system.ContainerOf(dataset);
            var scope = new CodeScope(container, null);

            foreach (Question q in questions.Where(q => q.Repo == name))
            {
                await using AsyncServiceScope services = system.Services.CreateAsyncScope();
                ICodeTools code = services.ServiceProvider.GetRequiredService<ICodeTools>();
                var timer = Stopwatch.StartNew();
                List<string> ranked = await AskAsync(code, scope, q, ct);
                string tool = q.Kind is "definition" or "how" ? "code_find_symbol" : "code_references";
                (latencyMs.TryGetValue(tool, out List<double>? times) ? times : latencyMs[tool] = []).Add(timer.Elapsed.TotalMilliseconds);
                recalls[q.Id] = Ks.ToDictionary(k => k, k => Recall(ranked, q.Files, k));
                rankedOf[q.Id] = ranked;
                // As grep_baseline.py: crowded when more files hold the name than the top 10 has room for.
                crowdOf[q.Id] = filesHolding.GetValueOrDefault(q.Name.Split('.')[^1]) > 10 ? "crowded" : "sparse";
            }
        }

        JsonObject result = Report(configName, split, questions, recalls, crowdOf, latencyMs, gate, grep);
        // Every question's top ten beside its key, for reading where a group loses.
        result["questions"] = new JsonArray([.. questions.Select(q => (JsonNode)new JsonObject
        {
            ["id"] = q.Id,
            ["name"] = q.Name,
            ["crowd"] = crowdOf[q.Id],
            ["recall@10"] = Round(recalls[q.Id][10]),
            ["ranked"] = new JsonArray([.. rankedOf[q.Id].Take(10).Select(p => (JsonNode)p)]),
            ["key"] = new JsonArray([.. q.Files.Select(p => (JsonNode)p)]),
        })]);
        string json = result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (outPath is not null)
            await File.WriteAllTextAsync(outPath, json + "\n", ct);
        log.WriteLine(json);
        return result["gate"]?["passed"]?.GetValue<bool>() == false ? 3 : 0;
    }

    /// <summary>The files an agent would read, best first, from the call it would make for the question's kind.</summary>
    public static async Task<List<string>> AskAsync(ICodeTools code, CodeScope scope, Question q, CancellationToken ct)
    {
        IEnumerable<string> ranked;
        switch (q.Kind)
        {
            case "definition":
            case "how":
                CodeSymbolResult found = await code.FindSymbolAsync(scope, q.Name, CodeMatch.Exact, follow: q.Kind == "how", 20, ct);
                ranked = found.Definitions.Select(d => d.Path).Concat(found.Files.Select(f => f.Path));
                break;
            default:
                CodeReferencesResult refs = await code.ReferencesAsync(scope, q.Name,
                    q.Kind == "callers" ? CodeReferenceKind.Calls : CodeReferenceKind.All, 20, ct);
                ranked = refs.Files.Select(f => f.Path).Concat(refs.Definitions.Select(d => d.Path));
                break;
        }
        return [.. ranked.Select(p => p.TrimStart('/')).Distinct(StringComparer.Ordinal)];
    }

    public static double Recall(IReadOnlyList<string> ranked, IReadOnlyCollection<string> key, int k) =>
        (double)ranked.Take(k).Count(key.Contains) / Math.Min(key.Count, k);

    private static JsonObject Report(string config, string split, List<Question> questions,
        Dictionary<string, Dictionary<int, double>> recalls, Dictionary<string, string> crowdOf,
        Dictionary<string, List<double>> latencyMs, JsonObject gate, JsonObject grep)
    {
        Dictionary<string, List<Question>> groups = new(StringComparer.Ordinal);
        foreach (Question q in questions)
        {
            string crowd = crowdOf[q.Id];
            foreach (string g in new[] { split, $"{split}/{q.Kind}", $"{split}/{crowd}", $"{split}/{q.Kind}/{q.Repo}", $"{split}/{crowd}/{q.Kind}" })
                (groups.TryGetValue(g, out List<Question>? list) ? list : groups[g] = []).Add(q);
        }

        JsonObject scored = [];
        foreach ((string g, List<Question> members) in groups.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            JsonObject row = new() { ["questions"] = members.Count };
            foreach (int k in Ks)
                row[$"recall@{k}"] = Round(members.Average(q => recalls[q.Id][k]));
            if (grep[g] is JsonObject baseline)
                row["grepRecall@10"] = baseline["recall@10"]!.GetValue<double>();
            scored[g] = row;
        }

        JsonObject latency = [];
        foreach ((string tool, List<double> times) in latencyMs.OrderBy(p => p.Key, StringComparer.Ordinal))
            latency[tool] = new JsonObject { ["calls"] = times.Count, ["p50Ms"] = Round(Percentile(times, 0.5)), ["p95Ms"] = Round(Percentile(times, 0.95)) };
        double symbolP95 = latencyMs.TryGetValue("code_find_symbol", out List<double>? lookups) ? Percentile(lookups, 0.95) : 0;

        JsonObject report = new()
        {
            ["config"] = config,
            ["split"] = split,
            ["groups"] = scored,
            ["latency"] = latency,
        };
        // The bar is set on one split, and is only checked there.
        if ((string)gate["split"]! == split)
        {
            string metric = (string)gate["metric"]!;
            JsonObject checks = [];
            bool passed = symbolP95 < 300;
            foreach ((string g, JsonNode? bar) in gate["pass"]!.AsObject())
            {
                double got = scored[g]?[metric]?.GetValue<double>() ?? 0;
                bool ok = got >= bar!.GetValue<double>();
                checks[g] = new JsonObject { [metric] = got, ["bar"] = bar.GetValue<double>(), ["passed"] = ok };
                passed &= ok;
            }
            checks["symbolLookupP95Under300Ms"] = symbolP95 < 300;
            checks["passed"] = passed;
            report["gate"] = checks;
        }
        return report;
    }

    /// <summary>
    /// The repository at its pinned commit, from GitHub's archive of that commit, cached by commit so a run downloads
    /// it once. The archive holds the tracked files, as <c>git ls-files</c> lists them for the grep baseline.
    /// </summary>
    private async Task<string> CheckoutAsync(Repo repo, CancellationToken ct)
    {
        string tree = Path.Combine(paths.CacheRoot, "code-repos", $"{repo.Name}-{repo.Commit[..12]}");
        if (Directory.Exists(tree))
            return tree;

        string[] parts = new Uri(repo.Url).AbsolutePath.Trim('/').Split('/');
        string archive = $"https://codeload.github.com/{parts[0]}/{Path.GetFileNameWithoutExtension(parts[1])}/zip/{repo.Commit}";
        log.WriteLine($"{repo.Name}: downloading {archive}");
        string staging = tree + ".partial";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        await using (Stream zip = await http.GetStreamAsync(archive, ct))
        {
            using ZipArchive files = new(zip, ZipArchiveMode.Read);
            foreach (ZipArchiveEntry file in files.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                // Every entry sits under one "<repo>-<commit>/" folder, which is dropped.
                string relative = file.FullName[(file.FullName.IndexOf('/') + 1)..];
                string target = Path.GetFullPath(Path.Combine(staging, relative));
                if (!target.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException($"{repo.Name}: archive entry '{file.FullName}' leaves the folder.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                file.ExtractToFile(target);
            }
        }
        Directory.Move(staging, tree);
        return tree;
    }

    /// <summary>Every code file, uploaded to its own path as a synced repository's file would be.</summary>
    private static IEnumerable<EvalDocument> CodeFiles(string tree)
    {
        foreach (string file in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(tree, file).Replace('\\', '/');
            if (!CodeExtensions.Contains(Path.GetExtension(file)) || IsBinary(file))
                continue;
            string folder = "/" + (Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "");
            yield return new EvalDocument(relative, DocumentKind.Text, null, File.ReadAllText(file, Encoding.UTF8), null,
                new Dictionary<string, string>())
            {
                FileName = Path.GetFileName(relative),
                Folder = folder,
                // Not a strategy a user can pick, so the upload routes the file by its extension, as a synced one is.
                Strategy = "Code",
            };
        }
    }

    /// <summary>For each whole word, how many text files hold it on some line: <c>grep_baseline.py</c>'s corpus.</summary>
    private static Dictionary<string, int> WordFileCounts(string tree)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories))
        {
            if (IsBinary(file))
                continue;
            HashSet<string> words = new(StringComparer.Ordinal);
            foreach (Match m in Word().Matches(File.ReadAllText(file, Encoding.UTF8)))
                words.Add(m.Value);
            foreach (string w in words)
                counts[w] = counts.GetValueOrDefault(w) + 1;
        }
        return counts;
    }

    private static bool IsBinary(string file)
    {
        using FileStream stream = File.OpenRead(file);
        byte[] head = new byte[8192];
        int read = stream.Read(head, 0, head.Length);
        return Array.IndexOf(head, (byte)0, 0, read) >= 0;
    }

    private static double Percentile(List<double> xs, double p)
    {
        List<double> sorted = [.. xs.Order()];
        return sorted.Count == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * p) - 1)];
    }

    private static double Round(double x) => Math.Round(x, 4);

    private static IEnumerable<Question> ReadQuestions(string path) =>
        File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).Select(q => new Question(
            (string)q["id"]!, (string)q["repo"]!, (string)q["kind"]!, (string)q["name"]!, (string)q["split"]!,
            [.. q["answer"]!["files"]!.AsArray().Select(f => (string)f!)]));

    private static Dictionary<string, Repo> ReadRepos(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!["repos"]!.AsArray()
            .Select(r => new Repo((string)r!["name"]!, (string)r["url"]!, (string)r["commit"]!))
            .ToDictionary(r => r.Name, StringComparer.Ordinal);

    [GeneratedRegex(@"[A-Za-z_$][\w$]*")]
    private static partial Regex Word();
}
