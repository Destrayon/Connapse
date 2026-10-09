using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Isolation;
using Connapse.Ingestion.Parsers;
using Connapse.Ingestion.Tests.Parsers;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Isolation;

// #680: a parser host that runs the PDF models through the shared inference host reads a PDF
// exactly as one that runs them itself, and still reads it when the inference host is gone.
[Trait("Category", "Integration")]
public sealed class InferenceRelayTests : IDisposable
{
    private static readonly string TestHostPath = Path.Combine(AppContext.BaseDirectory, "Connapse.ParserHost.TestHost.dll");
    private static readonly UploadSettings Settings = new() { PdfTextMode = "Layout" };

    private readonly ParserProcessPool _local = new(hostPath: TestHostPath) { UseSharedInference = false };
    private readonly ParserProcessPool _shared = new(hostPath: TestHostPath) { UseSharedInference = true };

    public void Dispose()
    {
        _local.Dispose();
        _shared.Dispose();
    }

    private static Task<ParsedDocument> Parse(ParserProcessPool pool, byte[] pdf) =>
        pool.ParseAsync(new PdfParser(), pdf, "report.pdf", Settings, TimeSpan.FromSeconds(120), CancellationToken.None);

    [Fact]
    public async Task TextPdf_ReadsTheSameThroughTheInferenceHost()
    {
        byte[] pdf = TestPdf.Build(PdfLayoutTests.TwoColumnPage());

        ParsedDocument local = await Parse(_local, pdf);
        ParsedDocument shared = await Parse(_shared, pdf);

        shared.Content.Should().NotBeEmpty().And.Be(local.Content);
        shared.Warnings.Should().Equal(local.Warnings);
        _shared.InferenceProcessId.Should().NotBeNull();
        _local.InferenceProcessId.Should().BeNull();
    }

    [Fact]
    public async Task ScannedPdf_ReadsTheSameThroughTheInferenceHost()
    {
        byte[] scan = TestScanPdf.Build(150, PdfLayoutTests.TwoColumnPage());

        ParsedDocument local = await Parse(_local, scan);
        ParsedDocument shared = await Parse(_shared, scan);

        shared.Content.Should().Contain("reservoirs").And.Be(local.Content);
    }

    [Fact]
    public async Task InferenceHostThatCannotStart_LayoutFallsBackAndOcrRunsInTheParserHost()
    {
        _shared.StartInfoForTests = start =>
        {
            if (start.ArgumentList.Contains(InferenceHostLoop.Argument))
                start.Environment["CONNAPSE_TEST_FAIL_TO_START"] = "1";
            return start;
        };

        ParsedDocument text = await Parse(_shared, TestPdf.Build(PdfLayoutTests.TwoColumnPage()));
        ParsedDocument scan = await Parse(_shared, TestScanPdf.Build(150, PdfLayoutTests.TwoColumnPage()));

        text.Content.Should().Contain("Northfield County Water Authority");
        text.Warnings.Should().Contain(w => w.Contains("Layout analysis failed") && w.Contains("inference process"));
        scan.Content.Should().Contain("reservoirs");
    }
}
