using Connapse.Core;
using Connapse.Ingestion.Parsers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Ingestion.Tests.Parsers;

[Trait("Category", "Unit")]
public class PdfTableStructureTests
{
    // Boxes in PDF points (left, bottom, right, top) for a 2x3 grid of 100x20 cells from (0, 100).
    private static (double, double, double, double) Box(int row, int column, int columnSpan = 1) =>
        (column * 100, 80 - row * 20, (column + columnSpan) * 100, 100 - row * 20);

    [Fact]
    public void Build_HeadAndColumnSpan_PlacesCellsInTheGrid()
    {
        string[] tokens =
        [
            "<thead>", "<tr>", "<td", " colspan=\"2\"", ">", "</td>", "<td></td>", "</tr>", "</thead>",
            "<tbody>", "<tr>", "<td></td>", "<td></td>", "<td></td>", "</tr>", "</tbody>",
        ];
        var boxes = new[] { Box(0, 0, 2), Box(0, 2), Box(1, 0), Box(1, 1), Box(1, 2) }.ToList();

        PdfTableStructure.Structure structure = PdfTableStructure.Build(tokens, boxes)!;

        structure.Rows.Should().Be(2);
        structure.Columns.Should().Be(3);
        structure.HeaderRows.Should().Be(1);
        structure.Cells[0].Should().Match<PdfTableStructure.Cell>(c => c.ColumnSpan == 2 && c.Column == 0);
        structure.Cells[1].Column.Should().Be(2);
    }

    [Fact]
    public void Build_RowSpan_SkipsTheOccupiedPositionBelow()
    {
        string[] tokens =
        [
            "<tr>", "<td", " rowspan=\"2\"", ">", "</td>", "<td></td>", "</tr>",
            "<tr>", "<td></td>", "</tr>",
        ];
        var boxes = new[] { Box(0, 0), Box(0, 1), Box(1, 1) }.ToList();

        PdfTableStructure.Structure structure = PdfTableStructure.Build(tokens, boxes)!;

        structure.Cells[2].Should().Match<PdfTableStructure.Cell>(c => c.Row == 1 && c.Column == 1);
    }

    [Fact]
    public void Fill_WordsGoToTheirCellsAndSpansRepeat()
    {
        string[] tokens = ["<tr>", "<td", " colspan=\"2\"", ">", "</td>", "</tr>", "<tr>", "<td></td>", "<td></td>", "</tr>"];
        var structure = PdfTableStructure.Build(tokens, [Box(0, 0, 2), Box(1, 0), Box(1, 1)])!;
        (string, double, double, int)[] words =
        [
            ("Income", 50, 90, 0), ("2022", 150, 90, 1), ("Ohio", 50, 70, 2), ("4,213", 150, 70, 3),
            ("stray", 400, 70, 4), // outside every cell: goes to the nearest, not lost
        ];

        IReadOnlyList<IReadOnlyList<string>> grid = PdfTableStructure.Fill(structure, words);

        grid[0].Should().Equal("Income 2022", "Income 2022");
        grid[1].Should().Equal("Ohio", "4,213 stray");
    }

    [Fact]
    public void Dictionary_MergesTheOpenCellTokenAndAddsStartAndEnd()
    {
        string[] dictionary = PdfTableStructure.Dictionary("<tr>\n<td>\n</td>");

        dictionary.Should().Equal("sos", "<tr>", "</td>", "<td></td>", "eos");
    }

    private static PdfLayoutText.PlacedWord Word(string text, double x, double baseline) =>
        new(text, 0, 0, x - 5, x + 5, baseline, 8);

    [Fact]
    public void HasStackedRows_ColumnOfNumbersInOneCell_IsStacked()
    {
        // One cell per column, each holding five rows' numbers: rows that ran together.
        var cells = new[] { (0.0, 0.0, 100.0, 100.0), (100.0, 0.0, 200.0, 100.0) };
        var words = Enumerable.Range(0, 5).SelectMany(i => new[] { Word($"{i}.5", 50, 90 - i * 15), Word($"{i}.25", 150, 90 - i * 15) }).ToList();

        PdfLayoutText.HasStackedRows(cells, words).Should().BeTrue();
    }

