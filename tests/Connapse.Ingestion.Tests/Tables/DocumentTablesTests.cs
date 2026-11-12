using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Parsers;
using Connapse.Ingestion.Tables;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Tables;

[Trait("Category", "Unit")]
public class DocumentTablesTests
{
    private static readonly string[][] Sales =
    [
        ["Region", "Q1", "Q2"],
        ["North", "12", "15"],
        ["South", "9", "11"],
    ];

    private static async Task<IReadOnlyList<FoundTable>> FindAsync(IDocumentParser parser, byte[] file, string name)
    {
        var parsed = await parser.ParseAsync(new MemoryStream(file), name);
        return DocumentTables.Find(parsed.Content, Path.GetExtension(name));
    }

    private static void ShouldBeSales(FoundTable table)
    {
        table.Header.Should().Equal("Region", "Q1", "Q2");
        table.Rows.Should().HaveCount(2);
        table.Rows[0].Should().Equal("North", "12", "15");
        DocumentTables.Reject(table).Should().BeNull();
    }

    // The parsed PDF text, as PdfParser writes it, so this stays fast; PdfParserTableTests parses a real ruled PDF.
    [Fact]
    public void Find_PdfTableAfterAPageMarker_IsFoundOnThatPage()
    {
        string text = "--- Page 1 ---\nCover page with no tables.\n\n--- Page 2 ---\nResults by region\n\n" +
                      "| Region | Q1 | Q2 |\n| --- | --- | --- |\n| North | 12 | 15 |\n| South | 9 | 11 |\n";

        var tables = DocumentTables.Find(text, ".pdf");

        tables.Should().ContainSingle();
        tables[0].Page.Should().Be(2);
        tables[0].Number.Should().Be(1);
        ShouldBeSales(tables[0]);
    }

    [Fact]
    public async Task Find_WordTables_AreNumberedInOrderWithNoPage()
    {
        byte[] docx = TestOffice.Docx(Sales, [["Name", "Role"], ["Ana", "Lead"], ["Bo", "Dev"]]);

        var tables = await FindAsync(new OfficeParser(), docx, "report.docx");

        tables.Should().HaveCount(2);
        tables.Select(t => t.Number).Should().Equal(1, 2);
        tables.Should().OnlyContain(t => t.Page == null);
        ShouldBeSales(tables[0]);
        tables[1].Header.Should().Equal("Name", "Role");
    }

    [Fact]
    public async Task Find_PowerPointTable_IsFoundOnItsSlide()
    {
        byte[] pptx = TestOffice.Pptx([["Only", "Header"], ["a", "b"], ["c", "d"]], Sales);

        var tables = await FindAsync(new OfficeParser(), pptx, "deck.pptx");

        tables.Should().HaveCount(2);
        tables[1].Page.Should().Be(2);
        ShouldBeSales(tables[1]);
    }

    [Fact]
    public async Task Find_ExcelSheets_EachBlockIsATableOnItsSheet()
    {
        byte[] xlsx = TestOffice.Xlsx(
            ("Summary", [["Region", "Q1", "Q2"], ["North", 12, 15], ["South", 9, 11]], false),
            ("Hidden", [["Secret", "Value"], ["a", 1], ["b", 2]], true),
            ("Staff", [["Title of the sheet"], [null], ["Name", "Started"], ["Ana", new DateTime(2024, 3, 1)], ["Bo", new DateTime(2025, 1, 15)]], false));

        var tables = await FindAsync(new OfficeParser(), xlsx, "book.xlsx");

        tables.Should().HaveCount(2, "the hidden sheet is skipped and a lone title is not a table");
        tables[0].Page.Should().Be(1);
        ShouldBeSales(tables[0]);
        tables[1].Page.Should().Be(3);
        tables[1].Header.Should().Equal("Name", "Started");
        tables[1].Rows[0].Should().Equal("Ana", "2024-03-01");
    }

    [Fact]
    public async Task Find_MarkdownTable_IsFoundAndFencedTableIsNot()
    {
        string md = "# Notes\n\n| Region | Q1 | Q2 |\n|---|---|---|\n| North | 12 | 15 |\n| South | 9 | 11 |\n\n" +
                    "```\n| Not | A |\n|---|---|\n| real | table |\n| in | code |\n```\n";

        var tables = await FindAsync(new TextParser(), Encoding.UTF8.GetBytes(md), "notes.md");

        tables.Should().ContainSingle();
        tables[0].Page.Should().BeNull();
        ShouldBeSales(tables[0]);
    }

    [Fact]
    public async Task Find_HtmlTable_IsFound()
    {
        string html = "<html><body><h1>Report</h1><p>Sales follow.</p><table><thead><tr><th>Region</th><th>Q1</th><th>Q2</th></tr></thead>" +
                      "<tbody><tr><td>North</td><td>12</td><td>15</td></tr><tr><td>South</td><td>9</td><td>11</td></tr></tbody></table>" +
                      "<p>That is all.</p></body></html>";

        var tables = await FindAsync(new HtmlParser(), Encoding.UTF8.GetBytes(html), "report.html");

        tables.Should().ContainSingle();
        ShouldBeSales(tables[0]);
    }

    [Fact]
    public void Find_TextSpan_IsTheTableItself()
    {
        string text = "Intro.\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |\n\nAfter.";

        FoundTable table = DocumentTables.Find(text, ".md").Single();

        text.Substring(table.Start, table.Length).Should().Be("| A | B |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |");
        table.Text.Should().Be(text.Substring(table.Start, table.Length));
    }

    [Fact]
    public void Cells_EscapedPipe_StaysInsideItsCell()
    {
        DocumentTables.Cells(@"| a \| b | | c |").Should().Equal("a | b", null, "c");
    }

