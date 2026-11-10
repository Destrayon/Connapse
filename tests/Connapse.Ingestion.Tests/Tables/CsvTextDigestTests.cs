using System.Text;
using Connapse.Core;
using Connapse.Ingestion.Tables;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Tables;

[Trait("Category", "Unit")]
public class CsvTextDigestTests
{
    private static TableInfo Table(long rows) => new(
        Guid.NewGuid(), Guid.NewGuid(), "orders", null, Guid.NewGuid(),
        [new TableColumn("id", TableColumnType.WholeNumber), new TableColumn("customer", TableColumnType.Text),
         new TableColumn("day", TableColumnType.Date)],
        rows, false, [], "schema", "t_1", 1, DateTime.UtcNow, DateTime.UtcNow);

    private static string Csv(int rows)
    {
        var csv = new StringBuilder("id,customer,day\r\n");
        for (int i = 1; i <= rows; i++)
            csv.Append(i).Append(",customer-").Append(i).Append(",2024-01-01\r\n");
        return csv.ToString();
    }

    [Theory]
    [InlineData(CsvTextDigest.MaxFullTextRows, false)]
    [InlineData(CsvTextDigest.MaxFullTextRows + 1, true)]
    public void IsLarge_RowCountAroundTheLimit_OnlyAboveItIsLarge(long rows, bool large) =>
        CsvTextDigest.IsLarge(Table(rows)).Should().Be(large);

    [Fact]
    public void Build_LargeTable_DescribesTheTableAndKeepsOnlyTheFirstLines()
    {
        string digest = CsvTextDigest.Build(Table(60_000), Csv(60_000));

        digest.Should().StartWith("orders: a table of 60,000 rows and 3 columns.");
        digest.Should().Contain("Columns: id (whole number), customer (text), day (date)");
        digest.Should().Contain("id,customer,day\n1,customer-1,2024-01-01\n");
        digest.Should().Contain($"customer-{CsvTextDigest.SampleLines - 1},");
        digest.Should().NotContain($"customer-{CsvTextDigest.SampleLines},");
        digest.Should().NotContain("\r");
    }

    [Fact]
    public void Build_HugeCells_StopsTheSampleAtItsCharacterLimit()
    {
        string wide = "id,notes\n" + string.Concat(Enumerable.Range(1, 40).Select(i => $"{i},{new string('x', 4000)}\n"));

        string digest = CsvTextDigest.Build(Table(5_000), wide);

        digest.Length.Should().BeLessThan(CsvTextDigest.MaxSampleChars + 1000);
        digest.Should().Contain("\n1,x");
    }
}
