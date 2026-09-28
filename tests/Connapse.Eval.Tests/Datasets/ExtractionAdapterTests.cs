using System.Security.Cryptography;
using System.Text;
using Connapse.Eval.Checks;
using Connapse.Eval.Checks.OlmOcr;
using Connapse.Eval.Datasets;
using Connapse.Eval.Datasets.Generated;
using Connapse.Eval.Model;
using FluentAssertions;

namespace Connapse.Eval.Tests.Datasets;

[Trait("Category", "Unit")]
public class ExtractionAdapterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eval-extract-" + Guid.NewGuid().ToString("N"));

    public ExtractionAdapterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static DatasetEntry Entry(string adapter, params string[] files) =>
        new(adapter, "rev1", [], files.Select(f => new DatasetFile(f, "https://example.invalid/" + f, null)).ToList());

    [Fact]
    public async Task OlmOcrBench_LoadsDocumentsChecksAndDefaultBaselines()
    {
        foreach (string category in OlmOcrBenchAdapter.Categories)
            File.WriteAllText(Path.Combine(_dir, category + ".jsonl"), "");
        File.WriteAllLines(Path.Combine(_dir, "headers_footers.jsonl"),
        [
            """{"pdf": "headers_footers/a.pdf", "page": 1, "id": "a_h1", "type": "absent", "text": "Journal of Things", "max_diffs": 1}""",
            """{"pdf": "headers_footers/b.pdf", "page": 1, "id": "b_base", "type": "baseline"}""",
        ]);
        File.WriteAllLines(Path.Combine(_dir, "multi_column.jsonl"),
            ["""{"pdf": "multi_column/c.pdf", "page": 1, "id": "c_o1", "type": "order", "before": "first part", "after": "second part"}"""]);
        OlmOcrBenchAdapter adapter = new();

        EvalDataset dataset = await adapter.LoadAsync("olmocr-bench", Entry("olmocr-bench"), _dir, CancellationToken.None);
        ExtractionSpec spec = await adapter.LoadChecksAsync(_dir, CancellationToken.None);

        adapter.ListFiles(_dir).Should().BeEquivalentTo(["headers_footers/a.pdf", "headers_footers/b.pdf", "multi_column/c.pdf"]);
        dataset.Corpus.Should().OnlyContain(d => d.Kind == DocumentKind.File);
        dataset.Corpus.Single(d => d.Id == "multi_column/c.pdf").FilePath.Should().Be(Path.Combine(_dir, "multi_column", "c.pdf"));
        spec.Documents["multi_column/c.pdf"].Should().Be(new DocumentExpectation("multi_column", ExpectedIngestion.Extract));
        spec.Checks.Where(c => c.Category == OlmOcrBenchAdapter.BaselineCategory).Select(c => c.Test.Pdf)
            .Should().BeEquivalentTo(["headers_footers/a.pdf", "multi_column/c.pdf"]);
    }

    [Fact]
    public async Task OlmOcrBench_MultiPageTest_Throws()
    {
        foreach (string category in OlmOcrBenchAdapter.Categories)
            File.WriteAllText(Path.Combine(_dir, category + ".jsonl"), "");
        File.WriteAllLines(Path.Combine(_dir, "long_tiny_text.jsonl"),
            ["""{"pdf": "long_tiny_text/a.pdf", "page": 2, "id": "a_p", "type": "present", "text": "tiny"}"""]);

        Func<Task> act = () => new OlmOcrBenchAdapter().LoadChecksAsync(_dir, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*page 2*");
    }

    [Theory]
    [InlineData("r2-user-password.pdf", true)]
    [InlineData("r6-both-passwords.pdf", true)]
    [InlineData("r4-aes-v2-no-key-length.pdf", true)]
    [InlineData("r3-empty-password.pdf", false)]
    [InlineData("r4-owner-password.pdf", false)]
    [InlineData("unencrypted.pdf", false)]
    public void PypdfEncryption_NeedsPassword_FollowsNamesAndTheOneException(string file, bool needsPassword) =>
        PypdfEncryptionAdapter.NeedsPassword(file).Should().Be(needsPassword);

    [Fact]
    public async Task PypdfEncryption_ReadableFilesGetAPresenceCheck_LockedFilesMustFailLoudly()
    {
        File.WriteAllBytes(Path.Combine(_dir, "r2-user-password.pdf"), [1]);
        File.WriteAllBytes(Path.Combine(_dir, "r2-empty-password.pdf"), [1]);

        ExtractionSpec spec = await new PypdfEncryptionAdapter().LoadChecksAsync(_dir, CancellationToken.None);

        spec.Documents["r2-user-password.pdf"].Expected.Should().Be(ExpectedIngestion.FailLoudly);
        spec.Documents["r2-empty-password.pdf"].Expected.Should().Be(ExpectedIngestion.Extract);
        spec.Checks.Should().ContainSingle().Which.Test.Should().BeOfType<TextPresenceTest>()
            .Which.Text.Should().Be(PypdfEncryptionAdapter.SharedText);
    }

    [Fact]
    public async Task GeneratedNegatives_AreDeterministicAndAllMustFailLoudly()
    {
        GeneratedNegativesAdapter adapter = new();
        string second = Path.Combine(_dir, "second");

        EvalDataset first = await adapter.LoadAsync("generated-negatives", Entry("generated-negatives"), Path.Combine(_dir, "first"), CancellationToken.None);
        await adapter.LoadAsync("generated-negatives", Entry("generated-negatives"), second, CancellationToken.None);
        ExtractionSpec spec = await adapter.LoadChecksAsync(second, CancellationToken.None);

        foreach (EvalDocument doc in first.Corpus)
            Hash(doc.FilePath!).Should().Be(Hash(Path.Combine(second, doc.Id)), doc.Id);
        first.Corpus[^1].Id.Should().Be("zip-bomb.docx");
        new FileInfo(first.Corpus.Single(d => d.Id == "zero-byte.pdf").FilePath!).Length.Should().Be(0);
        new FileInfo(first.Corpus.Single(d => d.Id == "zip-bomb.docx").FilePath!).Length.Should().BeLessThan(2_000_000);
        spec.Documents.Values.Should().OnlyContain(e => e.Expected == ExpectedIngestion.FailLoudly);
        spec.Documents.Keys.Should().BeEquivalentTo(first.Corpus.Select(d => d.Id));
    }

    [Fact]
    public async Task GeneratedEncodings_WritesEachEncodingAndChecksItsText()
    {
        GeneratedEncodingsAdapter adapter = new();

        EvalDataset dataset = await adapter.LoadAsync("generated-encodings", Entry("generated-encodings"), _dir, CancellationToken.None);
        ExtractionSpec spec = await adapter.LoadChecksAsync(_dir, CancellationToken.None);

        dataset.Corpus.Should().HaveCount(10);
        File.ReadAllBytes(Path.Combine(_dir, "utf16le.txt")).Should().Equal(Encoding.Unicode.GetBytes(GeneratedEncodingsAdapter.Sentence));
        File.ReadAllBytes(Path.Combine(_dir, "utf8-bom.md")).Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        spec.Checks.Should().HaveCount(10);
        spec.Checks.Single(c => c.Test.Pdf == "latin1.md").Test.Should().BeOfType<TextPresenceTest>()
            .Which.Text.Should().Be(GeneratedEncodingsAdapter.LatinSentence);
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
