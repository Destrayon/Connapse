using System.Text;
using Connapse.Core;
using Connapse.Ingestion.Tables;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Tables;

[Trait("Category", "Unit")]
public class JsonTableReaderTests
{
    private sealed record Read(
        IReadOnlyList<TableLoad> Tables, string? Warning, Dictionary<string, List<IReadOnlyList<object?>>> Rows)
    {
        public TableLoad Table(string name) => Tables.Single(t => t.Name == name);

        public List<IReadOnlyList<object?>> RowsOf(string name) => Rows[name];

        public IEnumerable<(string, TableColumnType)> Columns(string name) => Table(name).Columns.Select(c => (c.Name, c.Type));
    }

    private static async Task<Read> ReadAsync(string json, string fileName = "people.json")
    {
        var stream = new MemoryStream(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray());
        var (tables, warning, owner) = await JsonTableReader.ReadAsync(stream, fileName, documentId: null);
        using (owner)
        {
            Dictionary<string, List<IReadOnlyList<object?>>> rows = [];
            foreach (TableLoad table in tables)
            {
                List<IReadOnlyList<object?>> list = [];
                await foreach (IReadOnlyList<object?> row in table.Rows)
                    list.Add(row);
                rows[table.Name] = list;
            }
            return new Read(tables, warning, rows);
        }
    }

    [Fact]
    public async Task ReadAsync_FlatArray_TypesEachKeyAndLeavesMissingKeysEmpty()
    {
        Read read = await ReadAsync("""
            [
              {"id": 1, "name": "Ann", "score": 2.5, "joined": "2024-01-31", "active": true},
              {"id": 2, "name": "Bob", "score": 3, "active": false, "note": null},
              {"id": 3, "name": null, "score": 1e2, "joined": "2024-02-01", "active": true, "note": "late"}
            ]
            """);

        read.Warning.Should().BeNull();
        read.Tables.Should().ContainSingle();
        TableLoad load = read.Table("people");
        load.Description.Should().BeNull();
        read.Columns("people").Should().Equal(
            ("id", TableColumnType.WholeNumber), ("name", TableColumnType.Text), ("score", TableColumnType.Number),
            ("joined", TableColumnType.Date), ("active", TableColumnType.Boolean), ("note", TableColumnType.Text));
        List<IReadOnlyList<object?>> rows = read.RowsOf("people");
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(1L, "Ann", 2.5m, new DateOnly(2024, 1, 31), true, null);
        rows[1].Should().Equal(2L, "Bob", 3m, null, false, null);
        rows[2].Should().Equal(3L, null, 100m, new DateOnly(2024, 2, 1), true, "late");
    }

    [Fact]
    public async Task ReadAsync_KeyWithMixedKinds_IsText()
    {
        Read read = await ReadAsync("""[{"v": 1}, {"v": "two"}, {"v": true}]""");

        read.Table("people").Columns[0].Type.Should().Be(TableColumnType.Text);
        read.RowsOf("people").Select(r => r[0]).Should().Equal("1", "two", "true");
    }

    [Theory]
    [InlineData("""{"rows": [{"a": 1}]}""")]
    [InlineData("""{"name": "app", "port": 8080, "tags": ["a"]}""")]
    [InlineData("""[]""")]
    [InlineData("""[{}]""")]
    [InlineData("""[null, null]""")]
    [InlineData("""  "just text" """)]
    public async Task ReadAsync_NotATableShape_StaysTextWithoutAWarning(string json)
    {
        Read read = await ReadAsync(json);

        read.Tables.Should().BeEmpty();
        read.Warning.Should().BeNull();
    }

