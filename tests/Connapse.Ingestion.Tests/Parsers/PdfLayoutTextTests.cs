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

        string text = PdfLayoutText.Compose(words, regions, _ => null);

        text.Should().Be("left\n\nright\n\nstray");
    }

    [Fact]
    public void Compose_TableRegion_UsesTheTableWriter()
    {
        PdfLayoutText.PlacedWord[] words = [Word("cell", 0.5, 0.5), Word("Intro", 0.5, 0.1)];
        PdfLayout.Region[] regions = [Region("text", 0, 0, 1, 0.2f, 0), Region("table", 0, 0.3f, 1, 0.7f, 1)];

        string text = PdfLayoutText.Compose(words, regions, r => r.Label == "table" ? "| a | b |" : null);

        text.Should().Be("Intro\n\n| a | b |");
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
}
