using System.Text.Json;
using Connapse.Core;
using Connapse.Storage.Tables;
using FluentAssertions;
using NpgsqlTypes;

namespace Connapse.Storage.Tests.Tables;

[Trait("Category", "Unit")]
public class TableQueryCompilerTests
{
    private static readonly Guid ContainerId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string Schema = "tables_11111111222233334444555555555555";
    private const string Storage = "t_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string From = $"FROM \"{Schema}\".\"{Storage}\"";

    private static readonly TableInfo Sales = Table(
        new TableColumn("region", TableColumnType.Text),
        new TableColumn("amount", TableColumnType.Number),
        new TableColumn("units", TableColumnType.WholeNumber),
        new TableColumn("paid", TableColumnType.Boolean),
        new TableColumn("sold_on", TableColumnType.Date),
        new TableColumn("created", TableColumnType.DateTime));

    private static TableInfo Table(params TableColumn[] columns) => new(
        Guid.NewGuid(), ContainerId, "sales", null, null, columns, 10, false, [], Schema, Storage, 1,
        DateTime.UtcNow, DateTime.UtcNow);

    private static TableCondition Where(string column, TableFilterOperator op, params object?[] values) =>
        new(new TableField(column), op, values);

    private static CompiledTableQuery Read(TableFilter? where = null, IReadOnlyList<string>? columns = null) =>
        TableQueryCompiler.CompileRead(Sales, new TableReadRequest(Sales.Id, columns, where));

    [Fact]
    public void CompileRead_NoOptions_SelectsEveryColumnWithDefaultLimit()
    {
        CompiledTableQuery query = Read();

        query.Sql.Should().Be(
            "SELECT pg_catalog.substr(\"c_1\", 1, 2001), \"c_2\", \"c_3\", \"c_4\", \"c_5\", \"c_6\" "
            + From + " LIMIT $1 OFFSET $2");
        query.Columns.Should().Equal("region", "amount", "units", "paid", "sold_on", "created");
        query.Limit.Should().Be(TableQueryCompiler.DefaultLimit);
        query.Parameters.Select(p => p.Value).Should().Equal(101L, 0L);
        query.Parameters.Should().OnlyContain(p => p.ParameterName == "" && p.NpgsqlDbType == NpgsqlDbType.Bigint);
    }

    [Fact]
    public void CompileRead_ColumnsByHeader_MapToGeneratedNames()
    {
        CompiledTableQuery query = Read(columns: ["units", "REGION"]);

        query.Sql.Should().StartWith("SELECT \"c_3\", pg_catalog.substr(\"c_1\", 1, 2001) FROM");
        query.Columns.Should().Equal("units", "region");
    }

    [Theory]
    [InlineData(TableFilterOperator.Equal, "=")]
    [InlineData(TableFilterOperator.NotEqual, "<>")]
    [InlineData(TableFilterOperator.Less, "<")]
    [InlineData(TableFilterOperator.LessOrEqual, "<=")]
    [InlineData(TableFilterOperator.Greater, ">")]
    [InlineData(TableFilterOperator.GreaterOrEqual, ">=")]
    public void CompileRead_Comparison_BindsTypedParameter(TableFilterOperator op, string sql)
    {
        CompiledTableQuery query = Read(Where("amount", op, 12.5m));

        query.Sql.Should().Contain($"WHERE \"c_2\" {sql} $1 LIMIT $2 OFFSET $3");
        query.Parameters[0].Value.Should().Be(12.5m);
        query.Parameters[0].NpgsqlDbType.Should().Be(NpgsqlDbType.Numeric);
    }

    [Fact]
    public void CompileRead_In_BindsOneTypedArray()
    {
        CompiledTableQuery query = Read(Where("units", TableFilterOperator.In, 1, 2L, "3"));

        query.Sql.Should().Contain("WHERE \"c_3\" = ANY($1)");
        query.Parameters[0].Value.Should().BeEquivalentTo(new long[] { 1, 2, 3 });
        query.Parameters[0].NpgsqlDbType.Should().Be(NpgsqlDbType.Array | NpgsqlDbType.Bigint);
    }

    [Fact]
    public void CompileRead_Between_BindsBothEnds()
    {
        CompiledTableQuery query = Read(Where("sold_on", TableFilterOperator.Between, "2026-01-01", "2026-12-31"));

        query.Sql.Should().Contain("WHERE \"c_5\" BETWEEN $1 AND $2");
        query.Parameters[0].Value.Should().Be(new DateOnly(2026, 1, 1));
        query.Parameters[1].Value.Should().Be(new DateOnly(2026, 12, 31));
    }

