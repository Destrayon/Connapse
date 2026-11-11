using System.Text;
using Connapse.Core;
using Connapse.Ingestion.Tables;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Tables;

[Trait("Category", "Unit")]
public class JsonTableReaderTests
{
    private static async Task<(TableLoad? Load, string? Warning, List<IReadOnlyList<object?>> Rows)> ReadAsync(string json, string fileName = "people.json")
    {
        var stream = new MemoryStream(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray());
        var (load, warning) = await JsonTableReader.ReadAsync(stream, fileName, documentId: null);
        List<IReadOnlyList<object?>> rows = [];
        if (load is not null)
            await foreach (IReadOnlyList<object?> row in load.Rows)
                rows.Add(row);
        return (load, warning, rows);
    }

    [Fact]
    public async Task ReadAsync_FlatArray_TypesEachKeyAndLeavesMissingKeysEmpty()
    {
        var (load, warning, rows) = await ReadAsync("""
            [
              {"id": 1, "name": "Ann", "score": 2.5, "joined": "2024-01-31", "active": true},
              {"id": 2, "name": "Bob", "score": 3, "active": false, "note": null},
              {"id": 3, "name": null, "score": 1e2, "joined": "2024-02-01", "active": true, "note": "late"}
            ]
            """);

        warning.Should().BeNull();
        load!.Name.Should().Be("people");
        load.Columns.Select(c => (c.Name, c.Type)).Should().Equal(
            ("id", TableColumnType.WholeNumber), ("name", TableColumnType.Text), ("score", TableColumnType.Number),
            ("joined", TableColumnType.Date), ("active", TableColumnType.Boolean), ("note", TableColumnType.Text));
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(1L, "Ann", 2.5m, new DateOnly(2024, 1, 31), true, null);
        rows[1].Should().Equal(2L, "Bob", 3m, null, false, null);
        rows[2].Should().Equal(3L, null, 100m, new DateOnly(2024, 2, 1), true, "late");
    }

    [Fact]
    public async Task ReadAsync_KeyWithMixedKinds_IsText()
    {
        var (load, _, rows) = await ReadAsync("""[{"v": 1}, {"v": "two"}, {"v": true}]""");

        load!.Columns[0].Type.Should().Be(TableColumnType.Text);
        rows.Select(r => r[0]).Should().Equal("1", "two", "true");
    }

    [Theory]
    [InlineData("""{"rows": [{"a": 1}]}""")]
    [InlineData("""[]""")]
    [InlineData("""[{}]""")]
    public async Task ReadAsync_NotATableShape_StaysTextWithoutAWarning(string json)
    {
        var (load, warning, _) = await ReadAsync(json);

        load.Should().BeNull();
        warning.Should().BeNull();
    }

    [Theory]
    [InlineData("""[{"a": 1, "b": {"c": 2}}]""", "nested")]
    [InlineData("""[{"a": [1, 2]}]""", "nested")]
    [InlineData("""[1, 2, 3]""", "numbers")]
    public async Task ReadAsync_ArrayThatIsntFlatObjects_StaysTextWithAWarning(string json, string says)
    {
        var (load, warning, _) = await ReadAsync(json);

        load.Should().BeNull();
        warning.Should().Contain(says);
    }

    [Fact]
    public async Task ReadAsync_Malformed_Throws()
    {
        Func<Task> read = () => ReadAsync("""[{"a": 1},""");

        await read.Should().ThrowAsync<System.Text.Json.JsonException>();
    }
}
