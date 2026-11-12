using Connapse.Core;
using Connapse.Ingestion.Parsers;
using Connapse.Ingestion.Tables;
using FluentAssertions;
using static Connapse.Ingestion.Tests.Tables.XlsxFixture;

namespace Connapse.Ingestion.Tests.Tables;

[Trait("Category", "Unit")]
public class XlsxTableReaderTests
{
    private static async Task<(IReadOnlyList<TableLoad> Tables, IReadOnlyList<string> Warnings, List<List<IReadOnlyList<object?>>> Rows)> ReadAsync(
        MemoryStream file, string fileName = "book.xlsx")
    {
        using TableFile read = await TableFiles.ReadAsync(file, fileName, documentId: null);
        List<List<IReadOnlyList<object?>>> rows = [];
        foreach (TableLoad load in read.Tables)
        {
            List<IReadOnlyList<object?>> sheet = [];
            await foreach (IReadOnlyList<object?> row in load.Rows)
                sheet.Add(row);
            rows.Add(sheet);
        }
        return (read.Tables, read.Warnings, rows);
    }

    private static MemoryStream Sheet(params string[] rows) => Build(new SheetSpec("Sales", $"<sheetData>{string.Concat(rows)}</sheetData>"));

    [Fact]
    public async Task Read_TypedSheet_NamesTheTableAfterFileAndSheetAndTypesEveryColumn()
    {
        var (tables, warnings, rows) = await ReadAsync(Sheet(
            Row(1, "id", "name", "amount", "day", "paid"),
            Row(2, 1, "Ann", 2.5, new DateOnly(2024, 1, 31), true),
            Row(3, 2, "Bob", 10, new DateOnly(2024, 2, 1), false)));

        TableLoad table = tables.Should().ContainSingle().Subject;
        table.Name.Should().Be("book - Sales");
        table.Columns.Select(c => (c.Name, c.Type)).Should().Equal(
            ("id", TableColumnType.WholeNumber), ("name", TableColumnType.Text), ("amount", TableColumnType.Number),
            ("day", TableColumnType.Date), ("paid", TableColumnType.Boolean));
        rows[0].Should().HaveCount(2);
        rows[0][0].Should().Equal(1L, "Ann", 2.5m, new DateOnly(2024, 1, 31), true);
        rows[0][1].Should().Equal(2L, "Bob", 10m, new DateOnly(2024, 2, 1), false);
        table.NeedsReview.Should().BeFalse();
        warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_TitleRowAboveTheHeader_KeepsItInTheDescriptionWithoutAFlag()
    {
        var (tables, _, rows) = await ReadAsync(Sheet(
            Row(1, "Quarterly sales report"),
            Row(2, "region", "units", "price"),
            Row(3, "East", 3, 1.5),
            Row(4, "West", 4, 2.5)));

        tables[0].Columns.Select(c => c.Name).Should().Equal("region", "units", "price");
        rows[0].Should().HaveCount(2);
        tables[0].Description.Should().Be("Text on the sheet outside the table: Quarterly sales report");
        tables[0].NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task Read_TitleUnitsAndFootnote_AllGoIntoTheDescription()
    {
        var (tables, _, _) = await ReadAsync(Sheet(
            Row(1, "Q3 Regional Sales"),
            Row(2, "All figures in USD thousands"),
            Row(4, "region", "jul", "aug"),
            Row(5, "East", 3, 4),
            Row(6, "West", 5, 6),
            Row(8, "September figures are preliminary.")));

        tables.Should().ContainSingle().Which.Description.Should().Be(
            "Text on the sheet outside the table: Q3 Regional Sales / All figures in USD thousands / September figures are preliminary.");
    }

    [Fact]
    public async Task Read_TwoRowMergedHeader_JoinsBothRowsIntoEachName()
    {
        var (tables, warnings, rows) = await ReadAsync(Build(new SheetSpec("Staff",
            "<sheetData>" + Row(1, "dept", "2025", null, "2026", null) + Row(2, null, "hires", "leavers", "hires", "leavers") +
            Row(3, "Eng", 42, 11, 37, 14) + Row(4, "Sales", 25, 18, 30, 21) +
            "</sheetData><mergeCells count=\"3\"><mergeCell ref=\"A1:A2\"/><mergeCell ref=\"B1:C1\"/><mergeCell ref=\"D1:E1\"/></mergeCells>")));

        tables[0].Columns.Select(c => (c.Name, c.Type)).Should().Equal(
            ("dept", TableColumnType.Text), ("2025 hires", TableColumnType.WholeNumber), ("2025 leavers", TableColumnType.WholeNumber),
            ("2026 hires", TableColumnType.WholeNumber), ("2026 leavers", TableColumnType.WholeNumber));
        rows[0].Should().HaveCount(2);
        rows[0][0].Should().Equal("Eng", 42L, 11L, 37L, 14L);
        tables[0].NeedsReview.Should().BeFalse();
        warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_MergedDataCells_FillEveryRowTheMergeCovers()
    {
        var (tables, _, rows) = await ReadAsync(Build(new SheetSpec("Sales",
            "<sheetData>" + Row(1, "region", "store", "units") + Row(2, "East", "A", 1) + Row(3, null, "B", 2) + Row(4, "West", "C", 3) +
            "</sheetData><mergeCells count=\"1\"><mergeCell ref=\"A2:A3\"/></mergeCells>")));

        rows[0].Select(r => r[0]).Should().Equal("East", "East", "West");
        tables[0].NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task Read_MergedHeader_LoadsButNeedsReview()
    {
        var (tables, warnings, rows) = await ReadAsync(Build(new SheetSpec("Sales",
            "<sheetData>" + Row(1, "sales", null, "region") + Row(2, 1, 2, "East") + Row(3, 3, 4, "West") +
            "</sheetData><mergeCells count=\"1\"><mergeCell ref=\"A1:B1\"/></mergeCells>")));

        tables[0].NeedsReview.Should().BeTrue();
        tables[0].Columns.Select(c => c.Name).Should().Equal("sales", "sales_1", "region");
        rows[0].Should().HaveCount(2);
        warnings.Should().ContainSingle().Which.Should().Contain("needs review").And.Contain("merged cells");
    }

    [Fact]
    public async Task Read_FormulaSavedWithoutAResult_KeepsItsRowWithTheCellEmptyAndFlagsIt()
    {
        var (tables, warnings, rows) = await ReadAsync(Sheet(
            Row(1, "a", "b", "total"),
            "<row r=\"2\"><c r=\"A2\"><v>1</v></c><c r=\"B2\"><v>2</v></c><c r=\"C2\"><f>A2+B2</f><v>3</v></c></row>",
            "<row r=\"3\"><c r=\"A3\"><v>4</v></c><c r=\"B3\"><v>5</v></c><c r=\"C3\"><f>A3+B3</f><v></v></c></row>"));

        rows[0].Should().HaveCount(2);
        rows[0][0].Should().Equal(1L, 2L, 3L);
        rows[0][1].Should().Equal(4L, 5L, null);
        tables[0].Columns[2].Type.Should().Be(TableColumnType.WholeNumber);
        tables[0].NeedsReview.Should().BeTrue();
        tables[0].Rejects.Should().ContainSingle().Which.Should().Be(
            new TableReject(3, 3, "=A3+B3", "is a formula the file was saved without a result for, so the cell is empty in the table"));
        warnings.Should().ContainSingle().Which.Should().Contain("empty in the table").And.Contain("Open and save the file in Excel");
    }

    [Fact]
    public async Task Read_StringFormulaWithAnEmptyResult_IsAnEmptyCellNotAMissingResult()
    {
        var (tables, _, rows) = await ReadAsync(Sheet(
            Row(1, "a", "note"),
            "<row r=\"2\"><c r=\"A2\"><v>1</v></c><c r=\"B2\" t=\"str\"><f>\"\"</f><v></v></c></row>"));

        rows[0].Should().ContainSingle().Which.Should().Equal(1L, null);
        tables[0].Rejects.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_ExcelError_KeepsItsRowWithTheCellEmptyAndListsTheError()
    {
        var (tables, warnings, rows) = await ReadAsync(Sheet(
            Row(1, "a", "ratio"),
            Row(2, 1, 0.5),
            "<row r=\"3\"><c r=\"A3\"><v>2</v></c><c r=\"B3\" t=\"e\"><f>A3/0</f><v>#DIV/0!</v></c></row>"));

        rows[0].Should().HaveCount(2);
        rows[0][1].Should().Equal(2L, null);
        tables[0].Columns[1].Type.Should().Be(TableColumnType.Number);
        tables[0].Rejects.Should().ContainSingle().Which.Should().Be(
            new TableReject(3, 2, "#DIV/0!", "is the Excel error #DIV/0!, so the cell is empty in the table"));
        tables[0].NeedsReview.Should().BeTrue();
        warnings.Should().ContainSingle().Which.Should().Contain("Excel error");
    }

    [Fact]
    public async Task Read_CellsBesideTheHeader_LoadIntoAColumnWithAGeneratedName()
    {
        var (tables, warnings, rows) = await ReadAsync(Sheet(
            Row(1, "project", "owner", "status"),
            Row(2, "Billing", "Omar", "At risk", "blocked on legal"),
            Row(3, "Website", "Dana", "On track"),
            Row(4, "Warehouse", "Lee", "At risk", "waiting on budget")));

        tables[0].Columns.Select(c => c.Name).Should().Equal("project", "owner", "status", "column3");
        rows[0].Select(r => r[3]).Should().Equal(new object?[] { "blocked on legal", null, "waiting on budget" });
        tables[0].NeedsReview.Should().BeTrue();
        warnings.Should().ContainSingle().Which.Should().Contain("no name");
    }

    [Fact]
    public async Task Read_HeaderOfNumbersOnly_GeneratesNamesAndKeepsTheRowAsData()
    {
        var (tables, warnings, rows) = await ReadAsync(Sheet(Row(1, 2020, 2021), Row(2, 5, 6)));

        tables[0].Columns.Select(c => c.Name).Should().Equal("column0", "column1");
        rows[0].Should().HaveCount(2);
        tables[0].NeedsReview.Should().BeTrue();
        warnings.Should().ContainSingle().Which.Should().Contain("generated names");
    }

    [Fact]
    public async Task Read_SecondBlockOfRows_LoadsEachBlockAsItsOwnTableAndFlagsThem()
    {
        var (tables, warnings, rows) = await ReadAsync(Sheet(
            Row(1, "id", "name", "city"), Row(2, 1, "a", "b"), Row(3, 2, "c", "d"), Row(4, 3, "e", "f"),
            Row(6, "approver", "limit"), Row(7, "Director", 25000), Row(8, "CFO", 100000)));

        tables.Select(t => t.Name).Should().Equal("book - Sales", "book - Sales (2)");
        tables[0].Columns.Select(c => c.Name).Should().Equal("id", "name", "city");
        tables[1].Columns.Select(c => c.Name).Should().Equal("approver", "limit");
        rows[0].Should().HaveCount(3);
        rows[1].Should().HaveCount(2);
        tables.Should().OnlyContain(t => t.NeedsReview);
        warnings.Should().HaveCount(2).And.OnlyContain(w => w.Contains("2 separate tables"));
    }

    [Fact]
    public async Task Read_MixedColumn_IsTextWithNumbersWrittenAsExcelShowsThem()
    {
        var (tables, _, rows) = await ReadAsync(Sheet(Row(1, "code"), Row(2, "A1"), Row(3, 0.1), Row(4, 12)));

        tables[0].Columns[0].Type.Should().Be(TableColumnType.Text);
        rows[0].Select(r => r[0]).Should().Equal("A1", "0.1", "12");
    }

    [Fact]
    public async Task Read_DateTimesAndSharedStrings_ReadAsTheyShow()
    {
        var file = Build(["when", "who"], new SheetSpec("Log",
            "<sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>" +
            "<row r=\"2\"><c r=\"A2\" s=\"2\"><v>45322.5</v></c><c r=\"B2\" t=\"s\"><v>1</v></c></row></sheetData>"));

        var (tables, _, rows) = await ReadAsync(file);

        tables[0].Columns.Select(c => (c.Name, c.Type)).Should().Equal(("when", TableColumnType.DateTime), ("who", TableColumnType.Text));
        rows[0][0].Should().Equal(new DateTime(2024, 1, 31, 12, 0, 0), "who");
    }

    [Fact]
    public async Task Read_SeveralSheets_OneTablePerSheetWithHiddenOnesLoadedAndDescribed()
    {
        var file = Build(
            new SheetSpec("Sales", $"<sheetData>{Row(1, "a", "b")}{Row(2, 1, 2)}</sheetData>"),
            new SheetSpec("Lookup", $"<sheetData>{Row(1, "k", "v")}{Row(2, "x", 1)}</sheetData>", Hidden: true),
            new SheetSpec("Empty", "<sheetData/>"),
            new SheetSpec("Costs", $"<sheetData>{Row(1, "c", "d")}{Row(2, 3, 4)}</sheetData>"));

        var (tables, warnings, _) = await ReadAsync(file);

        tables.Select(t => t.Name).Should().Equal("book - Sales", "book - Lookup", "book - Costs");
        tables[1].Description.Should().Be("This sheet is hidden in Excel.");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_NoSheetWithData_IsUnreadable()
    {
        Func<Task> read = () => ReadAsync(Build(new SheetSpec("Empty", "<sheetData/>")));

        await read.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("yyyy-mm-dd", true)]
    [InlineData("d/m/yy h:mm", true)]
    [InlineData("[$-409]mmmm d, yyyy", true)]
    [InlineData("0.00", false)]
    [InlineData("\"days\" 0", false)]
    [InlineData("[h]:mm:ss", false)]
    [InlineData("#,##0;[Red]-#,##0", false)]
    public void IsDateFormat_FollowsCalaminesRule(string code, bool expected) =>
        XlsxWorkbook.IsDateFormat(code).Should().Be(expected);

    [Fact]
    public async Task SameWorkbook_TextHasVisibleSheetsAndTablesHaveEverySheet()
    {
        var file = Build(
            new SheetSpec("Sales", $"<sheetData>{Row(1, "region", "units")}{Row(2, "East", 3)}</sheetData>"),
            new SheetSpec("Prices", $"<sheetData>{Row(1, "sku", "price")}{Row(2, "P-1", 12.5)}</sheetData>", Hidden: true));

        var parsed = await new OfficeParser().ParseAsync(file, "book.xlsx");
        file.Position = 0;
        var (tables, _, rows) = await ReadAsync(file);

        parsed.Content.Should().Contain("## Sheet 1: Sales").And.Contain("| region | units |").And.Contain("| East | 3 |")
            .And.NotContain("P-1", "the searchable text is CON-312's Markdown, which leaves hidden sheets out");
        tables.Select(t => t.Name).Should().Equal("book - Sales", "book - Prices");
        tables[1].Description.Should().Contain("hidden in Excel");
        rows[1].Should().ContainSingle().Which.Should().Equal("P-1", 12.5m);
    }
}