    [Fact]
    public void CompileRead_IsNull_TakesNoParameter()
    {
        Read(Where("paid", TableFilterOperator.IsNull)).Sql.Should().Contain("WHERE \"c_4\" IS NULL LIMIT $1");
    }

    [Fact]
    public void CompileRead_Contains_IsCaseInsensitiveStrpos()
    {
        CompiledTableQuery query = Read(Where("region", TableFilterOperator.Contains, "west"));

        query.Sql.Should().Contain("WHERE pg_catalog.strpos(pg_catalog.lower(\"c_1\"), pg_catalog.lower($1)) > 0");
        query.Parameters[0].Value.Should().Be("west");
    }

    [Fact]
    public void CompileRead_StartsWith_UsesCatalogFunction()
    {
        Read(Where("region", TableFilterOperator.StartsWith, "No")).Sql
            .Should().Contain("WHERE pg_catalog.starts_with(\"c_1\", $1)");
    }

    [Fact]
    public void CompileRead_AndOrNot_NestWithParentheses()
    {
        TableFilter filter = new TableAndFilter([
            Where("paid", TableFilterOperator.Equal, true),
            new TableOrFilter([Where("units", TableFilterOperator.Greater, 5), new TableNotFilter(Where("region", TableFilterOperator.IsNull))]),
        ]);

        Read(filter).Sql.Should().Contain("WHERE (\"c_4\" = $1 AND (\"c_3\" > $2 OR NOT (\"c_1\" IS NULL)))");
    }

