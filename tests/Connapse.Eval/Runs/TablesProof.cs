using System.Text.Json;
using System.Text.Json.Nodes;

namespace Connapse.Eval.Runs;

/// <summary>One graded run from a <c>*.runs.jsonl</c> file that <see cref="TablesRagEval"/> writes.</summary>
public sealed record GradedRun(string Id, string Family, bool Correct, double CostUsd)
{
    public bool Mixed => Id.StartsWith("mixed-", StringComparison.Ordinal);

    public static GradedRun Parse(string line)
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement r = doc.RootElement;
        return new GradedRun(r.GetProperty("id").GetString()!, r.GetProperty("family").GetString()!,
            r.GetProperty("correct").GetBoolean(), r.TryGetProperty("costUsd", out JsonElement c) ? c.GetDouble() : 0);
    }

    public static List<GradedRun> ReadAll(string path) => [.. File.ReadLines(path).Where(l => l.Length > 0).Select(Parse)];
}

/// <summary>
/// CON-307: the tables run against the rag baseline, scored on the parts of the tables pass bar that come from the agent
/// runs (bars 1–4 and the cost half of bar 7, in <c>eval/pass-bars.md</c>). The bar's numbers are fixed here as they are
/// written there.
/// </summary>
public static class TablesProof
{
    public const double MinGainPoints = 25;
    public const double MaxMcNemarP = 0.01;
    public const double MinAccuracy = 0.70;
    public const double MaxCostRatio = 1.5;

    /// <summary>
    /// The exact McNemar test, two-sided: twice the binomial(n, 1/2) CDF at the smaller discordant count, capped at 1.
    /// Ported from statsmodels' <c>mcnemar(table, exact=True)</c>
    /// (statsmodels/stats/contingency_tables.py, BSD-3-Clause): <c>statistic = min(n1, n2)</c>,
    /// <c>pvalue = binom.cdf(statistic, n1 + n2, 0.5) * 2</c>, <c>pvalue = min(pvalue, 1)</c>.
    /// </summary>
    public static double McNemarExactP(int onlyFirstRight, int onlySecondRight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(onlyFirstRight);
        ArgumentOutOfRangeException.ThrowIfNegative(onlySecondRight);
        int n = onlyFirstRight + onlySecondRight;
        int statistic = Math.Min(onlyFirstRight, onlySecondRight);
        return Math.Min(BinomialHalfCdf(statistic, n) * 2, 1);
    }

    /// <summary>P(X ≤ k) for X ~ binomial(n, 1/2), summed in log space so large n can't overflow.</summary>
    public static double BinomialHalfCdf(int k, int n)
    {
        if (n == 0)
            return 1;
        double logHalfN = n * Math.Log(0.5);
        double sum = 0;
        double logChoose = 0;
        for (int i = 0; i <= k; i++)
        {
            if (i > 0)
                logChoose += Math.Log(n - i + 1) - Math.Log(i);
            sum += Math.Exp(logChoose + logHalfN);
        }
        return Math.Min(sum, 1);
    }

    /// <summary>
    /// Scores <paramref name="tables"/> against <paramref name="rag"/>. Both must hold the same questions, test and mixed,
    /// so every test pair is the same question asked of both setups.
    /// </summary>
    public static JsonObject Compare(IReadOnlyList<GradedRun> rag, IReadOnlyList<GradedRun> tables)
    {
        Dictionary<string, GradedRun> ragById = rag.ToDictionary(r => r.Id, StringComparer.Ordinal);
        Dictionary<string, GradedRun> tablesById = tables.ToDictionary(r => r.Id, StringComparer.Ordinal);
        if (!ragById.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(tablesById.Keys))
            throw new InvalidDataException("The rag and tables runs don't hold the same questions, so they can't be paired.");

        List<(GradedRun Rag, GradedRun Tables)> test = [.. rag.Where(r => !r.Mixed).Select(r => (r, tablesById[r.Id]))];
        List<(GradedRun Rag, GradedRun Tables)> mixed = [.. rag.Where(r => r.Mixed).Select(r => (r, tablesById[r.Id]))];
        if (test.Count == 0)
            throw new InvalidDataException("The runs hold no test questions.");

        int ragRight = test.Count(p => p.Rag.Correct);
        int tablesRight = test.Count(p => p.Tables.Correct);
        int onlyRag = test.Count(p => p.Rag.Correct && !p.Tables.Correct);
        int onlyTables = test.Count(p => !p.Rag.Correct && p.Tables.Correct);
        double gain = Math.Round(100.0 * (tablesRight - ragRight) / test.Count, 2);
        double p = McNemarExactP(onlyRag, onlyTables);
        double accuracy = (double)tablesRight / test.Count;

        JsonObject families = [];
        bool everyFamily = true;
        foreach (string family in test.Select(t => t.Rag.Family).Distinct().Order(StringComparer.Ordinal))
        {
            int r = test.Count(t => t.Rag.Family == family && t.Rag.Correct);
            int t = test.Count(x => x.Rag.Family == family && x.Tables.Correct);
            everyFamily &= t >= r;
            families[family] = new JsonObject
            {
                ["questions"] = test.Count(x => x.Rag.Family == family),
                ["rag"] = r,
                ["tables"] = t,
                ["pass"] = t >= r,
            };
        }

        int mixedRag = mixed.Count(m => m.Rag.Correct);
        int mixedBoth = mixed.Count(m => m.Tables.Correct);
        double ragCost = test.Average(t => t.Rag.CostUsd);
        double tablesCost = test.Average(t => t.Tables.CostUsd);
        double? costRatio = ragCost > 0 ? Math.Round(tablesCost / ragCost, 3) : null;

        return new JsonObject
        {
            ["questions"] = test.Count,
            ["bar1Accuracy"] = new JsonObject
            {
                ["rag"] = ragRight,
                ["tables"] = tablesRight,
                ["gainPoints"] = gain,
                ["onlyRagRight"] = onlyRag,
                ["onlyTablesRight"] = onlyTables,
                ["mcnemarP"] = p,
                ["pass"] = gain >= MinGainPoints && p < MaxMcNemarP,
            },
            ["bar2AbsoluteAccuracy"] = new JsonObject
            {
                ["accuracy"] = Math.Round(accuracy, 4),
                ["pass"] = accuracy >= MinAccuracy,
            },
            ["bar3EveryFamily"] = new JsonObject { ["families"] = families, ["pass"] = everyFamily },
            ["bar4Mixed"] = new JsonObject
            {
                ["questions"] = mixed.Count,
                ["rag"] = mixedRag,
                ["both"] = mixedBoth,
                ["pass"] = mixed.Count > 0 && mixedBoth >= mixedRag,
            },
            ["bar7AgentCost"] = new JsonObject
            {
                ["ragPerQuestionUsd"] = Math.Round(ragCost, 4),
                ["tablesPerQuestionUsd"] = Math.Round(tablesCost, 4),
                ["ratio"] = costRatio,
                ["pass"] = costRatio is double ratio && ratio <= MaxCostRatio,
            },
        };
    }
}
