using Connapse.Eval.Checks;
using Connapse.Eval.Checks.OlmOcr;
using Connapse.Eval.Model;

namespace Connapse.Eval.Datasets;

/// <summary>
/// pypdf's encryption test files (py-pdf/pypdf resources/encryption). A PDF that cannot be opened
/// without a password must fail loudly. A PDF whose user or owner password is empty opens in any reader
/// without a prompt, so its text must be extracted like the unencrypted copy's.
/// </summary>
public sealed class PypdfEncryptionAdapter : IExtractionAdapter
{
    /// <summary>The first line every file in the set carries, checked in pypdf 6.1.1.</summary>
    public const string SharedText = "pdf encryption test";

    public string Name => "pypdf-encryption";

    /// <summary>
    /// The files that cannot be opened without a password, checked with pypdf 6.1.1 (with its AES
    /// support): <c>decrypt("")</c> fails for these and succeeds for every other file. The names do not
    /// decide it: r5-user-password.pdf and r6-user-password.pdf have an empty owner password, so any
    /// reader opens them without a prompt.
    /// <para>
    /// r4-aes-v2-no-key-length.pdf is not on the list although pypdf fails it. Its encryption
    /// dictionary omits /Length; PdfPig 0.1.16 opens it with the empty password and extracts text
    /// identical to unencrypted.pdf, which a wrong key could not produce, so the empty password is
    /// valid and pypdf's failure is its own handling of the missing length (checked 2026-10-01, #597).
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> PasswordRequired = new HashSet<string>(StringComparer.Ordinal)
    {
        "r2-user-password.pdf", "r3-user-password.pdf", "r4-user-password.pdf", "r4-aes-user-password.pdf",
        "r6-both-passwords.pdf",
    };

    public static bool NeedsPassword(string fileName) => PasswordRequired.Contains(fileName);

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
