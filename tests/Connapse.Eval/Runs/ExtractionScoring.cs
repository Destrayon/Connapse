using Connapse.Eval.Checks;
using Connapse.Eval.Datasets;

namespace Connapse.Eval.Runs;

/// <param name="Rate">Passed ÷ (passed + failed); skipped checks are left out. NaN when nothing ran.</param>
public sealed record CategoryScore(string Dataset, string Category, string Level, int Passed, int Failed, int Skipped, double Rate);

public sealed record ExtractionDatasetScore(
    string Name,
    bool Complete,
    int Documents,
    int Rejected,
    int Failed,
    int Stalled,
    int SilentFailures,
    int MustFailLoudly,
    int FailedLoudly,
    IReadOnlyList<CategoryScore> Categories);

/// <summary>
/// The headline numbers of an extract run.
/// <para><see cref="SilentFailureRate"/>: File documents shown as fine although text is missing, ÷ all File documents.</para>
/// <para><see cref="FailsLoudlyRate"/>: documents that must fail loudly and did, ÷ those that must.</para>
/// <para><see cref="OlmOcrNative"/>: olmOCR mean of per-category pass rates over the native-PDF categories and
/// baseline, per level (baseline checks run on the parsed text only, so the chunk level has no baseline).</para>
/// <para><see cref="OlmOcrComparable"/>: olmOCR mean over all categories plus baseline at the parsed level, with
/// skipped math checks counted as failures, as a system without LaTeX output scores on the published benchmark.</para>
/// </summary>
public sealed record ExtractionScores(
    string RunName,
    RunManifest Manifest,
    IReadOnlyList<ExtractionDatasetScore> Datasets,
    double SilentFailureRate,
    double FailsLoudlyRate,
    IReadOnlyDictionary<string, double> OlmOcrNative,
    double? OlmOcrComparable);

public static class ExtractionScoring
{
    public static ExtractionScores Score(RunFolder run)
    {
        RunManifest manifest = run.ReadManifest();
        List<ExtractionDatasetScore> datasets = [];
        int files = 0, silent = 0, mustFail = 0, failedLoudly = 0;
        Dictionary<string, double> native = [];
        double? comparable = null;

        foreach (RunDatasetInfo info in manifest.Datasets)
        {
            if (!run.IsDatasetComplete(info.Name))
            {
                datasets.Add(new ExtractionDatasetScore(info.Name, false, info.Documents, 0, 0, 0, 0, 0, 0, []));
                continue;
            }
            IReadOnlyList<DocumentRecord> documents = run.ReadDocuments(info.Name);
            IReadOnlyList<CheckRecord> checks = run.ReadChecks(info.Name);
            List<CategoryScore> categories = Categories(info.Name, checks);

            int datasetSilent = checks.Count(c => c.Type == ExtractionEvaluator.NoSilentFailure && c.Outcome == CheckOutcome.Fail);
            int datasetMustFail = checks.Count(c => c.Type == ExtractionEvaluator.FailsLoudly);
            int datasetFailedLoudly = checks.Count(c => c.Type == ExtractionEvaluator.FailsLoudly && c.Outcome == CheckOutcome.Pass);
            files += documents.Count;
            silent += datasetSilent;
            mustFail += datasetMustFail;
            failedLoudly += datasetFailedLoudly;

            datasets.Add(new ExtractionDatasetScore(info.Name, true, documents.Count,
                documents.Count(d => d.UploadError is not null),
                documents.Count(d => d.Status == "Failed" || d.IngestionState == "Failed"),
                documents.Count(d => d.Stalled),
                datasetSilent, datasetMustFail, datasetFailedLoudly, categories));

            if (info.Name == "olmocr-bench")
            {
                foreach (string level in new[] { CheckLevel.Parsed, CheckLevel.Chunks })
                {
                    List<double> rates = categories
                        .Where(c => c.Level == level && !double.IsNaN(c.Rate)
                            && (OlmOcrBenchAdapter.HeadlineCategories.Contains(c.Category) || c.Category == OlmOcrBenchAdapter.BaselineCategory))
                        .Select(c => c.Rate).ToList();
                    if (rates.Count > 0)
                        native[level] = rates.Average();
                }
                comparable = OlmOcrBenchAdapter.Categories.Append(OlmOcrBenchAdapter.BaselineCategory)
                    .Select(category => categories.FirstOrDefault(c => c.Category == category && c.Level == CheckLevel.Parsed))
                    .Where(c => c is not null)
                    .Select(c => (double)c!.Passed / (c.Passed + c.Failed + c.Skipped))
                    .DefaultIfEmpty(double.NaN)
                    .Average();
            }
        }

        return new ExtractionScores(run.Name, manifest, datasets,
            files == 0 ? double.NaN : (double)silent / files,
            mustFail == 0 ? double.NaN : (double)failedLoudly / mustFail,
            native, comparable);
    }

    /// <summary>Pass rates per category and level for text checks; document-level checks are counted separately.</summary>
    public static List<CategoryScore> Categories(string dataset, IEnumerable<CheckRecord> checks) =>
        checks.Where(c => c.Level != CheckLevel.Document)
            .GroupBy(c => (c.Category, c.Level))
            .Select(g =>
            {
                int passed = g.Count(c => c.Outcome == CheckOutcome.Pass);
                int failed = g.Count(c => c.Outcome == CheckOutcome.Fail);
                int skipped = g.Count(c => c.Outcome == CheckOutcome.Skipped);
                return new CategoryScore(dataset, g.Key.Category, g.Key.Level, passed, failed, skipped,
                    passed + failed == 0 ? double.NaN : (double)passed / (passed + failed));
            })
            .OrderBy(c => c.Category, StringComparer.Ordinal)
            .ThenBy(c => c.Level, StringComparer.Ordinal)
            .ToList();
}
