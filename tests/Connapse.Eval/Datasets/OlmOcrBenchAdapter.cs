using System.Text.Json;
using Connapse.Eval.Checks;
using Connapse.Eval.Checks.OlmOcr;
using Connapse.Eval.Model;

namespace Connapse.Eval.Datasets;

/// <summary>
/// olmOCR-bench (allenai/olmOCR-bench): single-page PDFs with unit tests per category, one JSONL file
/// per category. Each PDF is a File document whose ID is its path under bench_data/pdfs; its tests
/// become extraction checks in that category, and every PDF without a baseline test gets a default
/// one in the "baseline" category, as benchmark.py does.
/// </summary>
public sealed class OlmOcrBenchAdapter : IExtractionAdapter, IFileListAdapter
{
    public static readonly IReadOnlyList<string> Categories =
        ["arxiv_math", "headers_footers", "long_tiny_text", "multi_column", "old_scans", "old_scans_math", "table_tests"];

    /// <summary>Native (born-digital) PDF categories that make up the headline; scans need OCR, which is deferred.</summary>
    public static readonly IReadOnlyList<string> HeadlineCategories = ["headers_footers", "long_tiny_text", "multi_column", "table_tests"];

    public static readonly IReadOnlyList<string> ScanCategories = ["old_scans", "old_scans_math"];

    public const string BaselineCategory = "baseline";

    public string Name => "olmocr-bench";

    public IReadOnlyList<string> ListFiles(string directory) =>
        Categories.SelectMany(c => PdfPaths(Path.Combine(directory, c + ".jsonl"))).Distinct(StringComparer.Ordinal).ToList();

    public Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct)
    {
        List<EvalDocument> corpus = ListFiles(directory)
            .Order(StringComparer.Ordinal)
            .Select(pdf => new EvalDocument(pdf, DocumentKind.File, null, null, null, new Dictionary<string, string>(),
                Path.Combine(directory, pdf.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();
        return Task.FromResult(new EvalDataset(datasetName, entry.Version, entry.Tags, corpus, [], new Qrels()));
    }

    public Task<ExtractionSpec> LoadChecksAsync(string directory, CancellationToken ct)
    {
        List<ExtractionCheck> checks = [];
        Dictionary<string, DocumentExpectation> documents = new(StringComparer.Ordinal);
        foreach (string category in Categories)
        {
            foreach (OlmOcrTest test in OlmOcrTestLoader.LoadJsonl(Path.Combine(directory, category + ".jsonl")))
            {
                if (test.Page != 1)
                    throw new InvalidDataException($"olmOCR test {test.Id} targets page {test.Page}; the adapter assumes single-page PDFs.");
                checks.Add(new ExtractionCheck(category, test));
                documents.TryAdd(test.Pdf, new DocumentExpectation(category, ExpectedIngestion.Extract));
            }
        }
        checks.AddRange(OlmOcrTestLoader.DefaultBaselines(checks.Select(c => c.Test))
            .Select(b => new ExtractionCheck(BaselineCategory, b)));
        return Task.FromResult(new ExtractionSpec(documents, checks));
    }

    private static IEnumerable<string> PdfPaths(string jsonl)
    {
        foreach (string line in File.ReadLines(jsonl))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using JsonDocument row = JsonDocument.Parse(line);
            yield return row.RootElement.GetProperty("pdf").GetString()
                ?? throw new InvalidDataException($"{jsonl} has a row without a pdf.");
        }
    }
}
