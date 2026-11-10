using System.Text;
using Connapse.Core;
using Connapse.Ingestion.Tables;
using CsCheck;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Tables;

[Trait("Category", "Unit")]
public class CsvTableReaderTests
{
    private static async Task<(TableLoad Load, List<IReadOnlyList<object?>> Rows)> ReadAsync(string csv, string fileName = "sales.csv", Encoding? encoding = null)
    {
        var stream = new MemoryStream((encoding ?? new UTF8Encoding(false)).GetBytes(csv));
        TableLoad load = await CsvTableReader.ReadAsync(stream, fileName, documentId: null);
        List<IReadOnlyList<object?>> rows = [];
        await foreach (IReadOnlyList<object?> row in load.Rows)
            rows.Add(row);
        return (load, rows);
    }

    [Fact]
    public async Task ReadAsync_TypedFile_NamesTheTableAndTypesEveryColumn()
    {
        var (load, rows) = await ReadAsync("id,day,amount,paid,zip\n1,2024-01-31,\"1,234.50\",true,02134\n2,2024-02-01,7.25,false,90210\n");

        load.Name.Should().Be("sales");
        load.Columns.Select(c => (c.Name, c.Type)).Should().Equal(
            ("id", TableColumnType.WholeNumber), ("day", TableColumnType.Date), ("amount", TableColumnType.Number),
            ("paid", TableColumnType.Boolean), ("zip", TableColumnType.Text));
        load.Columns[2].Format!.GroupChar.Should().Be(",");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(1L, new DateOnly(2024, 1, 31), 1234.50m, true, "02134");
        load.NeedsReview.Should().BeFalse();
        load.Rejects.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_BadCellTheSampleMissed_IsListedAndItsRowLeftOut()
    {
        // Past the first 2,048 rows the sample keeps every k-th row with k a power of two, so an odd row is never in it.
        const int bad = 52_050;
        var csv = new StringBuilder("id,amount\n");
        for (int i = 1; i <= 100_000; i++)
            csv.Append(i).Append(',').Append(i == bad ? "n/a" : (i * 2).ToString()).Append('\n');

        var (load, rows) = await ReadAsync(csv.ToString());

        load.Columns.Select(c => c.Type).Should().Equal(TableColumnType.WholeNumber, TableColumnType.WholeNumber);
        rows.Should().HaveCount(99_999);
        load.NeedsReview.Should().BeTrue();
        load.Rejects.Should().ContainSingle().Which.Should().Be(new TableReject(bad + 1, 2, "n/a", "not a whole number"));
    }

    [Fact]
    public async Task ReadAsync_BadValueInTheSample_MakesTheColumnText()
    {
        var (load, rows) = await ReadAsync("id,amount\n1,2\n2,n/a\n3,4\n");

        load.Columns[1].Type.Should().Be(TableColumnType.Text);
        rows.Should().HaveCount(3);
        load.Rejects.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_WrongWidthRow_IsListedAsAWholeRow()
    {
        var (load, rows) = await ReadAsync("a,b\n1,2\n3\n4,5\n");

        rows.Should().HaveCount(2);
        load.Rejects.Should().ContainSingle().Which.Should().Be(new TableReject(3, 0, "3", "has 1 fields, expected 2"));
    }

    [Fact]
    public async Task ReadAsync_ManyBadCells_ReguessesTypesFromTheWholeFile()
    {
        var csv = new StringBuilder("id,code\n");
        for (int i = 1; i <= 40_000; i++)
            csv.Append(i).Append(',').Append(i > 30_000 ? "X" + i : i.ToString()).Append('\n');

        var (load, rows) = await ReadAsync(csv.ToString());

        load.Columns[1].Type.Should().Be(TableColumnType.Text);
        rows.Should().HaveCount(40_000);
        load.Rejects.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_TooManyWrongWidthRows_Fails()
    {
        var csv = new StringBuilder("a,b\n");
        for (int i = 0; i < CsvTableReader.MaxRejects + 5; i++)
            csv.Append("1,2\n3\n");

        Func<Task> read = () => ReadAsync(csv.ToString());

        await read.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadAsync_NoHeader_GeneratesNames()
    {
        var (load, rows) = await ReadAsync("1,2024-01-01\n2,2024-01-02\n");

        load.Columns.Select(c => c.Name).Should().Equal("column0", "column1");
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadAsync_TsvLatin1WithEuropeanNumbers_ReadsThem()
    {
        var (load, rows) = await ReadAsync("Stadt\tBetrag\nMünchen\t1.234,50\nKöln\t7,25\n", "umsatz.tsv", Encoding.Latin1);

        load.Columns[1].Format!.DecimalChar.Should().Be(",");
        rows[0].Should().Equal("München", 1234.50m);
    }

    [Fact]
    public async Task ReadAsync_HeaderOnly_IsAnEmptyTable()
    {
        var (load, rows) = await ReadAsync("a,b\n");

        load.Columns.Select(c => c.Name).Should().Equal("a", "b");
        rows.Should().BeEmpty();
    }

    [Fact]
    public void Utf8Boundary_NeverSplitsACharacter()
    {
        Gen.String.Sample(s =>
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            for (int cut = 0; cut <= bytes.Length; cut++)
            {
                int end = CsvTableReader.Utf8Boundary(bytes.AsSpan(0, cut));
                var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
                Action decode = () => strict.GetString(bytes, 0, end);
                decode.Should().NotThrow();
                end.Should().BeGreaterThanOrEqualTo(cut - 3);
            }
        });
    }

    [Fact]
    public async Task ReadAsync_AnyGrid_LoadsOrListsEveryRow()
    {
        Gen<string> cell = Gen.OneOf(Gen.Int[-1000, 1000].Select(i => i.ToString()), Gen.String[Gen.Char.AlphaNumeric, 0, 6], Gen.Const(""));
        await Gen.Int[1, 5].SelectMany(w => cell.Array[w].Array[1, 30]).SampleAsync(async grid =>
        {
            string csv = string.Concat(grid.Select(r => string.Join(",", r.Select(f => "\"" + f + "\"")) + "\n"));

            var (load, rows) = await ReadAsync(csv);

            // The first row is either the header or data; every other row is loaded or listed, never lost.
            long listedRows = load.Rejects!.Select(r => r.Row).Distinct().Count();
            (rows.Count + listedRows).Should().BeOneOf(grid.Length, grid.Length - 1);
            rows.Should().AllSatisfy(r => r.Should().HaveCount(load.Columns.Count));
        }, iter: 50);
    }
}