    [Fact]
    public async Task ReadAsync_NestedObject_BecomesDottedColumns()
    {
        Read read = await ReadAsync("""
            [
              {"id": 1, "customer": {"name": "Ann", "address": {"city": "Austin"}}},
              {"id": 2, "customer": {"name": "Bob"}}
            ]
            """, "orders.json");

        read.Tables.Should().ContainSingle();
        read.Columns("orders").Should().Equal(
            ("id", TableColumnType.WholeNumber), ("customer.name", TableColumnType.Text), ("customer.address.city", TableColumnType.Text));
        read.RowsOf("orders").Should().BeEquivalentTo(new[]
        {
            new object?[] { 1L, "Ann", "Austin" },
            new object?[] { 2L, "Bob", null },
        }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task ReadAsync_ListOfObjects_BecomesChildTableJoinedByRowIds()
    {
        Read read = await ReadAsync("""
            [
              {"id": 10, "lines": [{"sku": "A", "qty": 2}, {"sku": "B", "qty": 1}]},
              {"id": 11, "lines": []},
              {"id": 12, "lines": [{"sku": "C", "qty": 5}]}
            ]
            """, "orders.json");

        read.Tables.Select(t => t.Name).Should().Equal("orders", "orders.lines");
        read.Columns("orders").Should().Equal(("_row", TableColumnType.WholeNumber), ("id", TableColumnType.WholeNumber));
        read.Columns("orders.lines").Should().Equal(
            ("_parent_row", TableColumnType.WholeNumber), ("_index", TableColumnType.WholeNumber),
            ("sku", TableColumnType.Text), ("qty", TableColumnType.WholeNumber));
        read.RowsOf("orders").Select(r => (r[0], r[1])).Should().Equal((1L, 10L), (2L, 11L), (3L, 12L));
        read.RowsOf("orders.lines").Select(r => (r[0], r[1], r[2])).Should().Equal((1L, 0L, "A"), (1L, 1L, "B"), (3L, 0L, "C"));
        read.Table("orders.lines").Description.Should().Contain("_parent_row").And.Contain("orders._row");
        read.Table("orders").Columns[0].Description.Should().Contain("orders.lines");
    }

    [Fact]
    public async Task ReadAsync_ListOfPlainValues_BecomesValueTable()
    {
        Read read = await ReadAsync("""[{"id": 1, "tags": ["red", "blue"]}, {"id": 2, "tags": ["red"]}]""", "items.json");

        read.Columns("items.tags").Should().Equal(
            ("_parent_row", TableColumnType.WholeNumber), ("_index", TableColumnType.WholeNumber), ("value", TableColumnType.Text));
        read.RowsOf("items.tags").Select(r => (r[0], r[2])).Should().Equal((1L, "red"), (1L, "blue"), (2L, "red"));
    }

    [Fact]
    public async Task ReadAsync_ArrayOfPlainValues_IsOneValueColumn()
    {
        Read read = await ReadAsync("""[1, 2, 3]""", "n.json");

        read.Columns("n").Should().Equal(("value", TableColumnType.WholeNumber));
        read.RowsOf("n").Select(r => r[0]).Should().Equal(1L, 2L, 3L);
    }

    [Fact]
    public async Task ReadAsync_GrandchildLists_ChainIdsThroughEachLevel()
    {
        Read read = await ReadAsync("""
            [
              {"id": "a", "rooms": [{"n": 1, "beds": [{"s": "king"}]}, {"n": 2, "beds": [{"s": "twin"}, {"s": "twin"}]}]},
              {"id": "b", "rooms": [{"n": 3, "beds": [{"s": "queen"}]}]}
            ]
            """, "hotels.json");

        read.Tables.Select(t => t.Name).Should().Equal("hotels", "hotels.rooms", "hotels.rooms.beds");
        read.Columns("hotels.rooms").Select(c => c.Item1).Should().Equal("_row", "_parent_row", "_index", "n");
        read.RowsOf("hotels.rooms").Select(r => (r[0], r[1], r[3])).Should().Equal((1L, 1L, 1L), (2L, 1L, 2L), (3L, 2L, 3L));
        read.RowsOf("hotels.rooms.beds").Select(r => (r[0], r[2])).Should().Equal((1L, "king"), (2L, "twin"), (2L, "twin"), (3L, "queen"));
    }

    [Fact]
    public async Task ReadAsync_ValueThatIsSometimesAList_IsAListEverywhere()
    {
        Read read = await ReadAsync("""[{"id": 1, "phone": "555-1"}, {"id": 2, "phone": ["555-2", "555-3"]}, {"id": 3}]""", "c.json");

        read.Columns("c").Select(c => c.Item1).Should().Equal("_row", "id");
        read.RowsOf("c.phone").Select(r => (r[0], r[1], r[2])).Should().Equal((1L, 0L, "555-1"), (2L, 0L, "555-2"), (2L, 1L, "555-3"));
    }

    [Fact]
    public async Task ReadAsync_PlainValueWhereOthersHaveAnObject_GoesInValueColumn()
    {
        Read read = await ReadAsync("""[{"price": 5}, {"price": {"value": 7, "currency": "EUR"}}]""", "p.json");

        read.Columns("p").Should().Equal(("price.value", TableColumnType.WholeNumber), ("price.currency", TableColumnType.Text));
        read.RowsOf("p").Select(r => (r[0], r[1])).Should().Equal((5L, null), (7L, "EUR"));
    }

    [Fact]
    public async Task ReadAsync_ListOfLists_StaysWholeAsJson()
    {
        Read read = await ReadAsync("""[{"id": 1, "grid": [[1, 2], [3]]}, {"id": 2}]""", "g.json");

        read.Tables.Should().ContainSingle(t => t.Name == "g.grid");
        read.Columns("g.grid").Should().Equal(
            ("_parent_row", TableColumnType.WholeNumber), ("_index", TableColumnType.WholeNumber), ("value", TableColumnType.Json));
        read.RowsOf("g.grid").Select(r => r[2]).Should().Equal("[1, 2]", "[3]");
    }

    [Fact]
    public async Task ReadAsync_ObjectWithTooManyKeys_StaysWholeAsJson()
    {
        string wide = "{" + string.Join(",", Enumerable.Range(0, TreeFlattener.MaxKeys + 1).Select(i => $"\"k{i}\": {i}")) + "}";
        Read read = await ReadAsync("[{\"id\": 1, \"attrs\": " + wide + "}, {\"id\": 2, \"attrs\": {\"k0\": 0}}]", "w.json");

        read.Columns("w").Should().Equal(("id", TableColumnType.WholeNumber), ("attrs", TableColumnType.Json));
        read.RowsOf("w")[1][1].Should().Be("""{"k0": 0}""");
    }

    [Fact]
    public async Task ReadAsync_ListsPastTheTableDepth_StayWholeAsJson()
    {
        string json = "{\"v\": 1}";
        for (int i = 0; i < TreeFlattener.MaxTableDepth + 1; i++)
            json = $"{{\"v\": {i}, \"l\": [{json}, {json}]}}";
        Read read = await ReadAsync($"[{json}]", "d.json");

        read.Tables.Should().HaveCount(TreeFlattener.MaxTableDepth);
        read.Tables[^1].Columns.Should().Contain(c => c.Name == "l" && c.Type == TableColumnType.Json);
    }

    [Fact]
    public async Task ReadAsync_JsonWithNulCharacter_DropsItForJsonb()
    {
        Read read = await ReadAsync("""[{"g": [["a\u0000b"]]}, {"g": [["c"]]}]""", "z.json");

        read.RowsOf("z.g")[0][2].Should().Be("""["ab"]""");
    }

    [Fact]
    public async Task ReadAsync_TopLevelObjectWithList_LoadsListAndKeepsTheObjectsFields()
    {
        Read read = await ReadAsync("""
            {"store": "North", "employees": [{"name": "Ann"}, {"name": "Bob"}]}
            """, "staff.json");

        read.Tables.Select(t => t.Name).Should().Equal("staff", "staff.employees");
        read.Columns("staff").Should().Equal(("store", TableColumnType.Text));
        read.Columns("staff.employees").Select(c => c.Item1).Should().Equal("_index", "name");
        read.RowsOf("staff.employees").Select(r => r[1]).Should().Equal("Ann", "Bob");
    }

    [Fact]
    public async Task ReadAsync_TopLevelObjectOnlyWrappingAList_LoadsOnlyTheList()
    {
        Read read = await ReadAsync("""{"data": [{"a": 1}, {"a": 2}]}""", "api.json");

        read.Tables.Select(t => t.Name).Should().Equal("api.data");
        read.Columns("api.data").Should().Equal(("_index", TableColumnType.WholeNumber), ("a", TableColumnType.WholeNumber));
    }

    [Fact]
    public async Task ReadAsync_JsonLines_ReadsOneRecordPerLineAndRejectsBadLines()
    {
        Read read = await ReadAsync("{\"a\": 1, \"t\": [\"x\"]}\r\n\n{\"a\": 2}\nnot json\n{\"a\": 3, \"t\": [\"y\", \"z\"]}\n", "events.jsonl");

        read.Tables.Select(t => t.Name).Should().Equal("events", "events.t");
        read.RowsOf("events").Select(r => (r[0], r[1])).Should().Equal((1L, 1L), (2L, 2L), (3L, 3L));
        read.RowsOf("events.t").Select(r => (r[0], r[2])).Should().Equal((1L, "x"), (3L, "y"), (3L, "z"));
        TableLoad events = read.Table("events");
        events.NeedsReview.Should().BeTrue();
        events.Rejects.Should().ContainSingle().Which.Row.Should().Be(4);
        read.Table("events.t").Rejects.Should().BeNull();
    }

    [Fact]
    public async Task ReadAsync_JsonLinesNamedJson_IsReadAsJsonLines()
    {
        Read read = await ReadAsync("{\"a\": 1}\n{\"a\": 2}\n", "log.json");

        read.RowsOf("log").Select(r => r[0]).Should().Equal(1L, 2L);
    }

    [Fact]
    public async Task ReadAsync_Malformed_Throws()
    {
        Func<Task> read = () => ReadAsync("""[{"a": 1},""");

        await read.Should().ThrowAsync<System.Text.Json.JsonException>();
    }

    [Fact]
    public async Task ReadAsync_RowsReadTwice_GiveTheSameIds()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("""[{"l": [{"x": 1}, {"x": 2}]}, {"l": [{"x": 3}]}]"""));
        var (tables, _, _) = await JsonTableReader.ReadAsync(stream, "r.json", documentId: null);
        TableLoad child = tables.Single(t => t.Name == "r.l");

        List<string> first = [], second = [];
        await foreach (IReadOnlyList<object?> row in child.Rows)
            first.Add(string.Join(",", row));
        await foreach (IReadOnlyList<object?> row in child.Rows)
            second.Add(string.Join(",", row));

        second.Should().Equal(first);
        first.Should().Equal("1,0,1", "1,1,2", "2,0,3");
    }
}