    [Fact]
    public void CompileRead_JsonValues_ConvertToColumnTypes()
    {
        using JsonDocument doc = JsonDocument.Parse("""["2026-03-04T05:06:07Z", true, 42]""");
        JsonElement[] values = doc.RootElement.EnumerateArray().ToArray();

        Read(Where("created", TableFilterOperator.Equal, values[0])).Parameters[0].Value
            .Should().Be(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Unspecified));
        Read(Where("paid", TableFilterOperator.Equal, values[1])).Parameters[0].Value.Should().Be(true);
        Read(Where("amount", TableFilterOperator.Equal, values[2])).Parameters[0].Value.Should().Be(42m);
    }

    [Fact]
    public void CompileRead_OrderLimitOffset_AreAppliedAndBound()
    {
        CompiledTableQuery query = TableQueryCompiler.CompileRead(Sales, new TableReadRequest(
            Sales.Id, ["region"], null, [new TableOrder(new TableField("amount"), TableSortDirection.Descending)], 10, 20));

        query.Sql.Should().EndWith("ORDER BY \"c_2\" DESC NULLS LAST LIMIT $1 OFFSET $2");
        query.Parameters.Select(p => p.Value).Should().Equal(11L, 20L);
        query.Limit.Should().Be(10);
    }

    [Fact]
    public void CompileRead_HostileValue_IsBoundNeverWritten()
    {
        const string attack = "x'); DROP TABLE documents; --";

        CompiledTableQuery query = Read(Where("region", TableFilterOperator.Equal, attack));

        query.Sql.Should().NotContain("DROP").And.NotContain("'");
        query.Parameters[0].Value.Should().Be(attack);
    }

    [Theory]
    [InlineData("region\"; DROP TABLE documents; --")]
    [InlineData("c_1")]
    [InlineData("*")]
    [InlineData("pg_sleep(10)")]
    [InlineData("")]
    public void CompileRead_ColumnNotInCatalog_IsRejectedWithColumnList(string column)
    {
        Action act = () => Read(columns: [column]);

        act.Should().Throw<TableQueryException>().WithMessage("*columns are: \"region\", \"amount\"*");
    }

    [Fact]
    public void CompileRead_HostileHeaderInCatalog_StillEmitsOnlyGeneratedName()
    {
        TableInfo table = Table(new TableColumn("a\"; DROP TABLE x; --", TableColumnType.Text));

        CompiledTableQuery query = TableQueryCompiler.CompileRead(table, new TableReadRequest(table.Id, ["a\"; DROP TABLE x; --"]));

        query.Sql.Should().NotContain("DROP");
        query.Columns.Should().Equal("a\"; DROP TABLE x; --");
    }

    [Fact]
    public void CompileRead_BadStorageNameInCatalog_Throws()
    {
        TableInfo table = Sales with { StorageName = "t_1\"; DROP TABLE documents; --" };

        Action act = () => TableQueryCompiler.CompileRead(table, new TableReadRequest(table.Id));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CompileRead_HeadersDifferOnlyInCase_ExactWinsAndLooseIsAmbiguous()
    {
        TableInfo table = Table(new TableColumn("Name", TableColumnType.Text), new TableColumn("name", TableColumnType.Text));

        TableQueryCompiler.CompileRead(table, new TableReadRequest(table.Id, ["name"])).Sql.Should().Contain("\"c_2\"");
        Action act = () => TableQueryCompiler.CompileRead(table, new TableReadRequest(table.Id, ["NAME"]));
        act.Should().Throw<TableQueryException>().WithMessage("*ambiguous*");
    }

    public static TheoryData<TableFilter, string> RejectedFilters => new()
    {
        { Where("amount", TableFilterOperator.Equal, "lots"), "*number column*" },
        { Where("units", TableFilterOperator.Equal, 1.5m), "*whole-number column*" },
        { Where("sold_on", TableFilterOperator.Equal, "03/04/2026"), "*yyyy-mm-dd*" },
        { Where("paid", TableFilterOperator.Equal, "yes"), "*true/false column*" },
        { Where("paid", TableFilterOperator.Greater, true), "*true/false*" },
        { Where("amount", TableFilterOperator.Contains, "1"), "*only works on text*" },
        { Where("units", TableFilterOperator.StartsWith, "1"), "*only works on text*" },
        { Where("region", TableFilterOperator.Equal), "*exactly one value*" },
        { Where("region", TableFilterOperator.Equal, "a", "b"), "*exactly one value*" },
        { Where("region", TableFilterOperator.Equal, [null]), "*use is_null*" },
        { Where("region", TableFilterOperator.IsNull, "a"), "*no value*" },
        { Where("region", TableFilterOperator.Between, "a"), "*exactly two*" },
        { Where("region", TableFilterOperator.In), "*at least one*" },
        { Where("region", TableFilterOperator.Equal, new string('x', TableQueryCompiler.MaxValueLength + 1)), "*at most*" },
        { new TableAndFilter([]), "*at least one filter*" },
        { new TableCondition(new TableField("units", TableAggregateFunction.Sum), TableFilterOperator.Greater, [1]), "*having*" },
    };

    [Theory]
    [MemberData(nameof(RejectedFilters))]
    public void CompileRead_InvalidFilter_IsRejected(TableFilter filter, string message)
    {
        Action act = () => Read(filter);

        act.Should().Throw<TableQueryException>().WithMessage(message);
    }

    [Fact]
    public void CompileRead_TooManyInValues_IsRejected()
    {
        object?[] values = Enumerable.Range(0, TableQueryCompiler.MaxInValues + 1).Select(i => (object?)i).ToArray();

        Action act = () => Read(Where("units", TableFilterOperator.In, values));

        act.Should().Throw<TableQueryException>().WithMessage("*at most 1000 values*");
    }

    [Fact]
    public void CompileRead_FilterNestedTooDeep_IsRejected()
    {
        TableFilter filter = Where("units", TableFilterOperator.IsNull);
        for (int i = 0; i < TableQueryCompiler.MaxFilterDepth; i++)
            filter = new TableNotFilter(filter);

        Action act = () => Read(filter);

        act.Should().Throw<TableQueryException>().WithMessage("*nest at most*");
    }

    [Fact]
    public void CompileRead_TooManyConditions_IsRejected()
    {
        TableFilter filter = new TableOrFilter(Enumerable.Range(0, TableQueryCompiler.MaxConditions + 1)
            .Select(i => (TableFilter)Where("units", TableFilterOperator.Equal, i)).ToList());

        Action act = () => Read(filter);

        act.Should().Throw<TableQueryException>().WithMessage("*at most 100 conditions*");
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(TableQueryCompiler.MaxLimit + 1, null)]
    [InlineData(10, -1)]
    [InlineData(10, TableQueryCompiler.MaxOffset + 1)]
    public void CompileRead_LimitOrOffsetOutOfRange_IsRejected(int limit, int? offset)
    {
        Action act = () => TableQueryCompiler.CompileRead(Sales, new TableReadRequest(Sales.Id, Limit: limit, Offset: offset));

        act.Should().Throw<TableQueryException>();
    }

    [Fact]
    public void CompileAggregate_GroupedWithHavingAndOrder_CompilesOneSelect()
    {
        CompiledTableQuery query = TableQueryCompiler.CompileAggregate(Sales, new TableAggregateRequest(
            Sales.Id,
            [new TableField(null, TableAggregateFunction.Count), new TableField("amount", TableAggregateFunction.Sum)],
            GroupBy: ["region"],
            Where: Where("paid", TableFilterOperator.Equal, true),
            Having: new TableCondition(new TableField(null, TableAggregateFunction.Count), TableFilterOperator.GreaterOrEqual, [2]),
            OrderBy: [new TableOrder(new TableField("amount", TableAggregateFunction.Sum), TableSortDirection.Descending)],
            Limit: 5));

        query.Sql.Should().Be(
            "SELECT pg_catalog.substr(\"c_1\", 1, 2001), pg_catalog.count(*), pg_catalog.sum(\"c_2\") " + From
            + " WHERE \"c_4\" = $1 GROUP BY \"c_1\" HAVING pg_catalog.count(*) >= $2"
            + " ORDER BY pg_catalog.sum(\"c_2\") DESC NULLS LAST LIMIT $3");
        query.Columns.Should().Equal("region", "count", "sum(amount)");
        query.Parameters.Select(p => p.Value).Should().Equal(true, 2L, 6L);
    }

    [Theory]
    [InlineData(TableAggregateFunction.Count, "units", "pg_catalog.count(\"c_3\")")]
    [InlineData(TableAggregateFunction.CountDistinct, "region", "pg_catalog.count(DISTINCT \"c_1\")")]
    [InlineData(TableAggregateFunction.Avg, "units", "pg_catalog.avg(\"c_3\")")]
    [InlineData(TableAggregateFunction.Min, "sold_on", "pg_catalog.min(\"c_5\")")]
    [InlineData(TableAggregateFunction.Max, "region", "pg_catalog.substr(pg_catalog.max(\"c_1\"), 1, 2001)")]
    public void CompileAggregate_EachFunction_CompilesToCatalogAggregate(TableAggregateFunction function, string column, string sql)
    {
        CompiledTableQuery query = TableQueryCompiler.CompileAggregate(Sales, new TableAggregateRequest(Sales.Id, [new TableField(column, function)]));

        query.Sql.Should().Be($"SELECT {sql} {From} LIMIT $1");
    }

    public static TheoryData<TableAggregateRequest, string> RejectedAggregates => new()
    {
        { new TableAggregateRequest(Guid.Empty, []), "*at least one aggregate*" },
        { new TableAggregateRequest(Guid.Empty, [new TableField("region", TableAggregateFunction.Sum)]), "*sum doesn't work*text*" },
        { new TableAggregateRequest(Guid.Empty, [new TableField("paid", TableAggregateFunction.Max)]), "*max doesn't work*" },
        { new TableAggregateRequest(Guid.Empty, [new TableField(null, TableAggregateFunction.Avg)]), "*needs a \"column\"*" },
        { new TableAggregateRequest(Guid.Empty, [new TableField("units")]), "*needs an \"aggregate\"*" },
        {
            new TableAggregateRequest(Guid.Empty, [new TableField(null, TableAggregateFunction.Count)],
                Having: Where("units", TableFilterOperator.Greater, 1)), "*needs an \"aggregate\"*"
        },
        {
            new TableAggregateRequest(Guid.Empty, [new TableField(null, TableAggregateFunction.Count)], GroupBy: ["region"],
                OrderBy: [new TableOrder(new TableField("units"))]), "*isn't in group_by*"
        },
    };

    [Theory]
    [MemberData(nameof(RejectedAggregates))]
    public void CompileAggregate_InvalidRequest_IsRejected(TableAggregateRequest request, string message)
    {
        Action act = () => TableQueryCompiler.CompileAggregate(Sales, request);

        act.Should().Throw<TableQueryException>().WithMessage(message);
    }

    [Fact]
    public void Compile_EveryQuery_BindsAtLeastOnePositionalParameter()
    {
        // Npgsql splits a parameterless command on semicolons; with positional parameters it never does.
        Read().Parameters.Should().NotBeEmpty();
        TableQueryCompiler.CompileAggregate(Sales, new TableAggregateRequest(Sales.Id, [new TableField(null, TableAggregateFunction.Count)]))
            .Parameters.Should().NotBeEmpty();
    }
}
