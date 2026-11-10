using System.Text.RegularExpressions;
using Connapse.Core;
using Connapse.Storage.Tables;
using CsCheck;
using FluentAssertions;

namespace Connapse.Core.Tests.Tables;

/// <summary>
/// CON-301: the names the table layer puts into SQL. Identifiers can't be query parameters, so every generated name
/// must stay inside <c>[a-z0-9_]</c> and PostgreSQL's 63-byte limit, and anything else must be refused.
/// </summary>
[Trait("Category", "Unit")]
public class TableDdlRandomInputTests
{
    private static readonly Regex Safe = new("^[a-z0-9_]{1,63}$");

    [Fact]
    public void SchemaName_AnyContainer_IsSafeAndUnique() =>
        Gen.Select(Gen.Guid, Gen.Guid).Sample((a, b) =>
        {
            string name = TableDdl.SchemaName(a);
            name.Should().MatchRegex("^tables_[0-9a-f]{32}$");
            TableDdl.Quote(name).Should().Be($"\"{name}\"");
            if (a != b)
                TableDdl.SchemaName(b).Should().NotBe(name);
        });

    [Fact]
    public void NewTableName_Always_MatchesTheCatalogCheck() =>
        Gen.Int[1, 1].Sample(_ =>
            TableDdl.NewTableName().Should().MatchRegex("^t_[0-9a-f]{32}$"));

    [Fact]
    public void ColumnName_AnyColumnPostgresAllows_IsSafeAndOneBased() =>
        Gen.Int[0, TableDdl.MaxColumns - 1].Sample(i =>
        {
            string name = TableDdl.ColumnName(i);
            name.Should().Be($"c_{i + 1}");
            Safe.IsMatch(name).Should().BeTrue();
        });

    [Fact]
    public void Quote_AnyString_AcceptsOnlyGeneratedShapes() =>
        Gen.String[Gen.Char[" aZ0_\"';-.é"], 0, 70].Sample(s =>
        {
            Func<string> quote = () => TableDdl.Quote(s);
            if (Safe.IsMatch(s))
                quote().Should().Be($"\"{s}\"");
            else
                quote.Should().Throw<ArgumentException>();
        });

    [Fact]
    public void CreateTableSql_AnyColumns_UsesOnlyGeneratedNames() =>
        Gen.Select(Gen.Guid, Gen.Select(Gen.String[Gen.Char["a\"; )-"], 0, 8], Gen.Enum<TableColumnType>(),
                (n, t) => new TableColumn(n, t)).List[1, 20])
            .Sample((container, columns) =>
            {
                string sql = TableDdl.CreateTableSql(container, "t_" + container.ToString("N"), columns);
                // Headers from a file never reach SQL; only c_1..c_n and their types do.
                string body = sql[(sql.IndexOf('(') + 1)..^1];
                body.Split(", ").Select(part => part.Split(' ')[0])
                    .Should().Equal(columns.Select((_, i) => $"\"c_{i + 1}\""));
            });

    [Fact]
    public void FrictionlessType_EveryColumnType_RoundTrips()
    {
        foreach (TableColumnType type in Enum.GetValues<TableColumnType>())
            TableValues.FromFrictionlessType(TableValues.FrictionlessType(type)).Should().Be(type);
    }

    [Fact]
    public void Convert_ValueOfItsColumnType_IsKept() =>
        Gen.Select(Gen.Long, Gen.Decimal, Gen.Bool, Gen.String).Sample((l, d, b, s) =>
        {
            TableValues.Convert(l, TableColumnType.WholeNumber, 1, 0).Should().Be(l);
            TableValues.Convert((int)l, TableColumnType.WholeNumber, 1, 0).Should().Be((long)(int)l);
            TableValues.Convert(d, TableColumnType.Number, 1, 0).Should().Be(d);
            TableValues.Convert(b, TableColumnType.Boolean, 1, 0).Should().Be(b);
            TableValues.Convert(s, TableColumnType.Text, 1, 0).Should().Be(s);
            foreach (TableColumnType type in Enum.GetValues<TableColumnType>())
                TableValues.Convert(null, type, 1, 0).Should().BeNull();
        });

    [Fact]
    public void Convert_ValueOfAnotherType_FailsTheLoad() =>
        Gen.String.Sample(s =>
        {
            foreach (TableColumnType type in Enum.GetValues<TableColumnType>().Where(t => t != TableColumnType.Text))
            {
                Action convert = () => TableValues.Convert(s, type, 3, 1);
                convert.Should().Throw<ArgumentException>().WithMessage("Row 3, column 2*");
            }
        });
}
