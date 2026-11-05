using System.Text.RegularExpressions;
using Connapse.Eval.Statistics;

namespace Connapse.Eval.Runs.MemorySearch;

/// <summary>One memory-search task (<c>tasks.jsonl</c>).</summary>
public sealed record SearchTask(
    string Id, string Kind, string Split, bool? Documented, string Question,
    IReadOnlyList<string> Gold, IReadOnlyList<string> Stale, string Answer, string? StaleAnswer)
{
    /// <summary>Set M, where memory should help; the rest (control and stale) are set H.</summary>
    public bool InMemorySet => Kind is "current" or "alias" or "correction" or "location";
}

/// <summary>What one run earned: the gated score and the reported measures.</summary>
public sealed record RunScore(
    double Score, bool CitedStale, bool AnswerRight, bool SawGold, double FirstQueryMrr,
    int Searches, int ToolCalls, int MemoryCalls, long InputTokens, long OutputTokens, long CacheReadTokens);

public enum Verdict { Keep, Worse, NoBetter }

/// <summary>
/// Turns what an agent cited and saw into dataset ids, and scores runs and arms by the CON-244 bar in
/// <c>eval/pass-bars.md</c>.
/// </summary>
public sealed partial class DocumentIndex
{
    private readonly Dictionary<Guid, string> _byDocument = [];
    private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="docMap">Connapse document id → dataset id, as the search system indexed them.</param>
    /// <param name="fileNames">Dataset id → the file name it was uploaded under.</param>
    public DocumentIndex(IReadOnlyDictionary<string, string> docMap, IReadOnlyDictionary<string, string> fileNames)
    {
        foreach ((string document, string id) in docMap)
            if (Guid.TryParse(document, out Guid g))
                _byDocument[g] = id;
        foreach ((string id, string fileName) in fileNames)
        {
            _byName[fileName] = id;
            _byName[Path.GetFileNameWithoutExtension(fileName)] = id;
        }
    }

    /// <summary>The dataset id a citation names: a document UUID, or a file name or root path; null when none.</summary>
    public string? Resolve(string cited)
    {
        string text = cited.Trim();
        if (Guid.TryParse(text, out Guid g))
            return _byDocument.GetValueOrDefault(g);
        return _byName.GetValueOrDefault(text.TrimStart('/'));
    }

    /// <summary>Every indexed document a tool's output names by UUID.</summary>
    public IEnumerable<string> Seen(string output) =>
        Uuid().Matches(output).Select(m => _byDocument.GetValueOrDefault(Guid.Parse(m.Value))).OfType<string>();

    /// <summary>The documents a <c>search_knowledge</c> output ranks, best first, each once.</summary>
    public List<string> Ranked(string searchOutput) =>
        [.. RankedDocument().Matches(searchOutput).Select(m => _byDocument.GetValueOrDefault(Guid.Parse(m.Groups[1].Value)))
            .OfType<string>().Distinct()];

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Uuid();

    [GeneratedRegex(@"^DocumentId: ([0-9a-fA-F-]{36})\s*$", RegexOptions.Multiline)]
    private static partial Regex RankedDocument();
}

public static class MemorySearchScoring
{
    public const string Search = "search_knowledge";
    public const string Recall = "memory_recall";

    public static RunScore Score(SearchTask task, AgentRun run, DocumentIndex index)
    {
        HashSet<string> cited = [.. run.Cited.Select(index.Resolve).OfType<string>()];
        bool stale = task.Stale.Any(cited.Contains);
        bool gold = task.Gold.Any(cited.Contains);
        double score = run.Outcome == AgentOutcome.Answered && gold && !stale ? 1 : 0;

        bool answerRight = run.Outcome == AgentOutcome.Answered && run.Answer is { } answer
            && MemoryEval.Named(answer, task.Answer) >= 0
            && (task.StaleAnswer is null || MemoryEval.Named(answer, task.StaleAnswer) < 0);
        bool sawGold = run.Calls.SelectMany(c => index.Seen(c.Output)).Any(task.Gold.Contains);
        ToolTrace? firstSearch = run.Calls.FirstOrDefault(c => c.Name == Search);
        double mrr = firstSearch is null ? 0 : ReciprocalRank(index.Ranked(firstSearch.Output), task.Gold, 10);

        return new RunScore(score, stale, answerRight, sawGold, mrr,
            run.Calls.Count(c => c.Name == Search), run.Calls.Count, run.Calls.Count(c => c.Name == Recall),
            run.Usage.Input, run.Usage.Output, run.Usage.CacheRead);
    }

    public static double ReciprocalRank(IReadOnlyList<string> ranked, IReadOnlyList<string> gold, int k)
    {
        for (int i = 0; i < Math.Min(k, ranked.Count); i++)
            if (gold.Contains(ranked[i]))
                return 1.0 / (i + 1);
        return 0;
    }

    /// <summary>
    /// The bar's verdict from the paired On − Off task scores: <paramref name="m"/> at 95%,
    /// <paramref name="h90"/> (set H at 90%) and <paramref name="all"/> (every task at 95%).
    /// </summary>
    public static Verdict Decide(PairedComparison m, PairedComparison h90, PairedComparison all)
    {
        bool mWins = m.MeanDifference >= 0.15 && m.CiLow > 0 && m.PermutationP < 0.05;
        bool hHolds = h90.CiLow > -0.10;
        if (mWins && hHolds)
            return Verdict.Keep;
        bool worse = all.CiHigh < 0 || (h90.MeanDifference < 0 && h90.CiLow <= -0.10);
        return worse ? Verdict.Worse : Verdict.NoBetter;
    }

    public static string Label(Verdict verdict) => verdict switch
    {
        Verdict.Keep => "KEEP",
        Verdict.Worse => "WORSE (DROP)",
        _ => "NO BETTER (DROP)",
    };
}
