using System.Globalization;
using System.Net;
using System.Text;
using Connapse.Eval.Metrics;
using Connapse.Eval.Model;
using Connapse.Eval.Runs;

namespace Connapse.Eval.Reports;

/// <summary>Self-contained HTML: inline CSS, no scripts, no external requests.</summary>
public static class HtmlReport
{
    private const string Style = """
        :root { --bg:#fff; --fg:#1b1f24; --muted:#59636e; --line:#d1d9e0; --up:#1a7f37; --down:#cf222e; --warn:#9a6700; }
        @media (prefers-color-scheme: dark) { :root { --bg:#0d1117; --fg:#e6edf3; --muted:#9198a1; --line:#3d444d; --up:#3fb950; --down:#f85149; --warn:#d29922; } }
        body { background:var(--bg); color:var(--fg); font:14px/1.45 system-ui, sans-serif; margin:24px; }
        table { border-collapse:collapse; margin:12px 0 24px; } th, td { border-bottom:1px solid var(--line); padding:4px 10px; text-align:right; }
        th:first-child, td:first-child { text-align:left; } .muted { color:var(--muted); } .up { color:var(--up); font-weight:600; }
        .down { color:var(--down); font-weight:600; } .banner { border-left:4px solid var(--warn); padding:6px 12px; margin:12px 0; }
        details { margin:6px 0; } code { font-size:12px; }
        """;

    public static string RenderRun(RunScores scores, RunFolder run)
    {
        StringBuilder html = Start($"Eval run {scores.RunName}");
        RunManifest m = scores.Manifest;
        html.Append($"<h1>{E(scores.RunName)}</h1><p class=muted>{E(m.Suite)} · {E(m.System)}/{E(m.Config)} · git {E(m.GitSha)}{(m.GitDirty ? " (dirty)" : "")} · {E(m.Machine)} · {m.StartedUtc:u}</p>");
        foreach (RunResume resume in m.Resumes ?? [])
            html.Append($"<p class=muted>resumed {resume.Utc:u} from git {E(resume.GitSha)}{(resume.GitDirty ? " (dirty)" : "")}</p>");
        html.Append("<p class=muted>");
        foreach ((string key, string value) in m.SystemDescription)
            html.Append($"{E(key)}=<code>{E(value)}</code> ");
        html.Append("</p>");

        foreach (DatasetScores d in scores.Datasets.Where(d => d.Invalid))
            html.Append($"<div class=banner>{E(d.Name)} is INVALID (more than 1% of documents failed to ingest) and is not scored.</div>");
        int errors = scores.Datasets.Sum(d => d.ErrorQueries);
        if (errors > 0)
            html.Append($"<div class=banner>{errors} queries returned an error and were scored 0.</div>");

        html.Append("<h2>Scores (test split)</h2><table><tr><th>Dataset</th>");
        foreach (string metric in MetricNames.All)
            html.Append($"<th>{E(metric)}</th>");
        html.Append("<th>queries</th><th>no-answer</th><th>errors</th><th>p50 ms</th><th>p95 ms</th></tr>");
        foreach (DatasetScores d in scores.Datasets.Where(d => !d.Invalid))
        {
            html.Append($"<tr><td>{E(d.Name)}</td>");
            foreach (string metric in MetricNames.All)
                html.Append($"<td>{F(d.Means[metric])}</td>");
            html.Append($"<td>{d.TestQueries}</td><td>{d.NoAnswerQueries}</td><td>{d.ErrorQueries}</td><td>{d.LatencyP50Ms:F0}</td><td>{d.LatencyP95Ms:F0}</td></tr>");
        }
        foreach ((string domain, IReadOnlyDictionary<string, double> means) in scores.Domains.OrderBy(p => p.Key, StringComparer.Ordinal))
            AppendMeansRow(html, domain, means);
        AppendMeansRow(html, "portfolio", scores.Portfolio);
        html.Append("</table>");

        html.Append("<h2>Worst queries</h2>");
        foreach (DatasetScores d in scores.Datasets.Where(d => !d.Invalid && d.PerQuery.Count > 0))
            AppendFailures(html, d, run);

        return html.Append("</body></html>").ToString();
    }

