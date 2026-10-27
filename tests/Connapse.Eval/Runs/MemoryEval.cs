using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using Connapse.Eval.Statistics;
using Connapse.Eval.Systems;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Eval.Runs;

/// <summary>
/// The memory-v1 gate (CON-111): the CON-38 memory questions answered two ways on one Connapse instance.
/// <list type="bullet">
/// <item><b>Notes</b>: each project's session notes stored as plain documents in the project's container and found
/// with search under the config (the baseline, the app's defaults).</item>
/// <item><b>Memory</b>: the writer agents' operations (<c>memory-ops/&lt;CODE&gt;.jsonl</c>) replayed through
/// <see cref="IMemoryService"/> into the same container, each fact citing its session's note document, then read
/// back with the call an agent would make for the question's kind.</item>
/// </list>
/// Both are scored the same way: Recall@5 (an evidence note in the top five, through a fact's sources for memory;
/// for a window, every change's own) and, where <c>baseline.py</c> reads one, the answer from the first result that
/// names a value of the asked fact.
/// </summary>
public sealed partial class MemoryEval(RepoPaths paths, HttpClient http, TextWriter log)
{
    private const int K = 5;

    /// <summary>The manifest dataset whose pinned files this reads; memory-v1 is its only suite.</summary>
    public const string Dataset = "memory-questions";

    /// <summary>The kinds the bar calls current-value, as-of-date and what-changed, and the ordinary recall kinds.</summary>
    public static readonly string[] ChangedFactKinds = ["current", "as_of", "changed"];
    public static readonly string[] OrdinaryKinds = ["ticket", "when", "ticket_effect"];

    private static readonly Dictionary<string, string> FactOf = new()
    {
        ["database"] = "database",
        ["deploy region"] = "region",
        ["tech lead"] = "lead",
        ["CI system"] = "ci",
        ["cache"] = "cache",
        ["public API rate limit"] = "rate_limit",
        ["alert channel"] = "alert",
        ["current release"] = "release",
    };

    public sealed record Question(string Id, string Code, string Split, string Kind, string Text, JsonObject Answer, string[] Evidence);

    public sealed record Note(string Id, string Code, string Date, string Text);

    public sealed record Op(string Session, string Verb, string Id, string? Target, string? Kind, string? Subject,
        string? Attribute, string Value, string? ValidFrom, string? Reason);

    /// <summary>One side's result for one question: Recall@5 and, when scored, the answer.</summary>
    public sealed record Score(double Recall, double? Answer);

