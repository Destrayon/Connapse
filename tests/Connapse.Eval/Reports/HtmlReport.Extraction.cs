using System.Text;
using Connapse.Eval.Checks;
using Connapse.Eval.Datasets;
using Connapse.Eval.Runs;

namespace Connapse.Eval.Reports;

public static partial class HtmlReport
{
    public static string RenderExtraction(ExtractionScores scores, RunFolder run)
    {
        StringBuilder html = Start($"Extract run {scores.RunName}");
        RunManifest m = scores.Manifest;
        html.Append($"<h1>{E(scores.RunName)}</h1><p class=muted>extract · {E(m.Suite)} · {E(m.Config)} · git {E(m.GitSha)}{(m.GitDirty ? " (dirty)" : "")} · {E(m.Machine)} · {m.StartedUtc:u}</p><p class=muted>");
        foreach ((string key, string value) in m.SystemDescription)
            html.Append($"{E(key)}=<code>{E(value)}</code> ");
        html.Append("</p>");
        if (m.SystemDescription.GetValueOrDefault("embedding.provider") == nameof(Systems.HashingEmbeddingProvider))
            html.Append("<div class=banner>Chunks were made with the offline hashing embedder. The Semantic chunker places boundaries by embedding similarity, so chunk-level results differ from production; run with --real-embedder to match it.</div>");
        foreach (ExtractionDatasetScore d in scores.Datasets.Where(d => !d.Complete))
            html.Append($"<div class=banner>{E(d.Name)} did not finish; ingestion may have crashed the in-process host.</div>");

        html.Append("<h2>Headline</h2><table><tr><th>Measure</th><th>Value</th></tr>");
        html.Append($"<tr><td>Silent-failure rate (files shown as fine with text missing)</td><td>{Pct(scores.SilentFailureRate)}</td></tr>");
        html.Append($"<tr><td>Fails-loudly pass rate (unreadable files reported as failed)</td><td>{Pct(scores.FailsLoudlyRate)}</td></tr>");
        foreach ((string level, double value) in scores.OlmOcrNative)
            html.Append($"<tr><td>olmOCR native-PDF score, {E(level)} level</td><td>{Pct(value)}</td></tr>");
        if (scores.OlmOcrComparable is double comparable)
            html.Append($"<tr><td>olmOCR overall, comparable to published results (math counted as failed)</td><td>{Pct(comparable)}</td></tr>");
        html.Append("</table>");

        html.Append("<h2>Documents</h2><table><tr><th>Dataset</th><th>files</th><th>rejected</th><th>failed</th><th>stalled</th><th>silent failures</th><th>failed loudly</th></tr>");
        foreach (ExtractionDatasetScore d in scores.Datasets.Where(d => d.Complete))
            html.Append($"<tr><td>{E(d.Name)}</td><td>{d.Documents}</td><td>{d.Rejected}</td><td>{d.Failed}</td><td>{d.Stalled}</td>"
                + $"<td>{d.SilentFailures}</td><td>{(d.MustFailLoudly == 0 ? "—" : $"{d.FailedLoudly}/{d.MustFailLoudly}")}</td></tr>");
        html.Append("</table>");

        html.Append("<h2>Checks by category</h2><p class=muted>parsed = the parser's text; chunks = one single stored chunk. "
            + "A drop from parsed to chunks is text split by chunking.</p>"
            + "<table><tr><th>Dataset</th><th>Category</th><th>parsed</th><th>chunks</th><th>parsed − chunks</th><th>checks</th><th>skipped</th></tr>");
        foreach (ExtractionDatasetScore d in scores.Datasets.Where(d => d.Complete))
            foreach (IGrouping<string, CategoryScore> category in d.Categories.GroupBy(c => c.Category))
            {
                CategoryScore? parsed = category.FirstOrDefault(c => c.Level == CheckLevel.Parsed);
                CategoryScore? chunks = category.FirstOrDefault(c => c.Level == CheckLevel.Chunks);
                string label = d.Name == "olmocr-bench" && OlmOcrBenchAdapter.ScanCategories.Contains(category.Key)
                    ? $"{category.Key} (scans; OCR deferred)" : category.Key;
                string gap = parsed is null || chunks is null ? "—" : Pct(parsed.Rate - chunks.Rate);
                html.Append($"<tr><td>{E(d.Name)}</td><td>{E(label)}</td><td>{Pct(parsed?.Rate)}</td><td>{Pct(chunks?.Rate)}</td><td>{gap}</td>"
                    + $"<td>{(parsed is null ? 0 : parsed.Passed + parsed.Failed)}</td><td>{parsed?.Skipped ?? 0}</td></tr>");
            }
        html.Append("</table>");

        html.Append("<h2>Documents that need attention</h2>");
        foreach (ExtractionDatasetScore d in scores.Datasets.Where(d => d.Complete))
        {
            Dictionary<string, DocumentRecord> docs = run.ReadDocuments(d.Name).ToDictionary(x => x.DocId, StringComparer.Ordinal);
            List<CheckRecord> flagged = run.ReadChecks(d.Name)
                .Where(c => c.Level == CheckLevel.Document && c.Outcome == CheckOutcome.Fail).ToList();
            if (flagged.Count == 0)
                continue;
            html.Append($"<h3>{E(d.Name)} ({flagged.Count})</h3><table><tr><th>Document</th><th>Check</th><th>Status / state</th><th>Detail</th></tr>");
            foreach (CheckRecord check in flagged.Take(100))
            {
                DocumentRecord doc = docs[check.DocId];
                html.Append($"<tr><td>{E(check.DocId)}</td><td>{E(check.Type)}</td><td>{E(doc.UploadError is null ? $"{doc.Status} / {doc.IngestionState}" : "rejected")}</td><td>{E(check.Detail)}</td></tr>");
            }
            html.Append(flagged.Count > 100 ? $"</table><p class=muted>… and {flagged.Count - 100} more in checks/{E(d.Name)}.jsonl</p>" : "</table>");
        }

        return html.Append("</body></html>").ToString();
    }

