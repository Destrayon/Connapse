using Connapse.Core;
using Connapse.Storage.Tables;
using FluentAssertions;
using NpgsqlTypes;

namespace Connapse.Storage.Tests.Tables;

[Trait("Category", "Unit")]
public class TableJoinCompilerTests
{
    private static readonly Guid ContainerId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string Schema = "tables_11111111222233334444555555555555";
    private static readonly DateTime HistoryFrom = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly TableInfo Orders = Table("orders", "t_orders",
        new TableColumn("id", TableColumnType.WholeNumber),
        new TableColumn("customer_id", TableColumnType.WholeNumber),
        new TableColumn("amount", TableColumnType.Number),
        new TableColumn("note", TableColumnType.Text));

    private static readonly TableInfo Customers = Table("customers", "t_customers",
        new TableColumn("id", TableColumnType.WholeNumber),
        new TableColumn("name", TableColumnType.Text),
        new TableColumn("region", TableColumnType.Text));

    private const string OrdersSql = $"\"{Schema}\".\"t_orders\"";
    private const string CustomersSql = $"\"{Schema}\".\"t_customers\"";

    private static TableInfo Table(string name, string storage, params TableColumn[] columns) => new(
        Guid.NewGuid(), ContainerId, name, null, Guid.NewGuid(), columns, 10, false, [], Schema, storage, 1,
        DateTime.UtcNow, DateTime.UtcNow);

    private static TableJoin Join(TableInfo joined, string column = "customer_id", string joinColumn = "id", TableJoinKind kind = TableJoinKind.Inner) =>
        new(joined.Id, "c", column, joinColumn, kind);

    private static CompiledTableQuery Read(TableReadRequest request, TableInfo? joined = null, IReadOnlyList<Guid>? joinedSources = null,
        TableInfo? table = null, IReadOnlyList<Guid>? readableSources = null) =>
        TableQueryCompiler.CompileRead(table ?? Orders, request, readableSources, joined ?? Customers, joinedSources);

    [Fact]
    public void CompileRead_InnerJoin_QualifiesColumnsAndJoinsOnNamedColumns()
    {
        CompiledTableQuery query = Read(new TableReadRequest(Orders.Id, ["amount", "c.name"], Join: Join(Customers)));

        query.Sql.Should().Be(
            "SELECT t.\"c_3\", pg_catalog.substr(j.\"c_2\", 1, 2001) FROM " + OrdersSql + " AS t INNER JOIN " + CustomersSql
            + " AS j ON t.\"c_2\" = j.\"c_1\" LIMIT $1 OFFSET $2");
        query.Columns.Should().Equal("amount", "c.name");
    }

    [Fact]
    public void CompileRead_LeftJoin_UsesLeftJoin()
    {
        CompiledTableQuery query = Read(new TableReadRequest(Orders.Id, ["id"], Join: Join(Customers, kind: TableJoinKind.Left)));

        query.Sql.Should().Contain(" AS t LEFT JOIN " + CustomersSql + " AS j ON ");
    }

    [Fact]
    public void CompileRead_JoinWithoutColumns_ReturnsBothTablesVisibleColumns()
    {
        TableInfo customers = Customers with { HiddenColumns = ["region"] };

        CompiledTableQuery query = Read(new TableReadRequest(Orders.Id, Join: Join(customers)), customers);

        query.Columns.Should().Equal("id", "customer_id", "amount", "note", "c.id", "c.name");
    }

    [Fact]
    public void CompileRead_JoinedColumnInFilterAndOrder_UsesJoinedAlias()
    {
        CompiledTableQuery query = Read(new TableReadRequest(Orders.Id, ["amount"],
            Where: new TableCondition(new TableField("C.Region"), TableFilterOperator.Equal, ["west"]),
            OrderBy: [new TableOrder(new TableField("c.name"), TableSortDirection.Descending)],
            Join: Join(Customers)));

        query.Sql.Should().Contain(" WHERE j.\"c_3\" = $1 ORDER BY j.\"c_2\" DESC NULLS LAST LIMIT $2 OFFSET $3");
        query.Parameters[0].Value.Should().Be("west");
    }

