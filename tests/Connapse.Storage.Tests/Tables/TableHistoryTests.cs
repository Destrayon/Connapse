using System.Text.Json;
using Connapse.Core;
using Connapse.Storage.Tables;
using FluentAssertions;
using NpgsqlTypes;

namespace Connapse.Storage.Tests.Tables;

[Trait("Category", "Unit")]
public class TableHistoryTests
{
    private static readonly Guid ContainerId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string Schema = "tables_11111111222233334444555555555555";
    private const string Storage = "t_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string From = $"FROM \"{Schema}\".\"{Storage}\"";
    private static readonly DateTime HistoryFrom = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly TableColumn[] Columns =
    [
        new("sku", TableColumnType.Text),
        new("name", TableColumnType.Text),
        new("price", TableColumnType.Number),
    ];

    private static TableInfo Table(string? key = "sku", DateTime? historyFrom = null) => new(
        Guid.NewGuid(), ContainerId, "products", null, null, Columns, 10, false, [], Schema, Storage, 1,
        DateTime.UtcNow, DateTime.UtcNow, key, key is null ? null : historyFrom ?? HistoryFrom);

    private static JsonElement Json(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData("id")]
    [InlineData("ID")]
    [InlineData("order_id")]
    [InlineData("Order ID")]
    [InlineData("customer-id")]
    [InlineData("CustomerID")]
    [InlineData("orderId")]
    [InlineData("sku")]
    [InlineData("Product Code")]
    [InlineData("uuid")]
    [InlineData("key")]
    public void IsKeyName_IdentifierHeader_IsTrue(string name) => TableHistory.IsKeyName(name).Should().BeTrue();

    [Theory]
    [InlineData("paid")]
    [InlineData("ANDROID")]
    [InlineData("valid")]
    [InlineData("monkey")]
    [InlineData("barcode")]
    [InlineData("identity")]
    [InlineData("")]
    public void IsKeyName_OtherHeader_IsFalse(string name) => TableHistory.IsKeyName(name).Should().BeFalse();

    [Fact]
    public void KeyCandidates_SkipsWrongTypesAndSharedHeaders()
    {
        TableColumn[] columns =
        [
            new("amount", TableColumnType.Number),
            new("id", TableColumnType.Number),
            new("code", TableColumnType.Text),
            new("code", TableColumnType.Text),
            new("order_id", TableColumnType.WholeNumber),
            new("sku", TableColumnType.Text),
        ];

        TableHistory.KeyCandidates(columns).Should().Equal(4, 5);
    }

    [Fact]
    public void KeyCandidates_NoRoomForHistoryColumns_IsEmpty()
    {
        TableColumn[] columns = [new("id", TableColumnType.Text), .. Enumerable.Range(0, TableDdl.MaxColumns - 2)
            .Select(i => new TableColumn("v" + i, TableColumnType.Text))];

        TableHistory.KeyCandidates(columns).Should().BeEmpty();
    }

    [Fact]
    public void CloseSql_ClosesCurrentVersionsWithoutAnIdenticalIncomingRow()
    {
        TableHistory.CloseSql("E", "N", 0, 2).Should().Be(
            "UPDATE E AS e SET \"_seen_until\" = $1 WHERE e.\"_seen_until\" = 'infinity' "
            + "AND NOT EXISTS (SELECT 1 FROM N AS i WHERE i.\"c_1\" = e.\"c_1\" "
            + "AND ROW(i.\"c_1\", i.\"c_2\") IS NOT DISTINCT FROM ROW(e.\"c_1\", e.\"c_2\"))");
    }

    [Fact]
    public void InsertSql_AddsRowsWhoseKeyHasNoCurrentVersion()
    {
        TableHistory.InsertSql("E", "N", 1, 2).Should().Be(
            "INSERT INTO E (\"c_1\", \"c_2\", \"_seen_from\") SELECT i.\"c_1\", i.\"c_2\", $1 FROM N AS i "
            + "WHERE NOT EXISTS (SELECT 1 FROM E AS e WHERE e.\"_seen_until\" = 'infinity' AND e.\"c_2\" = i.\"c_2\")");
    }