    public static string RenderExtractionComparison(ExtractionComparisonResult c)
    {
        StringBuilder html = Start("Extract comparison");
        html.Append($"<h1>{E(c.Candidate)}</h1><p class=muted>compared with baseline {E(c.Baseline)} · paired by check · Holm-corrected permutation p &lt; 0.05 is significant</p>");
        if (c.UnpairedDatasets.Count > 0)
            html.Append($"<div class=banner>Not compared (missing or unfinished on one side): {E(string.Join(", ", c.UnpairedDatasets))}.</div>");
        html.Append($"<p><strong>{E(c.Verdict)}</strong></p><p>Silent-failure rate Δ {S(c.SilentFailureDelta)} · fails-loudly pass rate Δ {S(c.FailsLoudlyDelta)}</p>");
        html.Append("<table><tr><th>Dataset</th><th>Category</th><th>Level</th><th>baseline</th><th>candidate</th><th>Δ</th><th>p (Holm)</th><th>MDE</th><th>n</th></tr>");
        foreach (CategoryComparison row in c.Rows)
        {
            string css = !row.Significant ? "" : row.Stats!.MeanDifference > 0 ? " class=up" : " class=down";
            html.Append($"<tr><td>{E(row.Dataset)}</td><td>{E(row.Category)}</td><td>{E(row.Level)}</td><td>{Pct(row.BaselineRate)}</td><td>{Pct(row.CandidateRate)}</td>"
                + $"<td{css}>{(row.Stats is null ? "—" : S(row.Stats.MeanDifference))}</td><td>{(double.IsNaN(row.HolmP) ? "—" : row.HolmP.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))}</td>"
                + $"<td>{(row.Stats is null ? "—" : F(row.Stats.MinimumDetectableEffect))}</td><td>{row.Stats?.N ?? 0}</td></tr>");
        }
        return html.Append("</table></body></html>").ToString();
    }

    private static string Pct(double? value) =>
        value is null || double.IsNaN(value.Value) ? "—" : (value.Value * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
}
