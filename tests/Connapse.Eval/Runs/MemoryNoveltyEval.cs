using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Application;
using Connapse.Application.Memory;
using Connapse.Application.Search;
using Connapse.Core;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using Connapse.Eval.Runs.MemorySearch;
using Connapse.Eval.Systems;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Eval.Runs;

/// <summary>
/// The CON-245 sorting check, with no agent: each labelled memory note is searched against its container as the
/// save check does it, and scored for how much of it the passages already say. The rule (score and cutoff) is picked
/// on the dev split by the protocol in <c>eval/pass-bars.md</c>, then the test split is scored once.
/// </summary>
public sealed class MemoryNoveltyEval(RepoPaths paths, HttpClient http, TextWriter log)
{
    /// <summary>The CON-244 realistic run whose session-1 saves are the agent-written notes.</summary>
    public const string LearnedRuns = "eval/agent-search/con-244/memory-learned-test.runs.jsonl";

    public const double MinNewKept = 0.90;
    public const double BarRestatingHeld = 0.80;

    /// <summary>One note to sort. <c>Restates</c> is true when a document already says it.</summary>
    public sealed record Note(string Id, string Source, string Split, bool Restates, string Subject, string Attribute, string Value);

    /// <summary>The candidate scores of one note.</summary>
    public sealed record Scored(Note Note, IReadOnlyDictionary<string, double> Scores, string? TopFile);

    public sealed record Rule(string Score, double Cutoff, double RestatingHeld, double NewKept);

    public static readonly string[] Candidates = ["best-passage", "top3-together", "value-best-passage", "semantic-cosine"];

    public async Task<int> RunAsync(string configName, string? outPath, CancellationToken ct)
    {
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        EvalManifest manifest = EvalManifest.Load(paths.ManifestPath);
        string searchRoot = await RootAsync(cache, manifest, MemorySearchEval.Dataset, ct);
        string learnedRoot = await RootAsync(cache, manifest, MemoryLearnedEval.Dataset, ct);

        List<Note> planted = PlantedNotes(
            File.ReadLines(Path.Combine(searchRoot, "memory.jsonl")), File.ReadLines(Path.Combine(searchRoot, "tasks.jsonl")));
        List<Note> learned = LearnedNotes(File.ReadLines(Path.Combine(paths.RepoRoot, LearnedRuns)));
        log.WriteLine($"{planted.Count} planted notes, {learned.Count} agent-written notes");

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", configName);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, log, null, ct);
        Guid searchContainer = await IndexAsync(system, MemorySearchEval.Dataset, manifest, searchRoot, ct);
        Guid learnedContainer = await IndexAsync(system, MemoryLearnedEval.Dataset, manifest, learnedRoot, ct);

        OperationContext reader = new(McpToolbox.Reader(Guid.NewGuid(), "memory-novelty-eval"), OperationSurface.Mcp);
        List<Scored> scored = [];
        foreach ((List<Note> notes, Guid container) in new[] { (planted, searchContainer), (learned, learnedContainer) })
            foreach (Note note in notes)
                scored.Add(await ScoreAsync(system.Services, reader, container, note, ct));

        Rule rule = Tune([.. scored.Where(s => s.Note.Split == "dev")]);
        List<Scored> test = [.. scored.Where(s => s.Note.Split == "test")];
        Rule result = Apply(test, rule.Score, rule.Cutoff);
        bool passed = result.RestatingHeld >= BarRestatingHeld && result.NewKept >= MinNewKept;

        JsonObject report = new()
        {
            ["config"] = configName,
            ["notes"] = new JsonObject
            {
                ["planted"] = planted.Count,
                ["learned"] = learned.Count,
                ["dev"] = scored.Count(s => s.Note.Split == "dev"),
                ["test"] = test.Count,
            },
            ["rule"] = RuleJson(rule),
            ["retiredRuleOnDev"] =new JsonObject(Candidates.Select(c => KeyValuePair.Create(c, (JsonNode?)RuleJson(Best([.. scored.Where(s => s.Note.Split == "dev")], c))))),
            ["test"] = RuleJson(result),
            ["testBySource"] = new JsonObject(test.GroupBy(s => s.Note.Source).Select(g =>
                KeyValuePair.Create(g.Key, (JsonNode?)RuleJson(Apply([.. g], rule.Score, rule.Cutoff))))),
            ["gate"] = new JsonObject
            {
                ["restatingHeldAtLeast"] = BarRestatingHeld,
                ["newKeptAtLeast"] = MinNewKept,
                ["passed"] = passed,
            },
        };
        string json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (outPath is not null)
        {
            await File.WriteAllTextAsync(outPath, json + "\n", ct);
            await File.WriteAllLinesAsync(Path.ChangeExtension(outPath, ".notes.jsonl"), scored.Select(s => NoteJson(s, rule)), ct);
        }
        log.WriteLine(json);
        return passed ? 0 : 3;
    }