    [Theory]
    [InlineData("| A |\n| --- |\n| 1 |\n| 2 |", "column")]
    [InlineData("| A | B |\n| --- | --- |\n| 1 | 2 |", "data row")]
    [InlineData("|  |  |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |", "headers")]
    [InlineData("| A | B |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 | 5 |", "row of 3 cells")]
    [InlineData("| A | B | C |\n| --- | --- | --- |\n| 1 |  |  |\n|  |  | 2 |", "cells filled")]
    public void Reject_UnfitTable_SaysWhy(string markdown, string reason)
    {
        FoundTable table = DocumentTables.Find(markdown, ".md").Single();

        DocumentTables.Reject(table).Should().Contain(reason);
    }

    [Fact]
    public void HeaderHash_SameHeaderInAnotherCase_IsTheSame()
    {
        FoundTable a = DocumentTables.Find("| Region | Q1 |\n|---|---|\n| x | 1 |", ".md").Single();
        FoundTable b = DocumentTables.Find("| region | q1 |\n|---|---|\n| y | 2 |", ".md").Single();
        FoundTable c = DocumentTables.Find("| Region | Q2 |\n|---|---|\n| x | 1 |", ".md").Single();

        a.HeaderHash.Should().Be(b.HeaderHash);
        a.HeaderHash.Should().NotBe(c.HeaderHash);
    }

    [Theory]
    [InlineData("report.pdf", 3, 2, "report, page 3, table 2")]
    [InlineData("notes.docx", null, 1, "notes, table 1")]
    public void Name_IncludesPageAndNumber(string document, int? page, int number, string expected)
    {
        var table = new FoundTable(number, page, 0, 0, "", ["A"], []);

        DocumentTables.Name(document, table).Should().Be(expected);
    }

    [Fact]
    public async Task ToLoad_GuessesEachColumnsType()
    {
        FoundTable table = DocumentTables.Find("| Region | Units | Shipped |\n|---|---|---|\n| North | 12 | 2024-03-01 |\n| South |  | 2024-04-02 |", ".md").Single();
        var documentId = Guid.NewGuid();

        TableLoad load = DocumentTables.ToLoad(table, "t", documentId);

        load.DocumentId.Should().Be(documentId);
        load.Columns.Select(c => c.Type).Should().Equal(TableColumnType.Text, TableColumnType.WholeNumber, TableColumnType.Date);
        List<IReadOnlyList<object?>> rows = [];
        await foreach (var row in load.Rows)
            rows.Add(row);
        rows.Should().HaveCount(2);
        rows[0].Should().Equal("North", 12L, new DateOnly(2024, 3, 1));
        rows[1][1].Should().BeNull();
    }

    [Fact]
    public void ToLoad_SetsThePlacement()
    {
        FoundTable table = DocumentTables.Find("--- Page 4 ---\n| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |", ".pdf").Single();

        DocumentTables.ToLoad(table, "t", Guid.NewGuid()).Placement.Should().Be(new TablePlacement(4, 1, table.HeaderHash));
    }

    private const string TwoTables =
        "Intro text.\n\n| Region | Q1 |\n|---|---|\n| North | 12 |\n| South | 9 |\n\nMiddle text.\n\n| Name | Role |\n|---|---|\n| Ada | Lead |\n| Lin | Dev |\n\nEnd.";

    private static ChunkInfo Chunk(string content, int start) => new(content, 0, 1, start, start + content.Length, []);

    [Fact]
    public void Spans_WholeDocumentInOneChunk_CoversEachTableExactly()
    {
        var tables = DocumentTables.Find(TwoTables, ".md");

        var spans = DocumentTables.Spans(tables, [Chunk(TwoTables, 0)]);

        spans.Should().HaveCount(2);
        spans.Select(s => TwoTables.Substring(s.Start, s.Length)).Should().Equal(tables[0].Text, tables[1].Text);
        spans.Select(s => s.Table).Should().Equal(0, 1);
    }

    [Fact]
    public void Spans_TableSplitAcrossChunksWithACutRow_EachChunkLinksItsPart()
    {
        var tables = DocumentTables.Find(TwoTables, ".md");
        int cut = TwoTables.IndexOf("| South", StringComparison.Ordinal) + 4;
        string first = TwoTables[..cut];
        string second = TwoTables[cut..];

        var spans = DocumentTables.Spans(tables, [Chunk(first, 0), Chunk(second, cut)]);

        spans.Where(s => s.Chunk == 0).Should().ContainSingle().Which.Table.Should().Be(0);
        var head = spans.Single(s => s.Chunk == 0);
        first.Substring(head.Start, head.Length).Should().StartWith("| Region").And.EndWith("| So");
        var tail = spans.Where(s => s.Chunk == 1).ToList();
        tail.Select(s => s.Table).Should().Equal(0, 1);
        second.Substring(tail[0].Start, tail[0].Length).Should().Be("uth | 9 |");
    }

    [Fact]
    public void Spans_SameTableTwice_EachChunkLinksTheNearestCopy()
    {
        string table = "| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |";
        string doc = table + "\n\ntext between\n\n" + table;
        var tables = DocumentTables.Find(doc, ".md");
        int second = doc.LastIndexOf("| A", StringComparison.Ordinal);

        var spans = DocumentTables.Spans(tables, [Chunk(doc[..second], 0), Chunk(doc[second..], second)]);

        spans.Select(s => (s.Chunk, s.Table)).Should().Equal((0, 0), (1, 1));
    }

    [Fact]
    public void Spans_ChunkWithoutTableRows_HasNone()
    {
        var tables = DocumentTables.Find(TwoTables, ".md");

        DocumentTables.Spans(tables, [Chunk("Intro text.\n|---|---|\nnot a table", 0)]).Should().BeEmpty();
    }
}