    public async Task<int> RunAsync(string configName, string split, string? outPath, CancellationToken ct)
    {
        // Copied from eval/datasets and checked against the manifest's SHA-256s, so a run scores the pinned files.
        DatasetEntry entry = EvalManifest.Load(paths.ManifestPath).Datasets[Dataset];
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        await cache.EnsureAsync(Dataset, entry, allowUnpinned: false, ct);
        string root = cache.DirectoryFor(Dataset, entry);
        List<Question> questions = [.. ReadQuestions(Path.Combine(root, "questions.jsonl")).Where(q => q.Split == split)];
        List<Note> notes = [.. File.ReadLines(Path.Combine(root, "notes.jsonl")).Select(l => JsonNode.Parse(l)!.AsObject())
            .Select(n => new Note((string)n["note"]!, (string)n["code"]!, (string)n["date"]!, (string)n["text"]!))];
        JsonObject timeline = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "timeline.json")))!.AsObject();
        string[] codes = [.. questions.Select(q => q.Code).Distinct().Order(StringComparer.Ordinal)];
        Dictionary<string, List<Op>> ops = codes.ToDictionary(c => c, c => ReadOps(Path.Combine(root, $"ops-{c}.jsonl")));

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", configName);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, log, null, ct);

        Dictionary<string, (Score Notes, Score Memory)> scored = new(StringComparer.Ordinal);
        List<double> writeMs = [], recallMs = [];
        var caller = new MemoryCaller(Guid.NewGuid(), null, "memory-eval");
        foreach (string code in codes)
        {
            string dataset = $"memory-{code.ToLowerInvariant()}";
            List<Note> own = [.. notes.Where(n => n.Code == code)];
            await system.IndexAsync(new EvalDataset(dataset, "con-38", [], [.. own.Select(n =>
                new EvalDocument(n.Id, DocumentKind.Text, n.Id, n.Text, null, new Dictionary<string, string>()))], [], new Qrels()), ct);
            (Guid container, IReadOnlyDictionary<string, string> docMap) = system.ContainerOf(dataset);
            Dictionary<string, string> documentOf = docMap.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
            Dictionary<string, Note> noteOf = own.ToDictionary(n => n.Id, StringComparer.Ordinal);

            await using AsyncServiceScope scope = system.Services.CreateAsyncScope();
            IMemoryService memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
            Dictionary<string, Guid> factOf = new(StringComparer.Ordinal);
            foreach (Op op in ops[code])
            {
                var fact = new NewMemoryFact(op.Subject ?? "", op.Attribute ?? "", op.Value, Date(op.ValidFrom), null,
                    [new MemorySource(MemorySourceKind.Document, documentOf[op.Session])], op.Reason);
                var timer = Stopwatch.StartNew();
                MemoryWriteResult result = op.Verb == "remember"
                    ? await memory.RememberAsync(container, fact, caller, ct)
                    : await memory.CorrectAsync(container, factOf[op.Target!],
                        op.Kind == "wrong" ? MemoryCorrection.Wrong : MemoryCorrection.Changed, fact, caller, ct);
                writeMs.Add(timer.Elapsed.TotalMilliseconds);
                if (result.Fact is null)
                    throw new InvalidOperationException($"{op.Id} was refused: {result.Error}");
                factOf[op.Id] = result.Fact.Id;
            }
            log.WriteLine($"{code}: {own.Count} notes indexed, {ops[code].Count} memory operations replayed");

            foreach (Question q in questions.Where(q => q.Code == code))
            {
                if (q.Evidence is ["(none)"])
                    continue; // a window with no changes has no evidence to find, as in baseline.py
                string[] asked = AskedValues(q, timeline);

                // Every note ranked, as baseline.py ranks them: its answer is read from the first one naming a value,
                // however far down; Recall@5 takes the first five.
                SearchOutcome outcome = await system.SearchAsync(dataset, new EvalQuery(q.Id, q.Text, Split.Test, []), own.Count, ct);
                if (outcome.Error is not null)
                    throw new InvalidOperationException($"{q.Id}: search failed: {outcome.Error}");
                List<Note> ranked = [.. outcome.Ranked.Select(r => noteOf[r.DocId])];
                Score notesScore = new(Recall(q, ranked.Take(K).Select(n => n.Id).ToHashSet(StringComparer.Ordinal)), AnswerFromNotes(q, ranked, asked));

                var timer = Stopwatch.StartNew();
                MemoryRecall recall = await memory.RecallAsync(QueryFor(q, container), caller, ct);
                recallMs.Add(timer.Elapsed.TotalMilliseconds);
                if (recall.Denied)
                    throw new InvalidOperationException($"{q.Id}: recall was denied");
                List<MemoryFact> facts = [.. recall.Facts.Select(f => f.Fact)];
                if (q.Kind == "changed")
                    facts = [.. facts.Where(f => f.SupersededAt is null && f.ValidFrom is { } from && InWindow(q, from)).OrderBy(f => f.ValidFrom)];
                HashSet<string> cited = [.. facts.Take(K).SelectMany(f => f.Sources).Select(s => docMap.GetValueOrDefault(s.Ref) ?? s.Ref)];
                Score memoryScore = new(Recall(q, cited), AnswerFromMemory(q, facts, asked));
                scored[q.Id] = (notesScore, memoryScore);
            }
        }

        JsonObject report = Report(configName, split, questions, scored, writeMs, recallMs);
        string json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (outPath is not null)
            await File.WriteAllTextAsync(outPath, json + "\n", ct);
        log.WriteLine(json);
        return (bool)report["gate"]!["passed"]! ? 0 : 3;
    }

    /// <summary>The call an agent makes for each kind: now, as of the asked date, a window, or the whole history.</summary>
    public static MemoryQuery QueryFor(Question q, Guid container) => q.Kind switch
    {
        "current" => new MemoryQuery(container, q.Text, Limit: K),
        "as_of" => new MemoryQuery(container, q.Text, AsOf: Dates(q.Text)[0], Limit: K),
        "changed" => new MemoryQuery(container, ChangedFrom: Dates(q.Text)[0], ChangedTo: Dates(q.Text)[1], Limit: 100),
        _ => new MemoryQuery(container, q.Text, History: true, Limit: K),
    };

    private static bool InWindow(Question q, DateTimeOffset date)
    {
        DateTimeOffset[] window = Dates(q.Text);
        return date >= window[0] && date <= window[1];
    }

    /// <summary>
    /// Recall@5 over <paramref name="top"/>: the top five notes, or the notes the top five facts cite. For a window
    /// every change needs one of its own evidence notes there; otherwise any evidence note does.
    /// </summary>
    public static double Recall(Question q, IReadOnlySet<string> top)
    {
        if (q.Kind == "changed")
            return q.Answer["changes"]!.AsArray().All(c => c!["evidence"]!.AsArray().Any(e => top.Contains((string)e!))) ? 1 : 0;
        return q.Evidence.Any(top.Contains) ? 1 : 0;
    }

    /// <summary>Every value the asked fact ever had in this project, for reading an answer as baseline.py does.</summary>
    private static string[] AskedValues(Question q, JsonObject timeline)
    {
        string? noun = FactOf.Keys.FirstOrDefault(k => q.Text.Contains($"'s {k}", StringComparison.Ordinal));
        return noun is null ? [] : [.. timeline[q.Code]![FactOf[noun]]!.AsArray().Select(s => (string)s!["value"]!).Distinct()];
    }

    private static string Target(Question q) =>
        (string?)q.Answer["value"] ?? q.Text[(q.Text.LastIndexOf(" to ", StringComparison.Ordinal) + 4)..].TrimEnd('?');

    /// <summary>baseline.py's answer, read from the first ranked note that names a value of the asked fact.</summary>
    public static double? AnswerFromNotes(Question q, IReadOnlyList<Note> ranked, string[] values) =>
        Answer(q, values, ranked.Select(n => (Text: n.Text, Date: n.Date, Ticket: Ticket().Match(n.Text) is { Success: true } m ? m.Value : null)));

    /// <summary>The same reading of facts: the first fact whose value names a value of the asked fact.</summary>
    public static double? AnswerFromMemory(Question q, IReadOnlyList<MemoryFact> facts, string[] values) =>
        Answer(q, values, facts.Select(f => (Text: f.Value, Date: f.ValidFrom?.ToString("yyyy-MM-dd") ?? "",
            Ticket: Ticket().Match(f.Reason ?? "") is { Success: true } m ? m.Value : null)));

    private static double? Answer(Question q, string[] values, IEnumerable<(string Text, string Date, string? Ticket)> ranked)
    {
        if (values.Length == 0 || q.Kind is not ("current" or "as_of" or "when" or "ticket"))
            return null;
        if (q.Kind is "current" or "as_of")
        {
            (string Text, string Date, string? Ticket)? first = ranked.Cast<(string, string, string?)?>()
                .FirstOrDefault(r => values.Any(v => Named(r!.Value.Item1, v) >= 0));
            if (first is null)
                return 0;
            string said = values.MaxBy(v => Named(first.Value.Item1, v))!;
            return said == (string)q.Answer["value"]! ? 1 : 0;
        }
        string target = Target(q);
        (string Text, string Date, string? Ticket)? hit = ranked.Cast<(string, string, string?)?>()
            .FirstOrDefault(r => Named(r!.Value.Item1, target) >= 0);
        if (hit is null)
            return 0;
        return q.Kind == "when"
            ? (hit.Value.Date == (string)q.Answer["date"]! ? 1 : 0)
            : (hit.Value.Ticket == (string)q.Answer["ticket"]! ? 1 : 0);
    }

    /// <summary>Where <paramref name="text"/> last names <paramref name="value"/> as a whole token, or -1 (baseline.py's <c>named</c>).</summary>
    public static int Named(string text, string value)
    {
        Match? last = null;
        foreach (Match m in Regex.Matches(text, $@"(?<![\w.-]){Regex.Escape(value)}(?![\w-])"))
            last = m;
        return last is null ? -1 : last.Index + last.Length;
    }

    private static JsonObject Report(string config, string split, List<Question> questions,
        Dictionary<string, (Score Notes, Score Memory)> scored, List<double> writeMs, List<double> recallMs)
    {
        JsonObject kinds = [];
        foreach (string kind in ChangedFactKinds.Concat(OrdinaryKinds))
            kinds[kind] = Group(questions.Where(q => q.Kind == kind), scored);

        PairedComparison changed = Compare(questions.Where(q => ChangedFactKinds.Contains(q.Kind)), scored);
        PairedComparison ordinary = Compare(questions.Where(q => OrdinaryKinds.Contains(q.Kind)), scored);
        // Reported beside the bar, not part of it: the bar is Recall@5 (eval/pass-bars.md).
        List<(Score Notes, Score Memory)> answered = [.. questions.Where(q => scored.ContainsKey(q.Id)).Select(q => scored[q.Id])
            .Where(r => r.Notes.Answer is not null)];
        PairedComparison answers = PairedStats.Compare([.. answered.Select(r => r.Notes.Answer!.Value)], [.. answered.Select(r => r.Memory.Answer!.Value)]);
        double[] holm = Holm.Adjust([changed.PermutationP, ordinary.PermutationP]);
        bool better = changed.MeanDifference >= 0.10 && holm[0] < 0.05;
        bool noDrop = !(ordinary.MeanDifference < 0 && holm[1] < 0.05);
        double writeMax = writeMs.Max();
        double recallP95 = Percentile(recallMs, 0.95);

        return new JsonObject
        {
            ["config"] = config,
            ["split"] = split,
            ["kinds"] = kinds,
            ["changedFact"] = Comparison(changed, holm[0]),
            ["ordinary"] = Comparison(ordinary, holm[1]),
            ["answers"] = Comparison(answers, answers.PermutationP),
            ["writes"] = new JsonObject { ["count"] = writeMs.Count, ["medianMs"] = Round(Percentile(writeMs, 0.5)), ["maxMs"] = Round(writeMax) },
            ["recall"] = new JsonObject { ["count"] = recallMs.Count, ["p95Ms"] = Round(recallP95) },
            ["gate"] = new JsonObject
            {
                ["changedFactPlus010"] = better,
                ["ordinaryNoSignificantDrop"] = noDrop,
                ["writesUnder200Ms"] = writeMax < 200,
                ["passed"] = better && noDrop && writeMax < 200,
            },
        };
    }

    private static JsonObject Group(IEnumerable<Question> group, Dictionary<string, (Score Notes, Score Memory)> scored)
    {
        List<(Score Notes, Score Memory)> rows = [.. group.Where(q => scored.ContainsKey(q.Id)).Select(q => scored[q.Id])];
        List<(Score Notes, Score Memory)> answered = [.. rows.Where(r => r.Notes.Answer is not null)];
        return new JsonObject
        {
            ["questions"] = rows.Count,
            ["notesRecall@5"] = Round(rows.Average(r => r.Notes.Recall)),
            ["memoryRecall@5"] = Round(rows.Average(r => r.Memory.Recall)),
            ["notesAnswer"] = answered.Count == 0 ? null : Round(answered.Average(r => r.Notes.Answer!.Value)),
            ["memoryAnswer"] = answered.Count == 0 ? null : Round(answered.Average(r => r.Memory.Answer!.Value)),
        };
    }

    private static PairedComparison Compare(IEnumerable<Question> group, Dictionary<string, (Score Notes, Score Memory)> scored)
    {
        List<(Score Notes, Score Memory)> rows = [.. group.Where(q => scored.ContainsKey(q.Id)).Select(q => scored[q.Id])];
        return PairedStats.Compare([.. rows.Select(r => r.Notes.Recall)], [.. rows.Select(r => r.Memory.Recall)]);
    }

    private static JsonObject Comparison(PairedComparison c, double holmP) => new()
    {
        ["questions"] = c.N,
        ["meanDifference"] = Round(c.MeanDifference),
        ["ci95"] = new JsonArray(Round(c.CiLow), Round(c.CiHigh)),
        ["permutationP"] = c.PermutationP,
        ["holmP"] = holmP,
        ["minimumDetectableEffect"] = Round(c.MinimumDetectableEffect),
    };

    private static double Percentile(List<double> xs, double p)
    {
        List<double> sorted = [.. xs.Order()];
        return sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * p) - 1)];
    }

    private static double Round(double x) => Math.Round(x, 4);

    public static DateTimeOffset[] Dates(string text) =>
        [.. IsoDate().Matches(text).Select(m => DateTimeOffset.ParseExact(m.Value, "yyyy-MM-dd", null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal))];

    private static DateTimeOffset? Date(string? text) => text is null ? null : Dates(text)[0];

    private static IEnumerable<Question> ReadQuestions(string path) =>
        File.ReadLines(path).Select(l => JsonNode.Parse(l)!.AsObject()).Select(q => new Question(
            (string)q["id"]!, (string)q["code"]!, (string)q["split"]!, (string)q["kind"]!, (string)q["question"]!,
            q["answer"]!.AsObject(), [.. q["evidence"]!.AsArray().Select(e => (string)e!)]));

    private static List<Op> ReadOps(string path) =>
        File.Exists(path)
            ? [.. File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).Select(o => new Op(
                (string)o["session"]!, (string)o["op"]!, (string)o["id"]!, (string?)o["target"], (string?)o["kind"],
                (string?)o["subject"], (string?)o["attribute"], (string)o["value"]!, (string?)o["validFrom"], (string?)o["reason"]))]
            : throw new FileNotFoundException($"No memory operations for this project: {path}");

    [GeneratedRegex(@"\b[A-Z]{3}-\d+\b")]
    private static partial Regex Ticket();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}")]
    private static partial Regex IsoDate();
}
