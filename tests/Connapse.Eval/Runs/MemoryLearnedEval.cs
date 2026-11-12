using System.Text.Json.Nodes;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using Connapse.Eval.Runs.MemorySearch;
using Connapse.Eval.Systems;

namespace Connapse.Eval.Runs;

/// <summary>
/// The memory-learned-v1 suite: how well an agent answers the session-2 questions with search alone, after the late
/// documents, each replacing an older policy, have been uploaded. CON-244's realistic arm compared this with memory that
/// session-1 agents saved in the container; CON-255 removed container memory, so session 1 is gone and this memory-off
/// score is the one to match. Scoring and the prompt are memory-search's; the rules are in <c>eval/pass-bars.md</c>.
/// </summary>
public sealed class MemoryLearnedEval(RepoPaths paths, HttpClient http, TextWriter log)
{
    public const string Dataset = "memory-learned";

    /// <summary>The pre-registered run scores the 104 session-2 test tasks.</summary>
    public const int RegisteredTasks = 104;

    public sealed record LearnedTask(SearchTask Task, string Family, int Session);

    public async Task<int> RunAsync(MemorySearchEval.Options options, CancellationToken ct)
    {
        string agentDir = Path.GetFullPath(options.AgentDir ?? Path.Combine(Path.GetTempPath(), "connapse-memory-learned-agent"));
        Directory.CreateDirectory(agentDir);
        MemorySearchEval.RequireNoProjectMemory(agentDir);

        DatasetEntry entry = EvalManifest.Load(paths.ManifestPath).Datasets[Dataset];
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        await cache.EnsureAsync(Dataset, entry, allowUnpinned: false, ct);
        string root = cache.DirectoryFor(Dataset, entry);
        List<LearnedTask> tasks = Select(ReadTasks(Path.Combine(root, "tasks.jsonl")), options.Split, options.LimitTasks);
        List<EvalDocument> corpus = MemorySearchEval.ReadCorpus(Path.Combine(root, "corpus.jsonl"));
        List<EvalDocument> late = MemorySearchEval.ReadCorpus(Path.Combine(root, "late.jsonl"));
        Dictionary<string, string> fileNames = corpus.Concat(late).ToDictionary(d => d.Id, d => d.FileName!, StringComparer.Ordinal);

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", options.Config);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, log, null, ct);

        IndexReport indexed = await system.IndexAsync(new EvalDataset(Dataset, entry.Version, [], corpus, [], new Qrels()), ct);
        (Guid container, IReadOnlyDictionary<string, string> docMap) = system.ContainerOf(Dataset);
        MemorySearchEval.RequireCompleteIndex(indexed, corpus.Count, docMap.Count);
        IndexReport lateIndexed = await system.IndexAsync(new EvalDataset($"{Dataset}-late", entry.Version, [], late, [], new Qrels()),
            IngestionWait.ThrowOnStall, ct, into: container);
        (_, IReadOnlyDictionary<string, string> lateMap) = system.ContainerOf($"{Dataset}-late");
        MemorySearchEval.RequireCompleteIndex(lateIndexed, late.Count, lateMap.Count);
        Dictionary<string, string> allDocs = docMap.Concat(lateMap).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        log.WriteLine($"{corpus.Count} documents, then {late.Count} late documents indexed");

        McpToolbox tools = new(system.Services, McpToolbox.Reader(Guid.NewGuid(), "memory-learned-eval"), options.ToolSet, MemorySearchEval.Pinned);
        await using ToolBridge bridge = new();
        await bridge.StartAsync(ct);
        ClaudeCliAgent agent = new(bridge, options.Model, options.Effort, agentDir);
        await MemorySearchEval.AnswerAndReportAsync(agent, tools, MemorySearchEval.SystemPrompt(container), [.. tasks.Select(t => t.Task)],
            new DocumentIndex(allDocs, fileNames), options, RegisteredTasks, log, ct);
        return 0;
    }

    /// <summary>The session-2 tasks of <paramref name="split"/>, limited to the first <paramref name="limitFamilies"/> families.</summary>
    public static List<LearnedTask> Select(IReadOnlyList<LearnedTask> tasks, string split, int? limitFamilies)
    {
        List<LearnedTask> second = [.. tasks.Where(t => t.Task.Split == split && t.Session == 2)];
        if (limitFamilies is not int limit)
            return second;
        HashSet<string> kept = [.. second.Select(t => t.Family).Distinct().Take(limit)];
        return [.. second.Where(t => kept.Contains(t.Family))];
    }

    public static List<LearnedTask> ReadTasks(string path)
    {
        List<SearchTask> tasks = MemorySearchEval.ReadTasks(path);
        List<JsonObject> rows = [.. File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject())];
        return [.. tasks.Zip(rows, (t, row) => new LearnedTask(t, (string)row["family"]!, (int)row["session"]!))];
    }
}
