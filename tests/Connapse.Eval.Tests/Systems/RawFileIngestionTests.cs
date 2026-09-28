using Connapse.Core;
using Connapse.Eval.Datasets.Generated;
using Connapse.Eval.Model;
using Connapse.Eval.Systems;
using FluentAssertions;

namespace Connapse.Eval.Tests.Systems;

[Trait("Category", "Integration")]
[Collection(EvalHostCollection.Name)]
public class RawFileIngestionTests
{
    [Fact]
    public async Task IndexAsync_FileDocuments_RunRealParsersAndProbeReadsBothViews()
    {
        string root = ConnapseSearchSystemTests.FindRepoRoot();
        string work = Path.Combine(Path.GetTempPath(), "eval-raw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string docx = Write(work, "memo.docx", DocumentBuilders.Docx("Quarterly pelican census", "Estuary counts rose in spring."));
        string pdf = Write(work, "report.pdf", DocumentBuilders.TextPdf("Page one mentions sourdough.", "Page two mentions fermentation."));
        string scan = Write(work, "scan.pdf", DocumentBuilders.ImageOnlyPdf(DocumentBuilders.StripedPng()));
        string empty = Write(work, "empty.pdf", []);

        SystemConfig config = new("it", SearchMode.Keyword, new Dictionary<string, string>
        {
            ["Knowledge:Embedding:Model"] = "hashing-test",
            ["Knowledge:Embedding:Dimensions"] = "64",
        });
        EvalDataset dataset = new("it-raw", "1", [],
            [File("docx", docx), File("pdf", pdf), File("scan", scan), File("empty", empty),
                File("missing", Path.Combine(work, "does-not-exist.pdf"))], [], new Qrels());

        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(
            config, Path.Combine(root, "src", "Connapse.Web"), new EmbeddingDiskCache(Path.Combine(work, "cache")),
            TextWriter.Null, new HashingEmbeddingProvider(), CancellationToken.None);

        IndexReport report = await system.IndexAsync(dataset, IngestionWait.RecordStalls, CancellationToken.None);
        Dictionary<string, DocumentOutcome> outcomes = report.Outcomes.ToDictionary(o => o.DatasetDocId);

        outcomes["docx"].Status.Should().Be("Ready");
        ProbeResult docxProbe = await IngestionProbe.ProbeAsync(system.Services, docx, outcomes["docx"].ConnapseDocId, CancellationToken.None);
        docxProbe.ParsedText.Should().Contain("Quarterly pelican census");
        docxProbe.Chunks.Should().Contain(c => c.Contains("pelican"));

        ProbeResult pdfProbe = await IngestionProbe.ProbeAsync(system.Services, pdf, outcomes["pdf"].ConnapseDocId, CancellationToken.None);
        pdfProbe.PageCount.Should().Be(2);
        pdfProbe.EmptyPages.Should().BeEmpty();
        pdfProbe.ParsedText.Should().Contain("fermentation").And.NotContain("--- Page");
        pdfProbe.Chunks.Should().NotBeEmpty().And.NotContain(c => c.Contains("--- Page"));

        // An image-only PDF yields no text. That is a permanent failure — the same file will never
        // yield any — and the UI shows it as one (#562; it used to be marked indexed, so shown Ready).
        outcomes["scan"].Status.Should().Be("Failed");
        outcomes["scan"].IngestionStatus.Should().Be(DocumentStatus.FailedPermanent);
        ProbeResult scanProbe = await IngestionProbe.ProbeAsync(system.Services, scan, outcomes["scan"].ConnapseDocId, CancellationToken.None);
        scanProbe.EmptyPages.Should().Equal(1);
        scanProbe.Chunks.Should().BeEmpty();

        outcomes["empty"].UploadError.Should().Contain("Zero-byte");
        outcomes["missing"].UploadError.Should().StartWith("could not open the file");
        report.FailedDocumentIds.Should().BeEquivalentTo(["scan", "empty", "missing"]);
    }

    private static EvalDocument File(string id, string path) =>
        new(id, DocumentKind.File, null, null, null, new Dictionary<string, string>(), path);

    private static string Write(string dir, string name, byte[] bytes)
    {
        string path = Path.Combine(dir, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }
}