    [Fact]
    public void HasStackedRows_WrappedProseCell_IsNotStacked()
    {
        var cells = new[] { (0.0, 0.0, 200.0, 100.0) };
        var words = new[] { "Taxable if paid", "$100 or more", "in cash in a year" }
            .SelectMany((line, i) => line.Split(' ').Select((w, k) => Word(w, 10 + k * 20, 90 - i * 15))).ToList();

        PdfLayoutText.HasStackedRows(cells, words).Should().BeFalse();
    }

    [Fact]
    public void FromStructure_BoxesAwayFromTheWords_Rejected()
    {
        // The model's cells sit at the top of the region; the words are 60 points below them.
        string[] tokens = ["<tr>", "<td></td>", "<td></td>", "</tr>", "<tr>", "<td></td>", "<td></td>", "</tr>"];
        var structure = PdfTableStructure.Build(tokens, [(0, 180, 100, 200), (100, 180, 200, 200), (0, 160, 100, 180), (100, 160, 200, 180)])!;
        PdfLayoutText.PlacedWord[] words = [Word("Ohio", 50, 100), Word("4,213", 150, 100), Word("Iowa", 50, 85), Word("812", 150, 85)];

        PdfLayoutText.FromStructure(structure, words, 0, 0, 200, 200).Should().BeNull();
    }

    [Fact]
    public void ToMarkdown_HeadingSpanningBothHeaderRows_WrittenOnce()
    {
        string[][] rows = [["Region", "2021"], ["Region", "Estimate"], ["Ohio", "4,213"]];

        string markdown = PdfTables.ToMarkdown(rows, headerRows: 2);

        markdown.Split('\n')[0].Should().Be("| Region | 2021 Estimate |");
    }

    [Fact]
    public async Task ParseAsync_BorderlessTable_LayoutModeWritesItsRowsAsMarkdown()
    {
        // A report page with a borderless table: no ruling lines for Tabula, so the region is read by
        // the structure model.
        var lines = new List<TestPdf.Line>
        {
            new(72, 740, "Table 4. Water supplied by district, 2024"),
            new(72, 712, "District"), new(220, 712, "Households"), new(340, 712, "Megalitres"), new(460, 712, "Change"),
        };
        string[][] rows =
        [
            ["Northfield", "41,200", "6,310", "-2.1%"], ["Riverside", "18,950", "2,874", "+0.4%"],
            ["Mill Creek", "7,430", "1,102", "-5.8%"], ["Eastgate", "22,610", "3,390", "-1.2%"],
            ["Harbour", "12,080", "1,815", "+1.9%"], ["Westvale", "9,340", "1,407", "-0.6%"],
        ];
        for (int i = 0; i < rows.Length; i++)
        {
            double y = 694 - i * 16;
            lines.Add(new(72, y, rows[i][0]));
            lines.Add(new(220, y, rows[i][1]));
            lines.Add(new(340, y, rows[i][2]));
            lines.Add(new(460, y, rows[i][3]));
        }
        lines.Add(new(72, 560, "Consumption fell in five of the six districts as meters were replaced across the county."));
        var settings = Substitute.For<IOptionsMonitor<UploadSettings>>();
        settings.CurrentValue.Returns(new UploadSettings { PdfTextMode = "Layout" });

        var parsed = await new PdfParser(settings).ParseAsync(new MemoryStream(TestPdf.Build(new TestPdf.Page(lines))), "water.pdf");

        parsed.Content.Should().Contain("| Mill Creek | 7,430 | 1,102 | -5.8% |");
        parsed.Metadata.Should().ContainKey("LayoutTables", "the table went through the structure model");
    }
}
