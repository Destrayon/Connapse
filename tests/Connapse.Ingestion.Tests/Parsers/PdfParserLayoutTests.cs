using Connapse.Core;
using Connapse.Ingestion.Parsers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Ingestion.Tests.Parsers;

[Trait("Category", "Unit")]
public class PdfParserLayoutTests
{
    private static PdfParser Parser(string mode, bool removeDecorations = true)
    {
        var settings = Substitute.For<IOptionsMonitor<UploadSettings>>();
        settings.CurrentValue.Returns(new UploadSettings { PdfTextMode = mode, PdfRemoveRepeatedHeadersAndFooters = removeDecorations });
        return new PdfParser(settings);
    }

    /// <summary>
    /// Two columns whose lines the content stream draws alternately -- left, right, left, right --
    /// as many two-column layouts do.
    /// </summary>
    private static byte[] TwoColumns() => TestPdf.Build(new TestPdf.Page(
    [
        new(72, 700, "Alpha one opens the left column."), new(330, 700, "Delta one opens the right column."),
        new(72, 686, "Alpha two continues the left."), new(330, 686, "Delta two continues the right."),
        new(72, 672, "Alpha three ends the left column."), new(330, 672, "Delta three ends the right column."),
    ]));

    [Fact]
    public async Task ParseAsync_XYCut_ReadsEachColumnToItsEnd()
    {
        var parsed = await Parser("XYCut").ParseAsync(new MemoryStream(TwoColumns()), "columns.pdf");

        string text = parsed.Content;
        text.IndexOf("Alpha three", StringComparison.Ordinal).Should()
            .BeLessThan(text.IndexOf("Delta one", StringComparison.Ordinal), "the left column is read before the right one starts");
    }

    [Fact]
    public async Task ParseAsync_Raw_InterleavesTheColumns()
    {
        // The behaviour XYCut replaces, pinned so the comparison above means something.
        var parsed = await Parser("Raw").ParseAsync(new MemoryStream(TwoColumns()), "columns.pdf");

        parsed.Content.IndexOf("Delta one", StringComparison.Ordinal).Should()
            .BeLessThan(parsed.Content.IndexOf("Alpha two", StringComparison.Ordinal));
    }

    private static readonly string[] Bodies =
    [
        "Revenue grew in every region during the quarter.",
        "Hiring slowed while attrition stayed flat.",
        "The new warehouse opened two weeks early.",
        "Cloud costs fell after the storage migration.",
    ];

    private static byte[] ReportWithRunningHeader(int pages) => TestPdf.Build(Enumerable.Range(0, pages).Select(i =>
        new TestPdf.Page(
        [
            new(72, 760, "ACME Corporation Confidential Quarterly Report"),
            new(72, 600, Bodies[i]),
            new(300, 40, $"Page {i + 1}"),
        ])).ToArray());

    [Theory]
    [InlineData("ContentOrder")]
    [InlineData("XYCut")]
    public async Task ParseAsync_RunningHeaderOnEveryPage_IsRemovedAndTheBodyKept(string mode)
    {
        var parsed = await Parser(mode).ParseAsync(new MemoryStream(ReportWithRunningHeader(4)), "report.pdf");

        parsed.Content.Should().NotContain("ACME Corporation Confidential");
        parsed.Content.Split('\n').Select(l => l.Trim()).Should().NotContain(l => l.StartsWith("Page ") && l.Length <= 7,
            "the page-number footer repeats on every page");
        foreach (string body in Bodies)
            parsed.Content.Should().Contain(body);
    }

    [Theory]
    [InlineData("ContentOrder")]
    [InlineData("XYCut")]
    public async Task ParseAsync_TemplatedPagesThatDifferByAWord_AreNotEmptied(string mode)
    {
        // The same sentence in the same place on every page, one word changed: body text, not a
        // running header, and the page would be blank without it.
        byte[] pdf = TestPdf.Build(Enumerable.Range(1, 4).Select(i =>
            new TestPdf.Page([new(72, 600, $"Inspection record for unit {i} passed all checks.")])).ToArray());

        var parsed = await Parser(mode).ParseAsync(new MemoryStream(pdf), "forms.pdf");

        for (int i = 1; i <= 4; i++)
            parsed.Content.Should().Contain($"unit {i} passed");
    }

    [Theory]
    [InlineData("ContentOrder")]
    [InlineData("XYCut")]
    public async Task ParseAsync_RemovalTurnedOff_KeepsTheHeader(string mode)
    {
        var parsed = await Parser(mode, removeDecorations: false)
            .ParseAsync(new MemoryStream(ReportWithRunningHeader(4)), "report.pdf");

        parsed.Content.Should().Contain("ACME Corporation Confidential");
    }

    [Theory]
    [InlineData("ContentOrder")]
    [InlineData("XYCut")]
    public async Task ParseAsync_TwoPages_AreTooFewToJudgeAHeader(string mode)
    {
        var parsed = await Parser(mode).ParseAsync(new MemoryStream(ReportWithRunningHeader(2)), "report.pdf");

        parsed.Content.Should().Contain("ACME Corporation Confidential");
    }

    [Theory]
    [InlineData("ContentOrder")]
    [InlineData("XYCut")]
    [InlineData("Raw")]
    public async Task ParseAsync_PageThatThrows_IsCountedApartFromEmptyPages(string mode)
    {
        byte[] pdf = TestPdf.Build(
            new TestPdf.Page([new(72, 700, "The first page reads fine.")]),
            new TestPdf.Page([new(72, 700, "The second page cannot be built.")], RawPrefix: "/CS0 cs 0.5 sc", ColorSpaces: "/CS0 /Bogus"));

        var parsed = await Parser(mode).ParseAsync(new MemoryStream(pdf), "broken.pdf");

        parsed.Content.Should().Contain("The first page reads fine.");
        parsed.Metadata.Should().ContainKey(PdfParser.MetadataKeyPageErrors).WhoseValue.Should().Be("1");
    }

    [Fact]
    public async Task ParseAsync_UnknownMode_FallsBackToTheDefault()
    {
        var parsed = await Parser("NoSuchMode").ParseAsync(new MemoryStream(TwoColumns()), "columns.pdf");

        parsed.Content.Should().Contain("Alpha one opens the left column.");
    }
}