    [Fact]
    public void CompileAggregate_GroupByJoinedColumn_LabelsWithAlias()
    {
        CompiledTableQuery query = TableQueryCompiler.CompileAggregate(Orders,
            new TableAggregateRequest(Orders.Id, [new TableField("amount", TableAggregateFunction.Sum)], GroupBy: ["c.region"],
                OrderBy: [new TableOrder(new TableField("c.region"))], Join: Join(Customers)),
            joined: Customers);

        query.Sql.Should().Be(
            "SELECT pg_catalog.substr(j.\"c_3\", 1, 2001), pg_catalog.sum(t.\"c_3\") FROM " + OrdersSql + " AS t INNER JOIN "
            + CustomersSql + " AS j ON t.\"c_2\" = j.\"c_1\" GROUP BY j.\"c_3\" ORDER BY j.\"c_3\" ASC NULLS LAST LIMIT $1");
        query.Columns.Should().Equal("c.region", "sum(amount)");
    }

    [Fact]
    public void CompileAggregate_AggregateOverJoinedColumn_Works()
    {
        CompiledTableQuery query = TableQueryCompiler.CompileAggregate(Orders,
            new TableAggregateRequest(Orders.Id, [new TableField("c.name", TableAggregateFunction.CountDistinct)], Join: Join(Customers)),
            joined: Customers);

        query.Sql.Should().StartWith("SELECT pg_catalog.count(DISTINCT j.\"c_2\") FROM");
        query.Columns.Should().Equal("count_distinct(c.name)");
    }

    [Fact]
    public void CompileAggregate_SumOfJoinedTextColumn_NamesItWithAlias()
    {
        Action act = () => TableQueryCompiler.CompileAggregate(Orders,
            new TableAggregateRequest(Orders.Id, [new TableField("c.name", TableAggregateFunction.Sum)], Join: Join(Customers)),
            joined: Customers);

        act.Should().Throw<TableQueryException>().WithMessage("sum doesn't work on \"c.name\", a text column.");
    }

    [Fact]
    public void CompileRead_MergedJoinedTable_FiltersJoinedRowsInOnClause()
    {
        Guid readable = Guid.NewGuid();
        TableInfo merged = Customers with { DocumentId = null, SourceDocuments = [readable, Guid.NewGuid()] };

        CompiledTableQuery query = Read(new TableReadRequest(Orders.Id, ["id"], Join: Join(merged, kind: TableJoinKind.Left)), merged, [readable]);

        query.Sql.Should().Contain(" AS j ON t.\"c_2\" = j.\"c_1\" AND j.\"_document\" = ANY($1) LIMIT $2 OFFSET $3");
        query.Parameters[0].Value.Should().BeEquivalentTo(new[] { readable });
        query.Parameters[0].NpgsqlDbType.Should().Be(NpgsqlDbType.Array | NpgsqlDbType.Uuid);
    }

    [Fact]
    public void CompileRead_MergedBaseTable_QualifiesSourceFilter()
    {
        Guid readable = Guid.NewGuid();
        TableInfo merged = Orders with { DocumentId = null, SourceDocuments = [readable] };

        CompiledTableQuery query = Read(new TableReadRequest(merged.Id, ["id"], Join: Join(Customers)), table: merged, readableSources: [readable]);

        query.Sql.Should().Contain(" WHERE t.\"_document\" = ANY($1) LIMIT");
    }

