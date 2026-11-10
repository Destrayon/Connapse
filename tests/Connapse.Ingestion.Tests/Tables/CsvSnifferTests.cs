using System.Globalization;
using Connapse.Core;
using Connapse.Ingestion.Tables;
using CsCheck;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Tables;

[Trait("Category", "Unit")]
public class CsvSnifferTests
{
    private static ValueReader Infer(params string?[] values) =>
        CsvSniffer.InferTypes(values.Select(v => new[] { v }), 1)[0];

    [Theory]
    [InlineData("a,b,c\n1,2,3\n4,5,6\n", ',')]
    [InlineData("a;b;c\n1,5;2;3\n4;5;6\n", ';')]
    [InlineData("a|b\n1|2\n", '|')]
    [InlineData("a\tb\tc\n1\t2\t3\n", '\t')]
    [InlineData("name,note\nx,\"a;b;c\"\ny,\"d;e\"\n", ',')]
    public void DetectDelimiter_TypicalFiles_PicksTheConsistentSplit(string csv, char expected)
    {
        CsvSniffer.DetectDelimiter(csv, wholeFile: true).Should().Be(expected);
    }

    [Fact]
    public void DetectDelimiter_RaggedCommaFile_StillBeatsADelimiterThatNeverAppears()
    {
        string csv = "a,b,c\n1,2,3\n4,5\n6,7,8\n";

        CsvSniffer.DetectDelimiter(csv, wholeFile: true).Should().Be(',');
    }

    [Fact]
    public void DetectDelimiter_SingleColumn_PrefersTheHint()
    {
        CsvSniffer.DetectDelimiter("a\n1\n2\n", wholeFile: true, preferred: '\t').Should().Be('\t');
    }

    [Theory]
    [InlineData(TableColumnType.Boolean, "true", "False", "yes", "NO", "t")]
    [InlineData(TableColumnType.WholeNumber, "1", "-20", "300", "0")]
    [InlineData(TableColumnType.WholeNumber, "1,234", "12", "1,000,000")]
    [InlineData(TableColumnType.Number, "1.5", "2", "-3.25", "1e3")]
    [InlineData(TableColumnType.Number, "1,5", "2,25")]
    [InlineData(TableColumnType.Number, "1.234,5", "12,0")]
    [InlineData(TableColumnType.Date, "2024-01-31", "2024-02-01")]
    [InlineData(TableColumnType.Date, "31/01/2024", "01/02/2024")]
    [InlineData(TableColumnType.DateTime, "2024-01-31 10:15:00", "2024-02-01 23:59:59")]
    [InlineData(TableColumnType.DateTime, "2024-01-31T10:15:00Z", "2024-02-01T23:59:59+02:00")]
    [InlineData(TableColumnType.Text, "007", "123")]
    [InlineData(TableColumnType.Text, "02134", "90210")]
    [InlineData(TableColumnType.Text, "1", "x")]
    public void InferTypes_Values_PicksTheExpectedType(TableColumnType expected, params string[] values)
    {
        Infer(values).Type.Should().Be(expected);
    }

    [Fact]
    public void InferTypes_AmbiguousDates_ReadsDayFirstLikeDuckDb()
    {
        ValueReader reader = Infer("01/02/2024", "03/04/2024");

        reader.Type.Should().Be(TableColumnType.Date);
        reader.Format!.Pattern.Should().Be("%d/%m/%Y");
        reader.TryRead("01/02/2024", out object? value).Should().BeTrue();
        value.Should().Be(new DateOnly(2024, 2, 1));
    }

    [Fact]
    public void InferTypes_AMonthOver12_SwitchesToMonthFirst()
    {
        ValueReader reader = Infer("01/02/2024", "12/31/2024");

        reader.Format!.Pattern.Should().Be("%m/%d/%Y");
    }

    [Fact]
    public void InferTypes_CommaDecimals_RecordsTheSeparatorAndReadsTheValue()
    {
        ValueReader reader = Infer("1.234,50", "7,25");

        reader.Format!.DecimalChar.Should().Be(",");
        reader.Format.GroupChar.Should().Be(".");
        reader.TryRead("1.234,50", out object? value).Should().BeTrue();
        value.Should().Be(1234.50m);
    }

    [Fact]
    public void InferTypes_EmptyCellsAreNulls_AndAnAllEmptyColumnIsText()
    {
        Infer("", "  ", null, "4").Type.Should().Be(TableColumnType.WholeNumber);
        Infer("", null).Type.Should().Be(TableColumnType.Text);
    }

