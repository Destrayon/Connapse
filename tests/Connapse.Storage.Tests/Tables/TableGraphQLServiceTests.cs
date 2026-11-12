using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Tables;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Storage.Tests.Tables;

/// <summary>
/// CON-336: the schema a caller sees, what a GraphQL query becomes as structured table requests, and what is refused
/// before any request is made. Access trimming inside each request is the query service's, covered by its own tests and
/// the integration leak tests.
/// </summary>
[Trait("Category", "Unit")]
public class TableGraphQLServiceTests
{
    private static readonly Guid ContainerId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Readable = Guid.NewGuid();
    private static readonly Guid Unreadable = Guid.NewGuid();

    private static TableInfo Table(string name, Guid document, params TableColumn[] columns) => new(
        Guid.NewGuid(), ContainerId, name, null, document, columns, 3, false, [], "s", "t_" + name, 1,
        DateTime.UtcNow, DateTime.UtcNow);

    private static readonly TableInfo Sales = Table("Sales 2025", Readable,
        new TableColumn("Region", TableColumnType.Text), new TableColumn("Amount", TableColumnType.Number), new TableColumn("Sold on", TableColumnType.Date),
        new TableColumn("Extra", TableColumnType.Json), new TableColumn("Margin", TableColumnType.Number));

    private static readonly TableInfo Payroll = Table("Payroll", Unreadable, new TableColumn("Salary", TableColumnType.Number));

    private static readonly TableAccess Access = new(new HashSet<Guid> { Readable });

    /// <summary>A query service over rows held in memory: returns the requested columns, honoring In filters and limit.</summary>
    private sealed class Fake : ITableQueryService
    {
        public Dictionary<Guid, List<Dictionary<string, object?>>> Rows { get; } = [];

        public List<TableReadRequest> Reads { get; } = [];

        public List<TableAggregateRequest> Aggregates { get; } = [];

        public TableQueryResult? AggregateAnswer { get; set; }

        public Task<TableQueryResult> ReadAsync(Guid containerId, TableAccess access, TableReadRequest request, CancellationToken ct = default)
        {
            Reads.Add(request);
            IEnumerable<Dictionary<string, object?>> rows = Rows.GetValueOrDefault(request.TableId) ?? [];
            if (Ins(request.Where) is { } ins)
                rows = rows.Where(r => ins.Values.Contains(r.GetValueOrDefault(ins.Field.Column!)));
            List<string> columns = request.Columns!.ToList();
            List<IReadOnlyList<object?>> result = rows.Take(request.Limit ?? 100)
                .Select(r => (IReadOnlyList<object?>)columns.Select(c => r.GetValueOrDefault(c)).ToList()).ToList();
            return Task.FromResult(new TableQueryResult(columns, result, false, null));
        }

        private static TableCondition? Ins(TableFilter? filter) => filter switch
        {
            TableCondition { Operator: TableFilterOperator.In } c => c,
            TableAndFilter and => and.All.Select(Ins).FirstOrDefault(c => c is not null),
            _ => null,
        };

        public Task<TableQueryResult> AggregateAsync(Guid containerId, TableAccess access, TableAggregateRequest request, CancellationToken ct = default)
        {
            Aggregates.Add(request);
            return Task.FromResult(AggregateAnswer ?? new TableQueryResult([], [], false, null));
        }

