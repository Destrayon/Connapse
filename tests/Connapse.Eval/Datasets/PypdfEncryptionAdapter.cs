using Connapse.Eval.Checks;
using Connapse.Eval.Checks.OlmOcr;
using Connapse.Eval.Model;

namespace Connapse.Eval.Datasets;

/// <summary>
/// pypdf's encryption test files (py-pdf/pypdf resources/encryption). A PDF that needs a user password
/// cannot be read without one, so Connapse must fail it loudly. A PDF with an empty user password
/// (the "empty-password" and "owner-password" files) opens in any reader without a prompt, so its text
/// must be extracted like the unencrypted copy's.
/// </summary>
public sealed class PypdfEncryptionAdapter : IExtractionAdapter
{
    /// <summary>The first line every file in the set carries, checked in pypdf 6.1.1.</summary>
    public const string SharedText = "pdf encryption test";

    public string Name => "pypdf-encryption";

    /// <summary>
    /// Whether opening the file needs a password. Names say so, except r4-aes-v2-no-key-length.pdf,
    /// which pypdf 6.1.1 could not open with an empty password.
    /// </summary>
    public static bool NeedsPassword(string fileName) =>
        fileName.Contains("user-password", StringComparison.Ordinal)
        || fileName.Contains("both-passwords", StringComparison.Ordinal)
        || fileName == "r4-aes-v2-no-key-length.pdf";

    public Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct)
    {
        List<EvalDocument> corpus = entry.Files
            .Select(f => new EvalDocument(f.Name, DocumentKind.File, null, null, null, new Dictionary<string, string>(),
                Path.Combine(directory, f.Name)))
            .ToList();
        return Task.FromResult(new EvalDataset(datasetName, entry.Version, entry.Tags, corpus, [], new Qrels()));
    }

    public Task<ExtractionSpec> LoadChecksAsync(string directory, CancellationToken ct)
    {
        Dictionary<string, DocumentExpectation> documents = new(StringComparer.Ordinal);
        List<ExtractionCheck> checks = [];
        foreach (string file in Directory.EnumerateFiles(directory, "*.pdf").Select(Path.GetFileName).Order(StringComparer.Ordinal)!)
        {
            if (NeedsPassword(file!))
            {
                documents[file!] = new DocumentExpectation("password-required", ExpectedIngestion.FailLoudly);
                continue;
            }
            documents[file!] = new DocumentExpectation("readable", ExpectedIngestion.Extract);
            checks.Add(new ExtractionCheck("readable",
                new TextPresenceTest(file!, 1, file + "_present", "present", 0, OlmOcrText.Normalize(SharedText)!)));
        }
        return Task.FromResult(new ExtractionSpec(documents, checks));
    }
}
