using Connapse.Core;
using Connapse.Ingestion.Parsers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Ingestion.Tests.Parsers;

[Trait("Category", "Unit")]
public class PdfLayoutTextTests
{
    private static PdfLayoutText.PlacedWord Word(string text, double x, double y) =>
        new(text, x, y, x * 612, x * 612 + 20, (1 - y) * 792, 10);

    private static PdfLayout.Region Region(string label, float left, float top, float right, float bottom, int order) =>
        new(label, 0.9f, left, top, right, bottom, order);

    [Fact]
    public void Compose_RegionsInModelOrder_DropsDecorationsAndKeepsOrphans()
    {
        PdfLayoutText.PlacedWord[] words =
        [
            Word("Header", 0.1, 0.03),
            Word("right", 0.7, 0.2),
            Word("left", 0.2, 0.2),
            Word("7", 0.5, 0.97),
            Word("stray", 0.5, 0.5),
        ];
        PdfLayout.Region[] regions =
        [
            Region("header", 0, 0, 1, 0.05f, 0),
            Region("text", 0.05f, 0.1f, 0.45f, 0.4f, 1),
            Region("text", 0.55f, 0.1f, 0.95f, 0.4f, 2),
            Region("number", 0.45f, 0.95f, 0.55f, 1, 3),
        ];

        string text = PdfLayoutText.Compose(words, regions, (_, _) => null);

        text.Should().Be("left\n\nright\n\nstray");
    }

    [Fact]
    public void Compose_TableRegion_UsesTheTableWriter()
    {
        PdfLayoutText.PlacedWord[] words = [Word("cell", 0.5, 0.5), Word("Intro", 0.5, 0.1)];
        PdfLayout.Region[] regions = [Region("text", 0, 0, 1, 0.2f, 0), Region("table", 0, 0.3f, 1, 0.7f, 1)];

        string text = PdfLayoutText.Compose(words, regions, (r, _) => r.Label == "table" ? new("| a | b |", 0, 0, 612, 792) : null);

        text.Should().Be("Intro\n\n| a | b |");
    }

    [Fact]
    public void Compose_TextRegionOverlappingATable_KeepsItsWordsOutOfTheTable()
    {
        // "caption" sits in both boxes; the smaller text region owns it, so the table never sees it.
        PdfLayoutText.PlacedWord[] words = [Word("caption", 0.5, 0.32), Word("cell", 0.5, 0.5)];
        PdfLayout.Region[] regions = [Region("table", 0, 0.3f, 1, 0.7f, 1), Region("text", 0.3f, 0.3f, 0.7f, 0.34f, 0)];
        var seen = new List<string>();

        string text = PdfLayoutText.Compose(words, regions, (r, owned) =>
        {
            seen.AddRange(owned.Select(w => w.Text));
            return new("| cell |", 0, 0, 612, 792);
        });

        seen.Should().Equal("cell");
        text.Should().Be("caption\n\n| cell |");
    }

    [Fact]
    public void Compose_TableCoversPartOfItsRegion_KeepsTheUncoveredWords()
    {
        // "cell" sits inside the written table's box; "Note" sits below it, in the region but not the table.
        PdfLayoutText.PlacedWord[] words = [Word("cell", 0.5, 0.4), Word("Note", 0.5, 0.65)];
        PdfLayout.Region[] regions = [Region("table", 0, 0.3f, 1, 0.7f, 0)];
        double tableBottom = (1 - 0.5) * 792, tableTop = (1 - 0.3) * 792;

        string text = PdfLayoutText.Compose(words, regions, (_, _) => new("| cell |", 0, tableBottom, 612, tableTop));

        text.Should().Be("| cell |\n\nNote");
    }

    [Fact]
    public async Task ParseAsync_LayoutLabelsEverythingDecoration_FallsBackToContentOrder()
    {
        // A one-line page: whatever the model calls it, the line must not vanish.
        var settings = Substitute.For<IOptionsMonitor<UploadSettings>>();
        settings.CurrentValue.Returns(new UploadSettings { PdfTextMode = "Layout" });
        byte[] pdf = TestPdf.Build(new TestPdf.Page([new(300, 40, "Page 12 of 40")]));

        var parsed = await new PdfParser(settings).ParseAsync(new MemoryStream(pdf), "one-line.pdf");

        parsed.Content.Should().Contain("Page 12 of 40");
    }

    [Fact]
    public void HeaderRowCount_HeadingsOnSeveralLines_CountsRowsBeforeTheNumbers()
    {
        string[][] rows =
        [
            ["", "C-CPI-U", ""], ["Income", "Index", "Percent"], ["year", "(December", "change from"],
            ["", "1999 = 100)", "year prior"], ["1947", "15.1", "X"], ["1948", "16.4", "8.6"],
        ];

        PdfTables.HeaderRowCount(rows).Should().Be(4);
    }

    [Fact]
    public void HeaderRowCount_TableOfWords_KeepsOneHeaderRow()
    {
        string[][] rows = [["Class", "Treatment"], ["Interns", "Withhold"], ["Patients", "Exempt"], ["Students", "Taxable"]];

        PdfTables.HeaderRowCount(rows).Should().Be(1);
    }

    [Fact]
    public void Lines_WordsOnOneBaseline_JoinedLeftToRight()
    {
        PdfLayoutText.PlacedWord[] words =
        [
            new("second", 0, 0, 100, 140, 700, 10), new("first", 0, 0, 50, 90, 700.5, 10), new("next", 0, 0, 50, 80, 686, 10),
        ];

        PdfLayoutText.Lines(words).Should().Be("first second\nnext");
    }

    [Fact]
    public async Task ParseAsync_LayoutMode_ReadsColumnsInOrderWithoutHeaderOrPageNumber()
    {
        var settings = Substitute.For<IOptionsMonitor<UploadSettings>>();
        settings.CurrentValue.Returns(new UploadSettings { PdfTextMode = "Layout" });
        byte[] pdf = TestPdf.Build(PdfLayoutTests.TwoColumnPage());

        var parsed = await new PdfParser(settings).ParseAsync(new MemoryStream(pdf), "report.pdf");

        string text = parsed.Content;
        text.Should().NotContain("Annual Report 2024");
        text.IndexOf("in September after a long delay", StringComparison.Ordinal).Should()
            .BeLessThan(text.IndexOf("Rainfall in the northern", StringComparison.Ordinal));
        parsed.Metadata.Should().ContainKey(PdfParser.MetadataKeyLayoutPages).WhoseValue.Should().Be("1");
        text.Split('\n').Should().NotContain(l => l.Trim() == "7");
    }

    [Fact]
    public async Task ParseAsync_ScannedTwoColumnPage_LayoutOrdersTheOcrdColumnsAndDropsTheHeader()
    {
        // The same report page as an image only: OCR reads it, and its layout regions order it (#653).
        byte[] scan = TestScanPdf.Build(200, PdfLayoutTests.TwoColumnPage());
        var settings = Substitute.For<IOptionsMonitor<UploadSettings>>();
        settings.CurrentValue.Returns(new UploadSettings { PdfTextMode = "Layout" });

        var parsed = await new PdfParser(settings).ParseAsync(new MemoryStream(scan), "scan.pdf");

        string text = parsed.Content;
        parsed.Metadata.Should().ContainKey("LayoutOcrPages");
        text.Should().NotContain("Annual Report 2024");
        text.IndexOf("long delay", StringComparison.Ordinal).Should().BeGreaterThan(0)
            .And.BeLessThan(text.IndexOf("Rainfall in the northern", StringComparison.Ordinal));
    }
}
