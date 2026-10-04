using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Parsers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Ingestion.Tests.Parsers;

/// <summary>OCR of PDF pages with no text layer (#598), on image-only PDFs built like scans.</summary>
[Trait("Category", "Unit")]
public class PdfOcrTests
{
    private static PdfParser Parser(UploadSettings settings)
    {
        var monitor = Substitute.For<IOptionsMonitor<UploadSettings>>();
        monitor.CurrentValue.Returns(settings);
        return new PdfParser(monitor);
    }

    private static TestPdf.Page Page(string text) => new([new(72, 700, text)]);

    private static Task<ParsedDocument> ParseAsync(byte[] pdf, UploadSettings? settings = null) =>
        Parser(settings ?? new UploadSettings()).ParseAsync(new MemoryStream(pdf), "scan.pdf");

    [Fact]
    public async Task ParseAsync_ScannedPage_IsReadByOcr()
    {
        byte[] scan = TestScanPdf.Build(pages: Page("Quarterly harbour report for the northern channel"));

        var parsed = await ParseAsync(scan);

        parsed.Content.Should().Contain("harbour report");
        parsed.Metadata[PdfParser.MetadataKeyOcrPages].Should().Be("1");
        parsed.Metadata.Should().ContainKey("OcrSecondsPerPage");
    }

    [Fact]
    public void OutputSettings_ChangeWithWhatChangesTheText()
    {
        string baseline = Parser(new UploadSettings()).OutputSettings;

        Parser(new UploadSettings { PdfTableMode = "Off" }).OutputSettings.Should().NotBe(baseline);
        Parser(new UploadSettings { PdfOcr = false }).OutputSettings.Should().NotBe(baseline);
        Parser(new UploadSettings { PdfOcrDpi = 300 }).OutputSettings.Should().NotBe(baseline);
        Parser(new UploadSettings { PdfLayoutOnOcrPages = false }).OutputSettings.Should().NotBe(baseline);
        Parser(new UploadSettings { PdfOcrThreads = 4, MaxOcrPagesPerDocument = 5 }).OutputSettings.Should().Be(baseline,
            "threads do not change the text, and pages the budget skipped already mark the document incomplete");
    }

    [Fact]
    public async Task ParseAsync_PageWithATextLayer_IsNotOcrd()
    {
        byte[] pdf = TestPdf.Build(Page("A born-digital page with real text."));

        var parsed = await ParseAsync(pdf);

        parsed.Content.Should().Contain("A born-digital page with real text.");
        parsed.Metadata.Should().NotContainKey(PdfParser.MetadataKeyOcrPages);
    }

    [Fact]
    public async Task ParseAsync_ScanWithOcrOff_FailsAsNoTextLayer()
    {
        byte[] scan = TestScanPdf.Build(pages: Page("Nobody will read this sentence."));

        var act = () => ParseAsync(scan, new UploadSettings { PdfOcr = false });

        await act.Should().ThrowAsync<PermanentIngestionException>().WithMessage("*[no_text_layer]*");
    }

    [Fact]
    public async Task ParseAsync_MorePagesThanTheBudget_OcrsUpToItAndWarns()
    {
        byte[] scan = TestScanPdf.Build(pages: [Page("First scanned page"), Page("Second scanned page"), Page("Third scanned page")]);

        var parsed = await ParseAsync(scan, new UploadSettings { MaxOcrPagesPerDocument = 1 });

        parsed.Content.Should().Contain("First").And.NotContain("Third");
        parsed.Metadata[PdfParser.MetadataKeyOcrPages].Should().Be("1");
        parsed.Warnings.Should().Contain(w => w.Contains("2 page(s)") && w.Contains("OCR limit"));
        parsed.Metadata[PdfParser.MetadataKeyPageErrors].Should().Be("2",
            "pages left unread make the extraction incomplete, so a reindex keeps a fuller index and retries");
    }

    [Fact]
    public async Task ParseAsync_OcrThatWouldOutrunTheDeadline_StopsAndKeepsWhatItRead()
    {
        // A one-second deadline leaves 0.6 s for OCR: a few pages of forty, never all of them,
        // however fast the machine.
        byte[] scan = TestScanPdf.Build(pages: Enumerable.Range(1, 40).Select(i => Page($"Scanned page number {i}")).ToArray());

        var parsed = await ParseAsync(scan, new UploadSettings { ParseTimeoutSeconds = 1 });

        int read = int.Parse(parsed.Metadata[PdfParser.MetadataKeyOcrPages]);
        read.Should().BeInRange(1, 39);
        parsed.Content.Should().Contain("Scanned page number 1");
        parsed.Warnings.Should().Contain(w => w.Contains("deadline"));
        parsed.Metadata[PdfParser.MetadataKeyPageErrors].Should().Be((40 - read).ToString());
    }

    [Fact]
    public async Task ParseAsync_BlankScannedPage_YieldsNoContent()
    {
        byte[] blank = TestScanPdf.Build(pages: new TestPdf.Page([]));

        var parsed = await ParseAsync(blank);

        parsed.Content.Should().BeEmpty();
        parsed.Warnings.Should().Contain("PDF contains no extractable text.");
    }

    [Fact]
    public void RenderDpi_LargePage_IsLoweredToKeepTheBitmapBounded()
    {
        // A 50-inch square page at 200 dpi would be a 10,000-pixel bitmap.
        byte[] poster = TestScanPdf.ImagePdf([(Jpeg: TinyJpeg(), Width: 1, Height: 1)], pageWidth: 3600, pageHeight: 3600);

        PdfOcr.RenderDpi(poster, 0, 200).Should().Be(PdfOcr.MaxRenderedSide / 50);
    }

    [Theory]
    [InlineData(7200)]
    [InlineData(72_000)]
    [InlineData(14_400_000)]
    public void RenderDpi_PageTooLargeToReadUnderTheCap_IsNotRendered(double side)
    {
        // Raising the resolution to a readable floor would break the pixel cap: a 1,000-inch page
        // at even 36 dpi is 36,000 pixels a side.
        byte[] poster = TestScanPdf.ImagePdf([(Jpeg: TinyJpeg(), Width: 1, Height: 1)], pageWidth: side, pageHeight: side);

        PdfOcr.RenderDpi(poster, 0, 200).Should().BeNull();
    }

    [Fact]
    public async Task ParseAsync_PageTooLargeToOcr_IsAnIncompletePageNotARender()
    {
        byte[] poster = TestScanPdf.ImagePdf([(Jpeg: TinyJpeg(), Width: 1, Height: 1)], pageWidth: 72_000, pageHeight: 72_000);

        var parsed = await ParseAsync(poster);

        parsed.Warnings.Should().Contain(w => w.Contains("too large to render"));
        parsed.Metadata[PdfParser.MetadataKeyPageErrors].Should().Be("1");
    }

    private static byte[] TinyJpeg()
    {
        using var bitmap = new SkiaSharp.SKBitmap(1, 1);
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }
}