    public static string RenderComparison(Comparison c)
    {
        StringBuilder html = Start("Eval comparison");
        html.Append($"<h1>{E(c.Candidate)}</h1><p class=muted>compared with baseline {E(c.Baseline)} · paired by query · Holm-corrected permutation p &lt; 0.05 is significant</p>");
        if (c.DatasetMismatches.Count > 0)
            html.Append($"<div class=banner>Dataset versions differ for {E(string.Join(", ", c.DatasetMismatches))}; those rows compare different data.</div>");
        if (c.UnpairedDatasets.Count > 0)
            html.Append($"<div class=banner>Not compared (missing or invalid on one side): {E(string.Join(", ", c.UnpairedDatasets))}.</div>");
        IReadOnlyList<string> partial = ComparisonBuilder.PartialOverlap(c.Pairings);
        if (partial.Count > 0)
            html.Append($"<div class=banner>Partial query overlap for {E(string.Join(", ", partial))}: only queries both runs scored are compared.</div>");
        html.Append($"<p><strong>{E(c.Verdict)}</strong></p>");

        html.Append("<table><tr><th>Dataset</th><th>Metric</th><th>Δ</th><th>95% CI</th><th>p (Holm)</th><th>p (t-test)</th><th>d_z</th><th>MDE</th><th>n</th></tr>");
        foreach (MetricComparison row in c.Rows)
        {
            string css = !row.Significant ? "" : row.Stats.MeanDifference > 0 ? " class=up" : " class=down";
            html.Append($"<tr><td>{E(row.Dataset)}</td><td>{E(row.Metric)}</td><td{css}>{S(row.Stats.MeanDifference)}</td>"
                + $"<td>[{S(row.Stats.CiLow)}, {S(row.Stats.CiHigh)}]</td><td>{row.HolmP:F4}</td><td>{row.Stats.TTestP:F4}</td>"
                + $"<td>{F(row.Stats.EffectSizeDz)}</td><td>{F(row.Stats.MinimumDetectableEffect)}</td><td>{row.Stats.N}</td></tr>");
        }
        html.Append("</table><h2>Queries and judged@10</h2><table><tr><th>Dataset</th><th>baseline queries</th><th>candidate queries</th><th>paired</th><th>judged@10</th></tr>");
        foreach (DatasetPairing p in c.Pairings)
            html.Append($"<tr><td>{E(p.Dataset)}</td><td>{p.BaselineQueries}</td><td>{p.CandidateQueries}</td><td>{p.PairedQueries}</td>"
                + $"<td>{F(p.BaselineJudgedAt10)} → {F(p.CandidateJudgedAt10)}</td></tr>");
        html.Append("</table><h2>Portfolio change</h2><table><tr><th>Metric</th><th>Δ</th></tr>");
        foreach ((string metric, double delta) in c.PortfolioDelta)
            html.Append($"<tr><td>{E(metric)}</td><td>{(double.IsNaN(delta) ? "—" : S(delta))}</td></tr>");
        return html.Append("</table></body></html>").ToString();
    }

    private static void AppendFailures(StringBuilder html, DatasetScores d, RunFolder run)
    {
        Qrels qrels = run.ReadQrels(d.Name);
        IReadOnlyDictionary<string, string> titles = run.ReadTitles(d.Name);
        Dictionary<string, QueryResult> results = run.ReadResults(d.Name).ToDictionary(r => r.QueryId, StringComparer.Ordinal);
        html.Append($"<h3>{E(d.Name)}</h3>");
        foreach ((string queryId, IReadOnlyDictionary<string, double> scores) in d.PerQuery
            .OrderBy(p => p.Value[MetricNames.Ndcg10]).ThenBy(p => p.Key, StringComparer.Ordinal).Take(20))
        {
            QueryResult result = results[queryId];
            IReadOnlyList<RankedDoc> ordered = RankingMetrics.Order(result.Ranked);
            html.Append($"<details><summary>nDCG@10 {F(scores[MetricNames.Ndcg10])} · {E(queryId)} · {E(result.QueryText)}</summary>");
            if (result.Error is not null)
                html.Append($"<p class=down>error: {E(result.Error)}</p>");
            html.Append("<p>Relevant:</p><ul>");
            foreach ((string docId, int grade) in qrels.For(queryId).Where(p => p.Value >= 1).OrderByDescending(p => p.Value))
            {
                int rank = ordered.Select((r, i) => (r.DocId, Rank: i + 1)).FirstOrDefault(x => x.DocId == docId).Rank;
                html.Append($"<li>{E(docId)} (grade {grade}, rank {(rank == 0 ? "—" : rank.ToString(CultureInfo.InvariantCulture))}) {E(titles.GetValueOrDefault(docId, ""))}</li>");
            }
            html.Append("</ul><p>Returned:</p><ol>");
            foreach (RankedDoc doc in ordered.Take(10))
            {
                string grade = qrels.For(queryId).TryGetValue(doc.DocId, out int g) ? $"grade {g}" : "unjudged";
                html.Append($"<li>{E(doc.DocId)} <span class=muted>({grade}, score {F(doc.Score)})</span> {E(titles.GetValueOrDefault(doc.DocId, ""))}</li>");
            }
            html.Append($"</ol><p class=muted>{result.Trace.Total.TotalMilliseconds:F0} ms</p></details>");
        }
    }

    private static void AppendMeansRow(StringBuilder html, string label, IReadOnlyDictionary<string, double> means)
    {
        html.Append($"<tr><td><strong>{E(label)}</strong></td>");
        foreach (string metric in MetricNames.All)
            html.Append($"<td><strong>{F(means[metric])}</strong></td>");
        html.Append("<td></td><td></td><td></td><td></td><td></td></tr>");
    }

    private static StringBuilder Start(string title) =>
        new($"<!doctype html><html><head><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\"><title>{E(title)}</title><style>{Style}</style></head><body>");

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private static string F(double value) =>
        double.IsNaN(value) ? "—" : double.IsInfinity(value) ? "∞" : value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string S(double value) => value.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
}
