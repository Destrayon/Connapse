using System.Text.Json;
using Connapse.Core;
using Connapse.Storage.Tables;
using FluentAssertions;

namespace Connapse.Storage.Tests.Tables;

[Trait("Category", "Unit")]
public class TableRequestJsonTests
{
    private const string Id = "6f9619ff-8b86-d011-b42d-00c04fc964ff";

    private static JsonElement Json(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void ParseRead_FullRequest_MapsEveryPart()
    {
        TableReadRequest request = TableRequestJson.ParseRead(Json($$$"""
            {"table": "{{{Id}}}", "columns": ["region", "amount"],
             "where": {"and": [{"column": "amount", "op": ">", "value": 100},
                               {"not": {"column": "region", "op": "in", "values": ["a", "b"]}}]},
             "order_by": [{"column": "amount", "direction": "desc"}], "limit": 20, "offset": 40}
            """));

        request.TableId.Should().Be(Guid.Parse(Id));
        request.Columns.Should().Equal("region", "amount");
        request.Limit.Should().Be(20);
        request.Offset.Should().Be(40);
        request.OrderBy.Should().ContainSingle().Which.Should().Be(new TableOrder(new TableField("amount"), TableSortDirection.Descending));
        TableAndFilter and = request.Where.Should().BeOfType<TableAndFilter>().Subject;
        TableCondition first = and.All[0].Should().BeOfType<TableCondition>().Subject;
        first.Operator.Should().Be(TableFilterOperator.Greater);
        ((JsonElement)first.Values[0]!).GetInt32().Should().Be(100);
        TableCondition inner = and.All[1].Should().BeOfType<TableNotFilter>().Subject.Inner.Should().BeOfType<TableCondition>().Subject;
        inner.Operator.Should().Be(TableFilterOperator.In);
        inner.Values.Should().HaveCount(2);
    }

    [Fact]
    public void ParseAggregate_FullRequest_MapsEveryPart()
    {
        TableAggregateRequest request = TableRequestJson.ParseAggregate(Json($$$"""
            {"table": "{{{Id}}}", "aggregates": [{"aggregate": "count"}, {"aggregate": "sum", "column": "amount"}],
             "group_by": ["region"], "having": {"aggregate": "count", "op": ">=", "value": 5},
             "order_by": [{"aggregate": "sum", "column": "amount", "direction": "desc"}], "limit": 10}
            """));

        request.Aggregates.Should().Equal(new TableField(null, TableAggregateFunction.Count), new TableField("amount", TableAggregateFunction.Sum));
        request.GroupBy.Should().Equal("region");
        request.Having.Should().BeOfType<TableCondition>().Which.Field.Aggregate.Should().Be(TableAggregateFunction.Count);
        request.OrderBy![0].Field.Should().Be(new TableField("amount", TableAggregateFunction.Sum));
        request.Limit.Should().Be(10);
    }

    [Theory]
    [InlineData("=", TableFilterOperator.Equal)]
    [InlineData("!=", TableFilterOperator.NotEqual)]
    [InlineData("<", TableFilterOperator.Less)]
    [InlineData("<=", TableFilterOperator.LessOrEqual)]
    [InlineData(">", TableFilterOperator.Greater)]
    [InlineData(">=", TableFilterOperator.GreaterOrEqual)]
    [InlineData("in", TableFilterOperator.In)]
    [InlineData("between", TableFilterOperator.Between)]
    [InlineData("is_null", TableFilterOperator.IsNull)]
    [InlineData("contains", TableFilterOperator.Contains)]
    [InlineData("starts_with", TableFilterOperator.StartsWith)]
    public void ParseRead_EachOperator_IsRecognized(string op, TableFilterOperator expected)
    {
        TableReadRequest request = TableRequestJson.ParseRead(Json($$$"""{"table": "{{{Id}}}", "where": {"column": "x", "op": "{{{op}}}"}}"""));

        request.Where.Should().BeOfType<TableCondition>().Which.Operator.Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"table": "ID", "sql": "SELECT 1"}""", "*unknown key \"sql\"*")]
    [InlineData("""{"table": "ID", "where": {"column": "x", "op": "=", "value": 1, "raw": "1=1"}}""", "*unknown key \"raw\"*")]
    [InlineData("""{"table": "ID", "where": {"and": [], "or": []}}""", "*unknown key \"or\"*")]
    [InlineData("""{"table": "ID", "where": {"column": "x", "op": "LIKE", "value": "%"}}""", "*Unknown op*")]
    [InlineData("""{"table": "ID", "where": {"column": "x", "op": "=", "value": 1, "values": [1]}}""", "*not both*")]
    [InlineData("""{"table": "ID", "where": {"column": "x"}}""", "*needs an \"op\"*")]
    [InlineData("""{"table": "ID", "where": {"op": "="}}""", "*needs a \"column\"*")]
    [InlineData("""{"table": "ID", "where": "1=1"}""", "*must be an object*")]
    [InlineData("""{"table": "ID", "where": {"column": "x", "op": "=", "value": {"a": 1}}}""", null)]
    [InlineData("""{"table": "ID", "columns": "region"}""", "*must be a list*")]
    [InlineData("""{"table": "ID", "columns": [1]}""", "*list of column headers*")]
    [InlineData("""{"table": "ID", "limit": "10"}""", "*whole number*")]
    [InlineData("""{"table": "ID", "limit": 1.5}""", "*whole number*")]
    [InlineData("""{"table": "ID", "order_by": [{"column": "x", "direction": "sideways"}]}""", "*asc*desc*")]
    [InlineData("""{"table": "sales"}""", "*table's id*")]
    [InlineData("""[]""", "*JSON object*")]
    public void ParseRead_MalformedRequest_IsRejected(string json, string? message)
    {
        Action act = () =>
        {
            TableReadRequest request = TableRequestJson.ParseRead(Json(json.Replace("ID", Id, StringComparison.Ordinal)));
            // A nested object as a value is refused when the value is converted for the compiler.
            if (request.Where is TableCondition { Values.Count: > 0 } condition)
                TableQueryValues.Convert(condition.Values[0], TableColumnType.Text, "x");
        };

        act.Should().Throw<TableQueryException>().WithMessage(message ?? "*string, number, true or false*");
    }

    [Theory]
    [InlineData("""{"table": "ID"}""", "*needs \"aggregates\"*")]
    [InlineData("""{"table": "ID", "aggregates": [{"aggregate": "median", "column": "x"}]}""", "*Unknown aggregate*")]
    [InlineData("""{"table": "ID", "aggregates": [{"aggregate": "count", "filter": "x"}]}""", "*unknown key*")]
    [InlineData("""{"table": "ID", "aggregates": [{"aggregate": "count"}], "offset": 5}""", "*unknown key \"offset\"*")]
    public void ParseAggregate_MalformedRequest_IsRejected(string json, string message)
    {
        Action act = () => TableRequestJson.ParseAggregate(Json(json.Replace("ID", Id, StringComparison.Ordinal)));

        act.Should().Throw<TableQueryException>().WithMessage(message);
    }

    [Fact]
    public void ParseRead_FilterDeeperThanLimit_IsRejected()
    {
        string filter = """{"column": "x", "op": "is_null"}""";
        for (int i = 0; i < TableQueryCompiler.MaxFilterDepth; i++)
            filter = $$$"""{"not": {{{filter}}}}""";

        Action act = () => TableRequestJson.ParseRead(Json($$$"""{"table": "{{{Id}}}", "where": {{{filter}}}}"""));

        act.Should().Throw<TableQueryException>().WithMessage("*nest at most*");
    }
}
