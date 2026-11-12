using System.Text.Json.Nodes;
using Connapse.Eval.Statistics;

namespace Connapse.Eval.Runs.MemorySearch;

/// <summary>
/// Compares two memory-search arms task by task from their run logs (<c>--out</c>'s <c>.runs.jsonl</c>): each task's
/// mean score over its repetitions, paired across the arms. The candidate passes when the lower end of the 90% BCa
/// interval of (candidate − baseline) is above −margin, the non-inferiority rule CON-344 registered in
/// <c>eval/pass-bars.md</c>.
/// </summary>
public static class MemorySearchComparison
{
    public const double DefaultMargin = 0.05;

    /// <summary>Each task's repetition scores, from a run log's lines.</summary>
    public static SortedDictionary<string, List<double>> TaskScores(IEnumerable<string> runLines)
    {
        SortedDictionary<string, List<double>> scores = new(StringComparer.Ordinal);
        foreach (string line in runLines.Where(l => l.Length > 0))
        {
            JsonObject run = JsonNode.Parse(line)!.AsObject();
            string task = (string?)run["task"] ?? throw new ArgumentException("A run line has no task.");
            double score = (double?)run["score"] ?? throw new ArgumentException($"A run of {task} has no score.");
            if (!scores.TryGetValue(task, out List<double>? list))
                scores[task] = list = [];
            list.Add(score);
        }
        return scores;
    }

    public static JsonObject Compare(SortedDictionary<string, List<double>> baseline, SortedDictionary<string, List<double>> candidate,
        double margin = DefaultMargin)
    {
        if (!baseline.Keys.SequenceEqual(candidate.Keys, StringComparer.Ordinal))
            throw new ArgumentException("The two arms must cover the same tasks: "
                + $"{baseline.Keys.Except(candidate.Keys).Count()} only in the baseline, {candidate.Keys.Except(baseline.Keys).Count()} only in the candidate.");
        List<string> uneven = [.. baseline.Keys.Where(t => baseline[t].Count != candidate[t].Count)];
        if (uneven.Count > 0)
            throw new ArgumentException($"The two arms ran {uneven.Count} task(s) a different number of times, first {uneven[0]}.");

        double[] b = [.. baseline.Values.Select(s => s.Average())];
        double[] c = [.. candidate.Values.Select(s => s.Average())];
        PairedComparison at90 = PairedStats.Compare(b, c, confidenceLevel: 0.90);
        PairedComparison at95 = PairedStats.Compare(b, c);
        bool pass = at90.CiLow > -margin;

        return new JsonObject
        {
            ["tasks"] = b.Length,
            ["runs"] = baseline.Values.Sum(s => s.Count),
            ["baselineScore"] = Round(b.Average()),
            ["candidateScore"] = Round(c.Average()),
            ["meanDifference"] = Round(at90.MeanDifference),
            ["ci90"] = new JsonArray(Round(at90.CiLow), Round(at90.CiHigh)),
            ["ci95"] = new JsonArray(Round(at95.CiLow), Round(at95.CiHigh)),
            ["tTestP"] = Round(at90.TTestP),
            ["permutationP"] = Round(at90.PermutationP),
            ["tasksBetter"] = b.Zip(c).Count(p => p.Second > p.First),
            ["tasksWorse"] = b.Zip(c).Count(p => p.Second < p.First),
            ["margin"] = margin,
            ["verdict"] = pass ? "PASS" : "FAIL",
            ["rule"] = $"PASS when the 90% BCa lower bound of (candidate - baseline) is above -{margin}",
        };
    }

    private static double Round(double x) => Math.Round(x, 4);
}