    [Fact]
    public void CompileRead_MergedJoinedTableWithoutSources_IsRefused()
    {
        TableInfo merged = Customers with { DocumentId = null, SourceDocuments = [Guid.NewGuid()] };

        Action act = () => Read(new TableReadRequest(Orders.Id, Join: Join(merged)), merged);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CompileRead_JoinedTableWithHistory_ShowsItsCurrentRows()
    {
        TableInfo customers = Customers with { Key = "id", HistoryFrom = HistoryFrom };

        CompiledTableQuery query = Read(new TableReadRequest(Orders.Id, ["id"], Join: Join(customers)), customers);

        query.Sql.Should().Contain(" ON t.\"c_2\" = j.\"c_1\" AND j.\"_seen_until\" = 'infinity' LIMIT");
    }

    [Fact]
    public void CompileRead_SeenAsOfWithHistoryOnBothTables_AppliesToBoth()
    {
        TableInfo orders = Orders with { Key = "id", HistoryFrom = HistoryFrom };
        TableInfo customers = Customers with { Key = "id", HistoryFrom = HistoryFrom };
        DateTimeOffset at = HistoryFrom.AddDays(1);

        CompiledTableQuery query = Read(new TableReadRequest(orders.Id, ["id"], SeenAsOf: at, Join: Join(customers)), customers, table: orders);

        query.Sql.Should().Contain(" AND j.\"_seen_from\" <= $1 AND j.\"_seen_until\" > $1 WHERE t.\"_seen_from\" <= $2 AND t.\"_seen_until\" > $2 LIMIT");
    }

    [Fact]
    public void CompileRead_SeenAsOfWithJoinedTableWithoutHistory_ReadsJoinedTableAsNow()
    {
        TableInfo orders = Orders with { Key = "id", HistoryFrom = HistoryFrom };

        CompiledTableQuery query = Read(new TableReadRequest(orders.Id, ["id"], SeenAsOf: HistoryFrom.AddDays(1), Join: Join(Customers)), table: orders);

        query.Sql.Should().Contain(" ON t.\"c_2\" = j.\"c_1\" WHERE t.\"_seen_from\" <= $1");
    }

    [Fact]
    public void CompileRead_SeenAsOfBeforeJoinedTableHistory_IsRejected()
    {
        TableInfo orders = Orders with { Key = "id", HistoryFrom = HistoryFrom.AddDays(-10) };
        TableInfo customers = Customers with { Key = "id", HistoryFrom = HistoryFrom };

        Action act = () => Read(new TableReadRequest(orders.Id, ["id"], SeenAsOf: HistoryFrom.AddDays(-1), Join: Join(customers)), customers, table: orders);

        act.Should().Throw<TableQueryException>().WithMessage("Table \"customers\" has history from*");
    }

    [Fact]
    public void CompileRead_SelfJoin_UsesTwoAliases()
    {
        TableJoin join = new(Orders.Id, "o", "customer_id", "id");

        CompiledTableQuery query = Read(new TableReadRequest(Orders.Id, ["id", "o.id"], Join: join), Orders);

        query.Sql.Should().Be(
            "SELECT t.\"c_1\", j.\"c_1\" FROM " + OrdersSql + " AS t INNER JOIN " + OrdersSql + " AS j ON t.\"c_2\" = j.\"c_1\" LIMIT $1 OFFSET $2");
    }

    [Fact]
    public void CompileRead_JoinColumnTypesDiffer_SaysBothTypes()
    {
        Action act = () => Read(new TableReadRequest(Orders.Id, Join: Join(Customers, column: "customer_id", joinColumn: "name")));

        act.Should().Throw<TableQueryException>().WithMessage(
            "Can't join \"customer_id\", a whole-number column, to \"c.name\", a text column: join columns must have the same type.");
    }

    [Fact]
    public void CompileRead_HiddenJoinColumn_ReadsAsMissing()
    {
        TableInfo customers = Customers with { HiddenColumns = ["id"] };

        Action act = () => Read(new TableReadRequest(Orders.Id, Join: Join(customers)), customers);

        act.Should().Throw<TableQueryException>().WithMessage("Table \"customers\" has no column \"c.id\". Its columns are: \"c.name\", \"c.region\".");
    }

    [Fact]
    public void CompileRead_HiddenJoinedColumnSelected_ReadsAsMissing()
    {
        TableInfo customers = Customers with { HiddenColumns = ["region"] };

        Action act = () => Read(new TableReadRequest(Orders.Id, ["c.region"], Join: Join(customers)), customers);

        act.Should().Throw<TableQueryException>().WithMessage("*has no column \"c.region\"*");
    }

    [Fact]
    public void CompileRead_MissingBaseColumnWithJoin_SaysHowJoinedColumnsAreNamed()
    {
        Action act = () => Read(new TableReadRequest(Orders.Id, ["region"], Join: Join(Customers)));

        act.Should().Throw<TableQueryException>().WithMessage(
            "Table \"orders\" has no column \"region\".*Columns of the joined table \"customers\" are named \"c.<header>\".");
    }

    [Fact]
    public void CompileRead_BaseColumnNamedLikeJoinedColumn_IsAmbiguous()
    {
        TableInfo orders = Table("orders", "t_orders",
            new TableColumn("customer_id", TableColumnType.WholeNumber),
            new TableColumn("c.name", TableColumnType.Text));

        Action act = () => Read(new TableReadRequest(orders.Id, ["c.name"], Join: Join(Customers)), table: orders);

        act.Should().Throw<TableQueryException>().WithMessage("\"c.name\" is ambiguous*Pick a different \"as\"*");
    }

    [Fact]
    public void CompileRead_BareAliasPrefix_ResolvesAgainstBaseTable()
    {
        Action act = () => Read(new TableReadRequest(Orders.Id, ["c."], Join: Join(Customers)));

        act.Should().Throw<TableQueryException>().WithMessage("Table \"orders\" has no column \"c.\"*");
    }

    [Fact]
    public void CompileRead_AmbiguousJoinedColumn_NamesMatchesWithAlias()
    {
        TableInfo customers = Table("customers", "t_customers",
            new TableColumn("id", TableColumnType.WholeNumber),
            new TableColumn("Name", TableColumnType.Text),
            new TableColumn("NAME", TableColumnType.Text));

        Action act = () => Read(new TableReadRequest(Orders.Id, ["c.name"], Join: Join(customers)), customers);

        act.Should().Throw<TableQueryException>().WithMessage("Column \"c.name\" is ambiguous: it matches \"c.Name\", \"c.NAME\". Use the exact header.");
    }

    [Fact]
    public void CompileRead_DuplicateJoinedHeader_IsAmbiguous()
    {
        TableInfo customers = Table("customers", "t_customers",
            new TableColumn("id", TableColumnType.WholeNumber),
            new TableColumn("name", TableColumnType.Text),
            new TableColumn("name", TableColumnType.Text));

        Action act = () => Read(new TableReadRequest(Orders.Id, ["c.name"], Join: Join(customers)), customers);

        act.Should().Throw<TableQueryException>().WithMessage("Column \"c.name\" is ambiguous: the table has 2 columns with that header.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("1c")]
    [InlineData("c-d")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefg")]
    public void CompileRead_BadAlias_IsRejected(string alias)
    {
        Action act = () => Read(new TableReadRequest(Orders.Id, Join: new TableJoin(Customers.Id, alias, "customer_id", "id")));

        act.Should().Throw<TableQueryException>().WithMessage("A join needs \"as\"*");
    }

    [Fact]
    public void CompileRead_NullAlias_IsRejected()
    {
        Action act = () => Read(new TableReadRequest(Orders.Id, Join: new TableJoin(Customers.Id, null!, "customer_id", "id")));

        act.Should().Throw<TableQueryException>().WithMessage("A join needs \"as\"*");
    }

    [Fact]
    public void CompileRead_JoinWithoutJoinedTable_IsRefused()
    {
        Action act = () => TableQueryCompiler.CompileRead(Orders, new TableReadRequest(Orders.Id, Join: Join(Customers)));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CompileRead_JoinedTableIsNotTheNamedOne_IsRefused()
    {
        Action act = () => Read(new TableReadRequest(Orders.Id, Join: Join(Customers)), Orders);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CompileRead_TooManyColumnsTogether_AsksForNamedColumns()
    {
        TableColumn[] wide = Enumerable.Range(0, 900).Select(i => new TableColumn("x" + i, TableColumnType.WholeNumber)).ToArray();
        TableInfo left = Table("left", "t_left", wide);
        TableInfo right = Table("right", "t_right", wide);

        Action act = () => Read(new TableReadRequest(left.Id, Join: new TableJoin(right.Id, "r", "x0", "x0")), right, table: left);

        act.Should().Throw<TableQueryException>().WithMessage("The two tables have 1800 visible columns together*");
    }

    [Fact]
    public void CompileRead_ExactlyMaxColumnsTogether_IsAllowed()
    {
        TableInfo left = Table("left", "t_left", Enumerable.Range(0, 800).Select(i => new TableColumn("x" + i, TableColumnType.WholeNumber)).ToArray());

        CompiledTableQuery query = Read(new TableReadRequest(left.Id, Join: new TableJoin(left.Id, "r", "x0", "x0")), left, table: left);

        query.Columns.Should().HaveCount(1600);
    }

    [Fact]
    public void CompileRead_WithoutJoin_KeepsUnqualifiedSql()
    {
        CompiledTableQuery query = TableQueryCompiler.CompileRead(Orders, new TableReadRequest(Orders.Id, ["id"]));

        query.Sql.Should().Be("SELECT \"c_1\" FROM " + OrdersSql + " LIMIT $1 OFFSET $2");
    }
}