    private static async Task<string> RootAsync(DatasetCache cache, EvalManifest manifest, string dataset, CancellationToken ct)
    {
        DatasetEntry entry = manifest.Datasets[dataset];
        await cache.EnsureAsync(dataset, entry, allowUnpinned: false, ct);
        return cache.DirectoryFor(dataset, entry);
    }

    /// <summary>Indexes the dataset's corpus, without late documents: what the container held when the notes were saved.</summary>
    private static async Task<Guid> IndexAsync(ConnapseSearchSystem system, string dataset, EvalManifest manifest, string root, CancellationToken ct)
    {
        List<EvalDocument> corpus = MemorySearchEval.ReadCorpus(Path.Combine(root, "corpus.jsonl"));
        IndexReport indexed = await system.IndexAsync(new EvalDataset(dataset, manifest.Datasets[dataset].Version, [], corpus, [], new Qrels()), ct);
        (Guid container, IReadOnlyDictionary<string, string> docMap) = system.ContainerOf(dataset);
        MemorySearchEval.RequireCompleteIndex(indexed, corpus.Count, docMap.Count);
        return container;
    }

    /// <summary>Hybrid with its default 10 results for the word scores, and the top Semantic hit for the cosine the save check uses.</summary>
    private static async Task<Scored> ScoreAsync(IServiceProvider services, OperationContext reader, Guid container, Note note, CancellationToken ct)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        SearchContainer search = scope.ServiceProvider.GetRequiredService<SearchContainer>();
        string query = RememberFact.CheckText(new NewMemoryFact(note.Subject, note.Attribute, note.Value, null, null, null));
        Attempt<SearchResult, SearchStatus> hybrid = await search.RunAsync(reader, container, new SearchRequest(query), ct);
        Attempt<SearchResult, SearchStatus> semantic = await search.RunAsync(reader, container, new SearchRequest(query, SearchMode.Semantic, 1, 0f), ct);
        // A container with no close passage answers NotFound for a hidden scope only; this one is visible, so any failure is real.
        if (!hybrid.Succeeded || !semantic.Succeeded)
            throw new InvalidOperationException($"search failed for note {note.Id}: {hybrid.Detail ?? semantic.Detail}");
        IReadOnlyList<SearchHit> hits = hybrid.Result!.Hits;
        double cosine = semantic.Result!.Hits is [{ } top, ..] ? top.Score : 0;
        return new Scored(note, Features(note, [.. hits.Select(h => h.Content)], cosine), hits.FirstOrDefault()?.Metadata.GetValueOrDefault("fileName"));
    }

    /// <summary>The candidate scores of a note against its passages, best first.</summary>
    public static Dictionary<string, double> Features(Note note, IReadOnlyList<string> passages, double cosine)
    {
        HashSet<string> words = MemoryCloseness.Words(RememberFact.CheckText(new NewMemoryFact(note.Subject, note.Attribute, note.Value, null, null, null)));
        HashSet<string> valueWords = MemoryCloseness.Words(note.Value);
        List<HashSet<string>> passageWords = [.. passages.Select(MemoryCloseness.Words)];
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["best-passage"] = passageWords.Select(p => MemoryCloseness.Coverage(words, p)).DefaultIfEmpty(0).Max(),
            ["top3-together"] = MemoryCloseness.Coverage(words, [.. passageWords.Take(3)]),
            ["value-best-passage"] = passageWords.Select(p => MemoryCloseness.Coverage(valueWords, p)).DefaultIfEmpty(0).Max(),
            ["semantic-cosine"] = cosine,
        };
    }

    /// <summary>The share of dev restating notes the recall rule must hold.</summary>
    public const double MinRestatingHeldDev = 0.90;

    /// <summary>The score the recall rule tunes: the cosine the save check compares.</summary>
    public const string TunedScore = "semantic-cosine";

    /// <summary>
    /// The tuning rule as amended before any agent run (eval/pass-bars.md): the highest <see cref="TunedScore"/>
    /// cutoff that holds at least <see cref="MinRestatingHeldDev"/> of dev restating notes. Held new facts are
    /// accepted, because the agent can save them again with a reason.
    /// </summary>
    public static Rule Tune(IReadOnlyList<Scored> dev)
    {
        foreach (double cutoff in dev.Select(s => s.Scores[TunedScore]).Distinct().OrderByDescending(v => v))
        {
            Rule rule = Apply(dev, TunedScore, cutoff);
            if (rule.RestatingHeld >= MinRestatingHeldDev)
                return rule;
        }
        return Apply(dev, TunedScore, double.NegativeInfinity);
    }

    /// <summary>
    /// The retired first rule, still reported per score: the cutoff holding the most restating notes while keeping at
    /// least <see cref="MinNewKept"/> of new facts. A note is held when its score is at or above the cutoff.
    /// </summary>
    public static Rule Best(IReadOnlyList<Scored> dev, string score)
    {
        // Above every value holds nothing and keeps every new fact, so a rule always exists.
        Rule best = Apply(dev, score, double.PositiveInfinity);
        foreach (double cutoff in dev.Select(s => s.Scores[score]).Distinct().OrderByDescending(v => v))
        {
            Rule rule = Apply(dev, score, cutoff);
            if (rule.NewKept >= MinNewKept && rule.RestatingHeld > best.RestatingHeld)
                best = rule;
        }
        return best;
    }

    public static Rule Apply(IReadOnlyList<Scored> notes, string score, double cutoff)
    {
        List<Scored> restating = [.. notes.Where(n => n.Note.Restates)];
        List<Scored> fresh = [.. notes.Where(n => !n.Note.Restates)];
        double held = restating.Count == 0 ? 0 : (double)restating.Count(n => n.Scores[score] >= cutoff) / restating.Count;
        double kept = fresh.Count == 0 ? 1 : (double)fresh.Count(n => n.Scores[score] < cutoff) / fresh.Count;
        return new Rule(score, cutoff, held, kept);
    }

    /// <summary>
    /// The memory-search remembers, labelled by the task that uses them: undocumented alias, current and location facts
    /// are new; every other remember restates a document (see <c>eval/pass-bars.md</c>). Corrections are outside the check.
    /// </summary>
    public static List<Note> PlantedNotes(IEnumerable<string> memoryLines, IEnumerable<string> taskLines)
    {
        Dictionary<string, (string Split, string Kind, bool? Documented)> usedBy = new(StringComparer.Ordinal);
        foreach (JsonObject task in taskLines.Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()))
            foreach (string fact in task["facts"]!.AsArray().Select(f => (string)f!))
                usedBy[fact] = ((string)task["split"]!, (string)task["kind"]!, (bool?)task["documented"]);

        List<Note> notes = [];
        foreach (JsonObject op in memoryLines.Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()))
        {
            if ((string)op["op"]! != "remember")
                continue;
            string id = (string)op["id"]!;
            (string split, bool restates) = usedBy.TryGetValue(id, out var use)
                ? (use.Split, !(use.Kind is "alias" or "current" or "location" && use.Documented == false))
                : (HashSplit(id), true);
            notes.Add(new Note(id, "planted", split, restates, (string)op["subject"]!, (string)op["attribute"]!, (string)op["value"]!));
        }
        return notes;
    }

    /// <summary>Every session-1 <c>memory_remember</c> in the CON-244 realistic run; each restates a document.</summary>
    public static List<Note> LearnedNotes(IEnumerable<string> runLines)
    {
        List<Note> notes = [];
        foreach (JsonObject run in runLines.Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()))
        {
            if ((int?)run["session"] != 1)
                continue;
            string family = (string)run["family"]!;
            int index = 0;
            foreach (JsonObject input in run["calls"]!.AsArray().Select(c => c!.AsObject())
                .Where(c => (string?)c["tool"] == "memory_remember").Select(c => c["input"]!.AsObject()))
            {
                notes.Add(new Note($"{run["task"]}-r{run["rep"]}-{index++}", "learned", HashSplit(family), true,
                    (string?)input["subject"] ?? "", (string?)input["attribute"] ?? "", (string?)input["value"] ?? ""));
            }
        }
        return notes;
    }

    /// <summary>dev when the first byte of SHA-256 of <paramref name="key"/> is even, else test.</summary>
    public static string HashSplit(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key))[0] % 2 == 0 ? "dev" : "test";

    private static JsonObject RuleJson(Rule rule) => new()
    {
        ["score"] = rule.Score,
        ["cutoff"] = double.IsInfinity(rule.Cutoff) ? null : Math.Round(rule.Cutoff, 6),
        ["restatingHeld"] = Math.Round(rule.RestatingHeld, 4),
        ["newKept"] = Math.Round(rule.NewKept, 4),
    };

    private static string NoteJson(Scored s, Rule rule) => new JsonObject
    {
        ["id"] = s.Note.Id,
        ["source"] = s.Note.Source,
        ["split"] = s.Note.Split,
        ["restates"] = s.Note.Restates,
        ["held"] = s.Scores[rule.Score] >= rule.Cutoff,
        ["note"] = RememberFact.CheckText(new NewMemoryFact(s.Note.Subject, s.Note.Attribute, s.Note.Value, null, null, null)),
        ["topFile"] = s.TopFile,
        ["scores"] = new JsonObject(s.Scores.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)Math.Round(kv.Value, 6)))),
    }.ToJsonString();
}