    [Fact]
    public void InferTypes_OffsetTimestamp_StoresTheUtcMoment()
    {
        ValueReader reader = Infer("2024-01-31T10:15:00+02:00");

        reader.TryRead("2024-01-31T10:15:00+02:00", out object? value).Should().BeTrue();
        value.Should().Be(new DateTime(2024, 1, 31, 8, 15, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void InferTypes_RowsWithAnotherFieldCount_AreSkipped()
    {
        IReadOnlyList<ValueReader> readers = CsvSniffer.InferTypes([["1", "2"], ["x"], ["3", "4"]], 2);

        readers.Select(r => r.Type).Should().Equal(TableColumnType.WholeNumber, TableColumnType.WholeNumber);
    }

    [Fact]
    public void IsHeader_NamesOverNumbers_IsAHeader()
    {
        IReadOnlyList<ValueReader> readers = CsvSniffer.InferTypes([["1", "2024-01-01"], ["2", "2024-01-02"]], 2);

        CsvSniffer.IsHeader(["id", "day"], readers).Should().BeTrue();
        CsvSniffer.IsHeader(["0", "2023-12-31"], readers).Should().BeFalse();
    }

    [Fact]
    public void IsHeader_AllTextColumns_IsAssumedLikeDuckDb()
    {
        CsvSniffer.IsHeader(["a", "b"], [ValueReaders.Text, ValueReaders.Text]).Should().BeTrue();
    }

    [Fact]
    public void Names_EmptyAndRepeated_AreGeneratedAndMadeUnique()
    {
        CsvSniffer.Names(["id", "", "Name", "name", "id"], 5)
            .Should().Equal("id", "column1", "Name", "name_1", "id_1");
        CsvSniffer.Names(null, 2).Should().Equal("column0", "column1");
    }

    [Fact]
    public void WholeNumber_ReadsAnyLong_AsWritten()
    {
        ValueReader reader = ValueReaders.Candidates.First(r => r.Type == TableColumnType.WholeNumber && r.Format is null);

        Gen.Long.Sample(n =>
        {
            reader.TryRead(n.ToString(CultureInfo.InvariantCulture), out object? value).Should().BeTrue();
            value.Should().Be(n);
        });
    }

    [Fact]
    public void Number_ReadsAnyDecimal_InEverySeparatorStyle()
    {
        List<ValueReader> numbers = [.. ValueReaders.Candidates.Where(r => r.Type == TableColumnType.Number)];

        Gen.Decimal[-1_000_000_000m, 1_000_000_000m].Select(d => Math.Round(d, 4)).Sample(d =>
        {
            foreach (ValueReader reader in numbers)
            {
                string dec = reader.Format!.DecimalChar!;
                string? group = reader.Format.GroupChar;
                var culture = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
                culture.NumberDecimalSeparator = dec;
                culture.NumberGroupSeparator = group ?? "";
                string text = d.ToString(group is null ? "0.####" : "#,0.####", culture);

                reader.TryRead(text, out object? value).Should().BeTrue($"{reader} reads {text}");
                value.Should().Be(d);
            }
        });
    }

    [Fact]
    public void Dates_ReadAnyDate_InTheirOwnFormat()
    {
        List<ValueReader> dates = [.. ValueReaders.Candidates.Where(r => r.Type == TableColumnType.Date && r.Format!.Pattern!.Contains("%Y"))];

        Gen.Date.Where(d => d.Year >= 1000).Sample(d =>
        {
            var day = DateOnly.FromDateTime(d);
            foreach (ValueReader reader in dates)
            {
                string text = Strftime(reader.Format!.Pattern!, d);
                reader.TryRead(text, out object? value).Should().BeTrue($"{reader} reads {text}");
                value.Should().Be(day);
            }
        });
    }

    [Fact]
    public void InferTypes_AnyStrings_NeverThrowsAndEveryValueReadsWithTheChosenReader()
    {
        Gen.String.Array[1, 40].Sample(values =>
        {
            ValueReader reader = Infer(values);
            foreach (string v in values.Where(v => !CsvSniffer.IsNull(v)))
                reader.TryRead(v, out _).Should().BeTrue();
        });
    }

    [Fact]
    public void Records_AnyFields_RoundTripThroughQuotedCsv()
    {
        Gen.String[Gen.OneOf(Gen.Char.AlphaNumeric, Gen.Char[",\"\n;| "]), 0, 12].Array[1, 6].Array[1, 8].Sample(rows =>
        {
            string csv = string.Concat(rows.Select(r => string.Join(",", r.Select(f => "\"" + f.Replace("\"", "\"\"") + "\"")) + "\n"));

            List<string?[]> read = [.. CsvSniffer.Records(new StringReader(csv), ',')];

            read.Should().HaveCount(rows.Length);
            for (int i = 0; i < rows.Length; i++)
                read[i].Should().Equal(rows[i]);
        });
    }

    private static string Strftime(string pattern, DateTime d) => pattern
        .Replace("%Y", d.ToString("yyyy", CultureInfo.InvariantCulture))
        .Replace("%m", d.ToString("MM", CultureInfo.InvariantCulture))
        .Replace("%d", d.ToString("dd", CultureInfo.InvariantCulture));
}
