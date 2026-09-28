using System.Text;
using Connapse.Eval.Checks;
using Connapse.Eval.Checks.OlmOcr;
using Connapse.Eval.Model;

namespace Connapse.Eval.Datasets.Generated;

/// <summary>
/// Files Connapse cannot read, generated at load time so nothing is downloaded or committed. Every
/// one must fail loudly: be rejected at upload, or end Failed with a reason, never Ready with no text.
/// The DOCX zip bomb comes last, because an out-of-memory crash would take the in-process host down.
/// </summary>
public sealed class GeneratedNegativesAdapter : IExtractionAdapter
{
    public const long ZipBombInflatedBytes = 200L * 1024 * 1024;

    public string Name => "generated-negatives";

    /// <summary>(file name, category, bytes); built lazily because the zip bomb takes a moment.</summary>
    public static IEnumerable<(string Name, string Category, Func<byte[]> Build)> Files()
    {
        byte[] Pdf() => DocumentBuilders.TextPdf("A generated page that is cut in half.", "Its second page.");
        yield return ("truncated.pdf", "truncated", () => Pdf()[..(Pdf().Length / 2)]);
        yield return ("image-only.pdf", "image-only", () => DocumentBuilders.ImageOnlyPdf(DocumentBuilders.StripedPng()));
        yield return ("image-only.docx", "image-only", () => DocumentBuilders.ImageOnlyDocx(DocumentBuilders.StripedPng()));
        yield return ("png-named.pdf", "wrong-extension", () => DocumentBuilders.StripedPng());
        yield return ("text-named.docx", "wrong-extension", () => Encoding.UTF8.GetBytes("Plain text saved with a .docx extension."));
        yield return ("pdf-named.docx", "wrong-extension", () => Pdf());
        yield return ("zero-byte.pdf", "empty", () => []);
        yield return ("zip-bomb.docx", "zip-bomb", () => DocumentBuilders.ZipBombDocx(ZipBombInflatedBytes));
    }

    public async Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        List<EvalDocument> corpus = [];
        foreach ((string name, _, Func<byte[]> build) in Files())
        {
            string path = Path.Combine(directory, name);
            await File.WriteAllBytesAsync(path, build(), ct);
            corpus.Add(new EvalDocument(name, DocumentKind.File, null, null, null, new Dictionary<string, string>(), path));
        }
        return new EvalDataset(datasetName, entry.Version, entry.Tags, corpus, [], new Qrels());
    }

    public Task<ExtractionSpec> LoadChecksAsync(string directory, CancellationToken ct) =>
        Task.FromResult(new ExtractionSpec(
            Files().ToDictionary(f => f.Name, f => new DocumentExpectation(f.Category, ExpectedIngestion.FailLoudly), StringComparer.Ordinal),
            []));
}

/// <summary>
/// The same sentence as TXT and Markdown in five encodings. Connapse should read every file's text
/// correctly; a <c>present</c> check at both levels catches garbled characters.
/// </summary>
public sealed class GeneratedEncodingsAdapter : IExtractionAdapter
{
    public const string Sentence = "Encoding check: café, naïve, Größe and 日本語 must survive.";

    /// <summary>Latin-1 cannot encode the Japanese, so its files carry only the Latin part.</summary>
    public const string LatinSentence = "Encoding check: café, naïve, Größe must survive.";

    public string Name => "generated-encodings";

    public static IEnumerable<(string Stem, string Text, byte[] Bytes)> Variants()
    {
        yield return ("utf8", Sentence, Encoding.UTF8.GetBytes(Sentence));
        yield return ("utf8-bom", Sentence, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Sentence)]);
        yield return ("utf16le-bom", Sentence, [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Sentence)]);
        yield return ("utf16le", Sentence, Encoding.Unicode.GetBytes(Sentence));
        // For these characters Latin-1 and Windows-1252 produce identical bytes.
        yield return ("latin1", LatinSentence, Encoding.Latin1.GetBytes(LatinSentence));
    }

    public async Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        List<EvalDocument> corpus = [];
        foreach ((string stem, _, byte[] bytes) in Variants())
            foreach (string extension in new[] { ".txt", ".md" })
            {
                string name = stem + extension;
                string path = Path.Combine(directory, name);
                await File.WriteAllBytesAsync(path, bytes, ct);
                corpus.Add(new EvalDocument(name, DocumentKind.File, null, null, null, new Dictionary<string, string>(), path));
            }
        return new EvalDataset(datasetName, entry.Version, entry.Tags, corpus, [], new Qrels());
    }

    public Task<ExtractionSpec> LoadChecksAsync(string directory, CancellationToken ct)
    {
        Dictionary<string, DocumentExpectation> documents = new(StringComparer.Ordinal);
        List<ExtractionCheck> checks = [];
        foreach ((string stem, string text, _) in Variants())
            foreach (string extension in new[] { ".txt", ".md" })
            {
                string name = stem + extension;
                documents[name] = new DocumentExpectation(stem, ExpectedIngestion.Extract);
                checks.Add(new ExtractionCheck(stem,
                    new TextPresenceTest(name, 1, name + "_present", "present", 0, OlmOcrText.Normalize(text)!)));
            }
        return Task.FromResult(new ExtractionSpec(documents, checks));
    }
}