    [Fact]
    public void CreateTableSql_History_AddsSeenColumns()
    {
        TableDdl.CreateTableSql(ContainerId, Storage, Columns, history: true).Should().EndWith(
            "\"c_3\" numeric, \"_seen_from\" timestamptz NOT NULL, \"_seen_until\" timestamptz NOT NULL DEFAULT 'infinity')");
    }

    [Fact]
    public void CompileRead_HistoryNoAsOf_ReadsCurrentVersions()
    {
        CompiledTableQuery query = TableQueryCompiler.CompileRead(Table(), new TableReadRequest(Guid.Empty, ["price"]));

        query.Sql.Should().Be($"SELECT \"c_3\" {From} WHERE \"_seen_until\" = 'infinity' LIMIT $1 OFFSET $2");
    }

    [Fact]
    public void CompileRead_HistoryAsOfWithFilter_BindsTimeOnceAndWrapsFilter()
    {
        DateTimeOffset at = new(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(2));
        var where = new TableCondition(new TableField("price"), TableFilterOperator.Greater, [5m]);

        CompiledTableQuery query = TableQueryCompiler.CompileRead(Table(), new TableReadRequest(Guid.Empty, ["price"], where, SeenAsOf: at));

        query.Sql.Should().Be(
            $"SELECT \"c_3\" {From} WHERE \"_seen_from\" <= $1 AND \"_seen_until\" > $1 AND (\"c_3\" > $2) LIMIT $3 OFFSET $4");
        query.Parameters[0].Value.Should().Be(new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc));
        query.Parameters[0].NpgsqlDbType.Should().Be(NpgsqlDbType.TimestampTz);
    }

    [Fact]
    public void CompileAggregate_HistoryAsOf_FiltersVersions()
    {
        var request = new TableAggregateRequest(Guid.Empty, [new TableField(null, TableAggregateFunction.Count)],
            SeenAsOf: HistoryFrom.AddDays(1));

        TableQueryCompiler.CompileAggregate(Table(), request).Sql.Should().Be(
            $"SELECT pg_catalog.count(*) {From} WHERE \"_seen_from\" <= $1 AND \"_seen_until\" > $1 LIMIT $2");
    }

    [Fact]
    public void CompileRead_AsOfOnKeylessTable_SaysItKeepsNoHistory()
    {
        var act = () => TableQueryCompiler.CompileRead(Table(key: null), new TableReadRequest(Guid.Empty, SeenAsOf: HistoryFrom));

        act.Should().Throw<TableQueryException>().WithMessage("*has no key column*keeps no history*seen_as_of*");
    }

    [Fact]
    public void CompileRead_KeylessTable_SqlIsUnchanged()
    {
        TableQueryCompiler.CompileRead(Table(key: null), new TableReadRequest(Guid.Empty, ["price"])).Sql
            .Should().Be($"SELECT \"c_3\" {From} LIMIT $1 OFFSET $2");
    }

    [Fact]
    public void CompileRead_AsOfBeforeHistory_SaysWhenHistoryStarts()
    {
        var act = () => TableQueryCompiler.CompileRead(Table(), new TableReadRequest(Guid.Empty, SeenAsOf: HistoryFrom.AddSeconds(-1)));

        act.Should().Throw<TableQueryException>().WithMessage("*history from 2026-11-01T00:00:00.000000Z on*");
    }

    [Fact]
    public void CompileChanges_ComparesChosenColumnsAndKeepsTheKey()
    {
        var request = new TableChangesRequest(Guid.Empty, HistoryFrom, Columns: ["price"]);

        CompiledTableQuery query = TableQueryCompiler.CompileChanges(Table(), request);

        query.Columns.Should().Equal("change", "version", "sku", "price");
        query.Sql.Should().Be(
            $"WITH old AS (SELECT \"c_1\", \"c_3\" {From} WHERE \"_seen_from\" <= $1 AND \"_seen_until\" > $1), "
            + $"new AS (SELECT \"c_1\", \"c_3\" {From} WHERE \"_seen_until\" = 'infinity') "
            + "SELECT d.change, d.version, pg_catalog.substr(d.v_0, 1, 2001), d.v_1 FROM ("
            + "SELECT CASE WHEN n.\"c_1\" IS NULL THEN 'removed' ELSE 'changed' END AS change, 'old' AS version, 0 AS side, "
            + "o.\"c_1\" AS sort_key, o.\"c_1\" AS v_0, o.\"c_3\" AS v_1 FROM old AS o LEFT JOIN new AS n ON n.\"c_1\" = o.\"c_1\" "
            + "WHERE n.\"c_1\" IS NULL OR ROW(o.\"c_1\", o.\"c_3\") IS DISTINCT FROM ROW(n.\"c_1\", n.\"c_3\") UNION ALL "
            + "SELECT CASE WHEN o.\"c_1\" IS NULL THEN 'added' ELSE 'changed' END AS change, 'new' AS version, 1 AS side, "
            + "n.\"c_1\" AS sort_key, n.\"c_1\" AS v_0, n.\"c_3\" AS v_1 FROM new AS n LEFT JOIN old AS o ON o.\"c_1\" = n.\"c_1\" "
            + "WHERE o.\"c_1\" IS NULL OR ROW(o.\"c_1\", o.\"c_3\") IS DISTINCT FROM ROW(n.\"c_1\", n.\"c_3\")"
            + ") AS d ORDER BY d.sort_key, d.side LIMIT $2 OFFSET $3");
    }

    [Fact]
    public void CompileChanges_Until_ReadsNewSideAsOfUntil()
    {
        var request = new TableChangesRequest(Guid.Empty, HistoryFrom, HistoryFrom.AddDays(2));

        CompiledTableQuery query = TableQueryCompiler.CompileChanges(Table(), request);

        query.Sql.Should().Contain("new AS (SELECT \"c_1\", \"c_2\", \"c_3\" " + From + " WHERE \"_seen_from\" <= $2 AND \"_seen_until\" > $2)");
        query.Parameters.Select(p => p.Value).Should().Equal(HistoryFrom, HistoryFrom.AddDays(2), 101L, 0L);
    }

    [Fact]
    public void CompileChanges_UntilBeforeSince_IsRefused()
    {
        var act = () => TableQueryCompiler.CompileChanges(Table(), new TableChangesRequest(Guid.Empty, HistoryFrom.AddDays(1), HistoryFrom));

        act.Should().Throw<TableQueryException>().WithMessage("until can't be earlier than since.");
    }

    [Fact]
    public void CompileChanges_KeylessTable_SaysItKeepsNoHistory()
    {
        var act = () => TableQueryCompiler.CompileChanges(Table(key: null), new TableChangesRequest(Guid.Empty, HistoryFrom));

        act.Should().Throw<TableQueryException>().WithMessage("*has no key column*can't ask what changed*");
    }

    [Fact]
    public void ParseRead_SeenAsOf_ParsesIsoTime()
    {
        TableReadRequest request = TableRequestJson.ParseRead(Json(
            """{"table": "6f9619ff-8b86-d011-b42d-00c04fc964ff", "seen_as_of": "2026-11-05T10:00:00Z"}"""));

        request.SeenAsOf.Should().Be(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ParseChanges_FullRequest_MapsEveryPart()
    {
        TableChangesRequest request = TableRequestJson.ParseChanges(Json("""
            {"table": "6f9619ff-8b86-d011-b42d-00c04fc964ff", "since": "2026-11-01T00:00:00", "until": "2026-11-02T00:00:00+01:00",
             "columns": ["price"], "limit": 5, "offset": 10}
            """));

        request.Since.Should().Be(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        request.Until.Should().Be(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.FromHours(1)));
        request.Columns.Should().Equal("price");
        request.Limit.Should().Be(5);
        request.Offset.Should().Be(10);
    }

    [Theory]
    [InlineData("""{"table": "6f9619ff-8b86-d011-b42d-00c04fc964ff"}""", "*needs \"since\"*")]
    [InlineData("""{"table": "6f9619ff-8b86-d011-b42d-00c04fc964ff", "since": "last week"}""", "\"since\" must be an ISO 8601 time*")]
    [InlineData("""{"table": "6f9619ff-8b86-d011-b42d-00c04fc964ff", "since": 5}""", "\"since\" must be an ISO 8601 time*")]
    [InlineData("""{"table": "6f9619ff-8b86-d011-b42d-00c04fc964ff", "since": "2026-11-01", "where": {}}""", "*unknown key \"where\"*")]
    public void ParseChanges_BadRequest_SaysWhatToFix(string json, string message)
    {
        var act = () => TableRequestJson.ParseChanges(Json(json));

        act.Should().Throw<TableQueryException>().WithMessage(message);
    }
}