        public Task<TableQueryResult> ChangesAsync(Guid containerId, TableAccess access, TableChangesRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TableDescription> DescribeAsync(Guid containerId, TableAccess access, Guid tableId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(Guid containerId, TableAccess access, Guid tableId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static (TableGraphQLService Service, Fake Queries) Service(params TableInfo[] tables)
    {
        ITableStore store = Substitute.For<ITableStore>();
        store.ListAsync(ContainerId, Arg.Any<CancellationToken>()).Returns(tables);
        Fake fake = new();
        return (new TableGraphQLService(store, fake, NullLogger<TableGraphQLService>.Instance), fake);
    }

    [Fact]
    public async Task RunAsync_UncheckedAccess_RefusesWithoutListingTables()
    {
        (TableGraphQLService service, _) = Service(Sales);

        Func<Task> act = () => service.RunAsync(ContainerId, TableAccess.Unchecked, "{ _schema }");

        (await act.Should().ThrowAsync<TableQueryException>()).Which.Message.Should().Be(TableAccess.UncheckedMessage);
    }

    [Fact]
    public async Task RunAsync_Schema_NamesOnlyReadableTablesAndVisibleColumns()
    {
        TableInfo hiding = Sales with { HiddenColumns = ["Margin"] };
        (TableGraphQLService service, _) = Service(hiding, Payroll);

        TableGraphQLResult result = await service.RunAsync(ContainerId, Access, "{ _schema }");

        string schema = result.Data.GetProperty("_schema").GetString()!;
        schema.Should().Contain("type Sales_2025 { # \"Sales 2025\", 3 rows");
        schema.Should().Contain("Sold_on: Date # \"Sold on\"");
        schema.Should().Contain("Extra: JSON");
        schema.Should().NotContain("Margin").And.NotContain("Payroll").And.NotContain("Salary");
    }

    [Theory]
    [InlineData("{ Payroll { Salary } }")]
    [InlineData("{ Payroll_aggregate { count } }")]
    [InlineData("{ _schema(table: \"Payroll\") }")]
    [InlineData("{ Sales_2025(join: {table: Payroll, on: {Region: Salary}}) { Region } }")]
    [InlineData("{ Sales_2025 { Region Payroll(on: {Region: Salary}) { Salary } } }")]
    public async Task RunAsync_UnreadableTable_IsUnknownAndNoRequestIsMade(string query)
    {
        (TableGraphQLService service, Fake queries) = Service(Sales, Payroll);

        Func<Task> act = () => service.RunAsync(ContainerId, Access, query);

        string message = (await act.Should().ThrowAsync<TableQueryException>()).Which.Message;
        message.Should().Contain("Payroll").And.NotContain("Salary:");
        queries.Reads.Should().BeEmpty();
        queries.Aggregates.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_HiddenColumn_IsUnknownAndNoRequestIsMade()
    {
        (TableGraphQLService service, Fake queries) = Service(Sales with { HiddenColumns = ["Margin"] });

        Func<Task> select = () => service.RunAsync(ContainerId, Access, "{ Sales_2025 { Margin } }");
        Func<Task> filter = () => service.RunAsync(ContainerId, Access, "{ Sales_2025(where: {Margin: {_gt: 1}}) { Region } }");
        Func<Task> sum = () => service.RunAsync(ContainerId, Access, "{ Sales_2025_aggregate { sum { Margin } } }");

        (await select.Should().ThrowAsync<TableQueryException>()).Which.Message.Should().NotContain("Margin,");
        await filter.Should().ThrowAsync<TableQueryException>();
        await sum.Should().ThrowAsync<TableQueryException>();
        queries.Reads.Should().BeEmpty();
        queries.Aggregates.Should().BeEmpty();
    }

    [Theory]
    [InlineData("mutation { Sales_2025 { Region } }", "Only queries")]
    [InlineData("subscription { Sales_2025 { Region } }", "Only queries")]
    [InlineData("query Q($r: String) { Sales_2025(where: {Region: {_eq: $r}}) { Region } }", "Variables")]
    [InlineData("{ ...F } fragment F on Query { Sales_2025 { Region } }", "Fragments")]
    [InlineData("{ Sales_2025 { ... on Sales_2025 { Region } } }", "Fragments")]
    [InlineData("{ __schema { types { name } } }", "Introspection")]
    [InlineData("{ Sales_2025 { __typename } }", "Introspection")]
    [InlineData("{ Sales_2025 @include(if: true) { Region } }", "Directives")]
    [InlineData("{ a: _schema } { b: _schema }", "exactly one query")]
    [InlineData("{ Sales_2025 { Region ", "isn't valid GraphQL")]
    [InlineData("{ Sales_2025(limit: 5000) { Region } }", "limit must be")]
    [InlineData("{ Sales_2025(drop: true) { Region } }", "takes no argument")]
    public async Task RunAsync_UnsupportedQuery_IsRefusedWithAReason(string query, string reason)
    {
        (TableGraphQLService service, Fake queries) = Service(Sales);

        Func<Task> act = () => service.RunAsync(ContainerId, Access, query);

        (await act.Should().ThrowAsync<TableQueryException>()).Which.Message.Should().Contain(reason);
        queries.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_TooManyRootFields_IsRefused()
    {
        (TableGraphQLService service, _) = Service(Sales);
        string query = "{ " + string.Join(" ", Enumerable.Range(0, 11).Select(i => $"a{i}: _schema")) + " }";

        Func<Task> act = () => service.RunAsync(ContainerId, Access, query);

        (await act.Should().ThrowAsync<TableQueryException>()).Which.Message.Should().Contain("root fields");
    }

    [Fact]
    public async Task RunAsync_Read_BecomesOneReadRequestAndShapesRowsByAlias()
    {
        (TableGraphQLService service, Fake queries) = Service(Sales);
        queries.Rows[Sales.Id] =
        [
            new() { ["Region"] = "West", ["Amount"] = 12.5m, ["Sold on"] = new DateOnly(2025, 3, 1), ["Extra"] = "{\"tags\":[\"a\"]}" },
        ];

        TableGraphQLResult result = await service.RunAsync(ContainerId, Access,
            """{ top: Sales_2025(where: {Amount: {_gt: 10}, _or: [{Region: {_eq: "West"}}, {Region: {_eq: null}}]}, order_by: [{Amount: desc}], limit: 5, offset: 2) { where: Region Amount Sold_on Extra } }""");

        TableReadRequest read = queries.Reads.Should().ContainSingle().Subject;
        read.TableId.Should().Be(Sales.Id);
        read.Columns.Should().Equal("Region", "Amount", "Sold on", "Extra");
        read.Limit.Should().Be(5);
        read.Offset.Should().Be(2);
        read.OrderBy.Should().Equal(new TableOrder(new TableField("Amount"), TableSortDirection.Descending));
        TableAndFilter where = read.Where.Should().BeOfType<TableAndFilter>().Subject;
        where.All[0].Should().BeEquivalentTo(new TableCondition(new TableField("Amount"), TableFilterOperator.Greater, [10L]));
        TableOrFilter or = where.All[1].Should().BeOfType<TableOrFilter>().Subject;
        or.Any[1].Should().BeEquivalentTo(new TableCondition(new TableField("Region"), TableFilterOperator.IsNull, []));

        JsonElement row = result.Data.GetProperty("top")[0];
        row.GetProperty("where").GetString().Should().Be("West");
        row.GetProperty("Amount").GetDecimal().Should().Be(12.5m);
        row.GetProperty("Sold_on").GetString().Should().Be("2025-03-01");
        row.GetProperty("Extra").GetProperty("tags")[0].GetString().Should().Be("a");
        result.Notice.Should().Be(TableQueryResult.UntrustedDataNotice);
    }

    [Fact]
    public async Task RunAsync_Aggregate_GroupsByPlainColumnsAndMapsAggregatesByPosition()
    {
        (TableGraphQLService service, Fake queries) = Service(Sales);
        queries.AggregateAnswer = new TableQueryResult(["Region", "count", "sum", "max"], [["West", 4L, 50.5m, 20m]], false, null);

        TableGraphQLResult result = await service.RunAsync(ContainerId, Access,
            "{ Sales_2025_aggregate(having: {count: {_gt: 1}}, order_by: {sum: {Amount: desc}}) { Region n: count sum { Amount } max { Amount } } }");

        TableAggregateRequest request = queries.Aggregates.Should().ContainSingle().Subject;
        request.GroupBy.Should().Equal("Region");
        request.Aggregates.Should().Equal(
            new TableField(null, TableAggregateFunction.Count),
            new TableField("Amount", TableAggregateFunction.Sum),
            new TableField("Amount", TableAggregateFunction.Max));
        request.Having.Should().BeEquivalentTo(new TableCondition(new TableField(null, TableAggregateFunction.Count), TableFilterOperator.Greater, [1L]));
        request.OrderBy.Should().Equal(new TableOrder(new TableField("Amount", TableAggregateFunction.Sum), TableSortDirection.Descending));

        JsonElement row = result.Data.GetProperty("Sales_2025_aggregate")[0];
        row.GetProperty("Region").GetString().Should().Be("West");
        row.GetProperty("n").GetInt64().Should().Be(4);
        row.GetProperty("sum").GetProperty("Amount").GetDecimal().Should().Be(50.5m);
        row.GetProperty("max").GetProperty("Amount").GetDecimal().Should().Be(20m);
    }

    [Fact]
    public async Task RunAsync_ChildTable_NestsUnderItsParentInOneBatchedRead()
    {
        TableInfo orders = Table("orders", Readable, new TableColumn("_row", TableColumnType.WholeNumber), new TableColumn("id", TableColumnType.Text));
        TableInfo items = Table("orders.items", Readable,
            new TableColumn("_row", TableColumnType.WholeNumber), new TableColumn("_parent_row", TableColumnType.WholeNumber), new TableColumn("sku", TableColumnType.Text));
        (TableGraphQLService service, Fake queries) = Service(orders, items);
        queries.Rows[orders.Id] = [new() { ["_row"] = 1L, ["id"] = "A" }, new() { ["_row"] = 2L, ["id"] = "B" }];
        queries.Rows[items.Id] =
        [
            new() { ["_parent_row"] = 1L, ["sku"] = "x" },
            new() { ["_parent_row"] = 1L, ["sku"] = "y" },
            new() { ["_parent_row"] = 2L, ["sku"] = "z" },
            new() { ["_parent_row"] = 9L, ["sku"] = "never" },
        ];

        TableGraphQLResult result = await service.RunAsync(ContainerId, Access, "{ orders { id items(limit: 1) { sku } } }");

        queries.Reads.Should().HaveCount(2);
        queries.Reads[1].TableId.Should().Be(items.Id);
        queries.Reads[1].Where.Should().BeEquivalentTo(new TableCondition(new TableField("_parent_row"), TableFilterOperator.In, [1L, 2L]));
        JsonElement rows = result.Data.GetProperty("orders");
        rows[0].GetProperty("items").EnumerateArray().Select(i => i.GetProperty("sku").GetString()).Should().Equal("x");
        rows[1].GetProperty("items").EnumerateArray().Select(i => i.GetProperty("sku").GetString()).Should().Equal("z");
        rows[0].TryGetProperty("_row", out _).Should().BeFalse("only the fields asked for come back");
        result.Truncated.Should().BeTrue("order A had two items and only one is shown");
        result.TruncatedReason.Should().Contain("orders.items");
    }

    [Fact]
    public async Task RunAsync_Join_ReadsTheJoinedColumnsUnderTheJoinedTablesName()
    {
        TableInfo regions = Table("regions", Readable, new TableColumn("code", TableColumnType.Text), new TableColumn("manager", TableColumnType.Text));
        (TableGraphQLService service, Fake queries) = Service(Sales, regions);

        await service.RunAsync(ContainerId, Access,
            "{ Sales_2025(join: {table: regions, on: {Region: code}, kind: left}, where: {regions: {manager: {_eq: \"Ann\"}}}, order_by: {regions: {manager: asc}}) { Amount regions { manager } } }");

        TableReadRequest read = queries.Reads.Should().ContainSingle().Subject;
        read.Join.Should().Be(new TableJoin(regions.Id, "joined", "Region", "code", TableJoinKind.Left));
        read.Columns.Should().Equal("Amount", "joined.manager");
        read.Where.Should().BeEquivalentTo(new TableCondition(new TableField("joined.manager"), TableFilterOperator.Equal, ["Ann"]));
        read.OrderBy.Should().Equal(new TableOrder(new TableField("joined.manager")));
    }
}
