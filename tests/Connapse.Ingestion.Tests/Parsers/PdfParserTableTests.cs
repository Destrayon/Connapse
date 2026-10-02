using Connapse.Core;
using Connapse.Ingestion.Parsers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Ingestion.Tests.Parsers;

[Trait("Category", "Unit")]
public class PdfParserTableTests
{
    private static PdfParser Parser(string tableMode)
    {
        var settings = Substitute.For<IOptionsMonitor<UploadSettings>>();
        settings.CurrentValue.Returns(new UploadSettings { PdfTableMode = tableMode });
        return new PdfParser(settings);
    }

    /// <summary>A heading, a 3x3 table ruled at 20pt rows and 100pt columns, then a paragraph.</summary>
    private static byte[] ReportWithRuledTable()
    {
        var grid = new System.Text.StringBuilder("0.5 w\n");
        for (int y = 640; y <= 700; y += 20) grid.Append($"72 {y} m 372 {y} l S\n");
        for (int x = 72; x <= 372; x += 100) grid.Append($"{x} 640 m {x} 700 l S\n");

        return TestPdf.Build(new TestPdf.Page(
        [
            new(72, 740, "Results by region"),
            new(76, 685, "Region"), new(176, 685, "Q1"), new(276, 685, "Q2"),
            new(76, 665, "North"), new(176, 665, "12"), new(276, 665, "15"),
            new(76, 645, "South"), new(176, 645, "9"), new(276, 645, "11"),
            new(72, 600, "Text after the table."),
        ], RawPrefix: grid.ToString()));
    }

    [Fact]
    public async Task ParseAsync_RuledTable_BecomesAMarkdownTableInPlace()
    {
        var parsed = await Parser("Ruled").ParseAsync(new MemoryStream(ReportWithRuledTable()), "report.pdf");

        parsed.Content.Should().Contain("| Region | Q1 | Q2 |");
        parsed.Content.Should().Contain("| --- | --- | --- |");
        parsed.Content.Should().Contain("| North | 12 | 15 |");
        parsed.Content.Should().Contain("| South | 9 | 11 |");

        int heading = parsed.Content.IndexOf("Results by region", StringComparison.Ordinal);
        int table = parsed.Content.IndexOf("| Region |", StringComparison.Ordinal);
        int after = parsed.Content.IndexOf("Text after the table.", StringComparison.Ordinal);
        heading.Should().BeLessThan(table);
        table.Should().BeLessThan(after);
    }

    [Fact]
    public async Task ParseAsync_RuledTable_CellsAreNotAlsoRepeatedAsText()
    {
        // The DOCX parser once emitted its tables twice; the cells belong in the table only.
        var parsed = await Parser("Ruled").ParseAsync(new MemoryStream(ReportWithRuledTable()), "report.pdf");

        parsed.Content.Split("North").Length.Should().Be(2, "\"North\" appears once, inside the table");
    }

    [Fact]
    public async Task ParseAsync_TablesOff_LeavesTheTableAsText()
    {
        var parsed = await Parser("Off").ParseAsync(new MemoryStream(ReportWithRuledTable()), "report.pdf");

        parsed.Content.Should().NotContain("| --- |");
        parsed.Content.Should().Contain("North");
    }

    [Fact]
    public async Task ParseAsync_StreamMode_DoesNotTurnProseIntoATable()
    {
        byte[] prose = TestPdf.Build(new TestPdf.Page(
        [
            new(72, 700, "Revenue grew in every region during the quarter, led by the north."),
            new(72, 686, "Hiring slowed while attrition stayed flat across the company."),
            new(72, 672, "The new warehouse opened two weeks early and under budget."),
            new(72, 658, "Cloud costs fell after the storage migration finished in May."),
        ]));

        var parsed = await Parser("RuledAndStream").ParseAsync(new MemoryStream(prose), "memo.pdf");

        parsed.Content.Should().NotContain("| --- |");
        parsed.Content.Should().Contain("Cloud costs fell after the storage migration");
    }
}
