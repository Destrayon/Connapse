using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Eval.Statistics;

namespace Connapse.Eval.Runs;

/// <summary>
/// Compares the full and compact tool lists on one suite (CON-346, <c>eval/pass-bars.md</c>): pairs the two reports'
/// per-task scores and checks non-inferiority (the 90% BCa lower bound of compact − full above −0.05), and gives the
/// ratio of median input tokens per task, which bar 2 holds to 80% or less with tool search off.
/// </summary>
public static class ToolArms
{
    public const double Margin = -0.05;
    public const double ConfidenceLevel = 0.90;
    public const double TokenBar = 0.80;

    public static int Run(string fullPath, string compactPath, TextWriter output)
    {
        JsonObject full = JsonNode.Parse(File.ReadAllText(fullPath))!.AsObject();
        JsonObject compact = JsonNode.Parse(File.ReadAllText(compactPath))!.AsObject();
        output.WriteLine(Compare(full, compact).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    public static JsonObject Compare(JsonObject full, JsonObject compact)
    {
        if ((string?)full["toolList"] != "full" || (string?)compact["toolList"] != "compact")
            throw new ArgumentException("--full must be a full-list report and --compact a compact-list report");
        bool? fullSearch = (bool?)full["toolSearch"];
        if (fullSearch != (bool?)compact["toolSearch"])
            throw new ArgumentException("the two reports ran with different --tool-search settings");

        Dictionary<string, double> f = Scores(full);
        Dictionary<string, double> c = Scores(compact);
        if (!f.Keys.ToHashSet().SetEquals(c.Keys))
            throw new ArgumentException("the two reports scored different tasks, so they cannot be paired");
        List<string> ids = [.. f.Keys.Order(StringComparer.Ordinal)];
        PairedComparison p = PairedStats.Compare([.. ids.Select(i => f[i])], [.. ids.Select(i => c[i])], confidenceLevel: ConfidenceLevel);

        double? fullTokens = MedianTokens(full);
        double? compactTokens = MedianTokens(compact);
        double? ratio = fullTokens is > 0 && compactTokens is not null ? compactTokens / fullTokens : null;
        JsonArray diagnostic = [];
        foreach (JsonObject report in new[] { full, compact })
            if ((bool?)report["conforming"] != true)
                diagnostic.Add((JsonNode)$"the {(string?)report["toolList"]} report is diagnostic only");

        return new JsonObject
        {
            ["toolSearch"] = fullSearch,
            ["tasks"] = p.N,
            ["fullMean"] = Math.Round(f.Values.Average(), 4),
            ["compactMean"] = Math.Round(c.Values.Average(), 4),
            ["difference"] = Math.Round(p.MeanDifference, 4),
            ["ci90Low"] = Math.Round(p.CiLow, 4),
            ["ci90High"] = Math.Round(p.CiHigh, 4),
            ["nonInferior"] = p.CiLow > Margin,
            ["medianInputTokens"] = new JsonObject { ["full"] = fullTokens, ["compact"] = compactTokens },
            ["tokenRatio"] = ratio is null ? null : Math.Round(ratio.Value, 4),
            // Bar 2 is judged with tool search off only; with it on, the ratio is reported.
            ["tokenBarMet"] = fullSearch == false && ratio is not null ? ratio <= TokenBar : null,
            ["diagnosticOnly"] = diagnostic,
        };
    }

    private static Dictionary<string, double> Scores(JsonObject report) =>
        (report["perTask"] ?? throw new ArgumentException("a report has no perTask scores; rerun it with this build"))
            .AsObject().ToDictionary(t => t.Key, t => (double)t.Value!, StringComparer.Ordinal);

    private static double? MedianTokens(JsonObject report) => (double?)report["toolUse"]?["medianInputTokens"];
}
