using System.Text.Json;
using Connapse.Core;
using Connapse.Storage.Tables;
using CsCheck;
using FluentAssertions;

namespace Connapse.Storage.Tests.Tables;

[Trait("Category", "Unit")]
public class SavedQueryBinderTests
{
    private const string Id = "6f9619ff-8b86-d011-b42d-00c04fc964ff";
    private static readonly SavedQueryLogin Alice = new("user-1", "alice@example.com");

    private static readonly string RegionRequest = $$$$"""
        {"table": "{{{{Id}}}}", "where": {"and": [{"column": "region", "op": "=", "value": {"param": "region"}},
                                              {"column": "owner", "op": "=", "value": {"param": "me"}}]},
         "limit": 10}
        """;

    private static SavedQueryDefinition Definition(string request, params SavedQueryParameter[] parameters) =>
        new("sales_by_region", "Sales of one region that the caller owns.", SavedQueryKind.Read, request, parameters);

    private static SavedQueryDefinition Regional() => Definition(RegionRequest,
        new SavedQueryParameter("region", SavedQueryParameterType.Text, "The region", Default: "\"west\""),
        new SavedQueryParameter("me", SavedQueryParameterType.Text, FromLogin: SavedQueryLoginField.Email));

    private static JsonElement Json(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static IReadOnlyList<object?> Values(TableFilter? filter, string column) => filter switch
    {
        TableCondition c when c.Field.Column == column => c.Values,
        TableAndFilter and => and.All.Select(f => Values(f, column)).First(v => v.Count > 0),
        _ => [],
    };

    [Fact]
    public void Bind_ArgumentAndLogin_FillsFilterValues()
    {
        BoundSavedQuery bound = SavedQueryBinder.Bind(Regional(), Json("""{"region": "east"}"""), Alice);

        bound.Read.Should().NotBeNull();
        bound.TableId.Should().Be(Guid.Parse(Id));
        ((JsonElement)Values(bound.Read!.Where, "region")[0]!).GetString().Should().Be("east");
        ((JsonElement)Values(bound.Read.Where, "owner")[0]!).GetString().Should().Be("alice@example.com");
        bound.Read.Limit.Should().Be(10);
    }

    [Fact]
    public void Bind_ArgumentLeftOut_UsesDefault()
    {
        BoundSavedQuery bound = SavedQueryBinder.Bind(Regional(), null, Alice);

        ((JsonElement)Values(bound.Read!.Where, "region")[0]!).GetString().Should().Be("west");
    }

    [Fact]
    public void Bind_LoginParameterSent_Refused()
    {
        Action bind = () => SavedQueryBinder.Bind(Regional(), Json("""{"me": "bob@example.com"}"""), Alice);

        bind.Should().Throw<TableQueryException>().WithMessage("*filled from your login*");
    }

    [Fact]
    public void Bind_UnknownArgument_RefusedNamingAccepted()
    {
        Action bind = () => SavedQueryBinder.Bind(Regional(), Json("""{"country": "fr"}"""), Alice);

        bind.Should().Throw<TableQueryException>().WithMessage("*\"country\" isn't a parameter*It takes: region.*");
    }

    [Fact]
    public void Bind_WrongType_Refused()
    {
        Action bind = () => SavedQueryBinder.Bind(Regional(), Json("""{"region": 5}"""), Alice);

        bind.Should().Throw<TableQueryException>().WithMessage("*\"region\" must be a string*");
    }

    [Fact]
    public void Bind_RequiredMissing_Refused()
    {
        SavedQueryDefinition definition = Definition(
            $$$$"""{"table": "{{{{Id}}}}", "where": {"column": "amount", "op": ">", "value": {"param": "min"}}}""",
            new SavedQueryParameter("min", SavedQueryParameterType.WholeNumber));

        Action bind = () => SavedQueryBinder.Bind(definition, Json("{}"), Alice);

        bind.Should().Throw<TableQueryException>().WithMessage("*\"min\" is required*");
    }

    [Fact]
    public void Bind_ArgumentsNotAnObject_Refused()
    {
        Action bind = () => SavedQueryBinder.Bind(Regional(), Json("[1]"), Alice);

        bind.Should().Throw<TableQueryException>().WithMessage("*must be a JSON object*");
    }

    [Fact]
    public void MissingLogin_NoEmail_ListsEmail()
    {
        SavedQueryBinder.MissingLogin(Regional(), new SavedQueryLogin("user-1", null))
            .Should().Equal(SavedQueryLoginField.Email);
        SavedQueryBinder.MissingLogin(Regional(), Alice).Should().BeEmpty();
    }

    [Fact]
    public void Bind_InValuesAndAggregate_FillsEachItem()
    {
        SavedQueryDefinition definition = new("totals", "Totals for two regions.", SavedQueryKind.Aggregate,
            $$$$"""
            {"table": "{{{{Id}}}}", "aggregates": [{"aggregate": "sum", "column": "amount"}], "group_by": ["region"],
             "where": {"column": "region", "op": "in", "values": [{"param": "a"}, {"param": "b"}]},
             "having": {"aggregate": "count", "op": ">=", "value": {"param": "least"}}}
            """,
            [
                new SavedQueryParameter("a", SavedQueryParameterType.Text),
                new SavedQueryParameter("b", SavedQueryParameterType.Text),
                new SavedQueryParameter("least", SavedQueryParameterType.WholeNumber, Default: "1"),
            ]);
        SavedQueryBinder.Validate(definition);

        BoundSavedQuery bound = SavedQueryBinder.Bind(definition, Json("""{"a": "east", "b": "west"}"""), Alice);

        bound.Aggregate.Should().NotBeNull();
        Values(bound.Aggregate!.Where, "region").Select(v => ((JsonElement)v!).GetString()).Should().Equal("east", "west");
        ((JsonElement)((TableCondition)bound.Aggregate.Having!).Values[0]!).GetInt32().Should().Be(1);
    }

    [Fact]
    public void Validate_GoodDefinition_Passes()
    {
        Action validate = () => SavedQueryBinder.Validate(Regional());

        validate.Should().NotThrow();
    }

    [Theory]
    [InlineData("""{"table": "TABLE", "where": {"column": "region", "op": "=", "value": {"param": "nope"}}}""", "*\"nope\", which isn't declared*")]
    [InlineData("""{"table": "TABLE"}""", "*\"region\" isn't used*")]
    [InlineData("""{"table": "TABLE", "limit": {"param": "region"}}""", "*only stand for a filter's*")]
    [InlineData("""{"table": "TABLE", "columns": [{"param": "region"}]}""", "*only stand for a filter's*")]
    [InlineData("""{"table": "TABLE", "where": {"column": "region", "op": "=", "value": {"param": 3}}}""", "*placeholder is*")]
    [InlineData("""{"table": "TABLE", "where": {"column": "region", "op": "=", "value": {"param": "region"}}, "bogus": 1}""", "*bogus*")]
    [InlineData("""[1]""", "*must be a JSON object*")]
    [InlineData("""not json""", "*must be a JSON object*")]
    public void Validate_BadRequest_Refused(string request, string message)
    {
        SavedQueryDefinition definition = Definition(request.Replace("TABLE", Id),
            new SavedQueryParameter("region", SavedQueryParameterType.Text));

        Action validate = () => SavedQueryBinder.Validate(definition);

        validate.Should().Throw<TableQueryException>().WithMessage(message);
    }

    [Theory]
    [InlineData("Sales")]
    [InlineData("1st")]
    [InlineData("")]
    [InlineData("has space")]
    public void Validate_BadName_Refused(string name)
    {
        Action validate = () => SavedQueryBinder.Validate(Regional() with { Name = name });

        validate.Should().Throw<TableQueryException>().WithMessage("*name must start*");
    }

    [Fact]
    public void Validate_EmptyDescription_Refused()
    {
        Action validate = () => SavedQueryBinder.Validate(Regional() with { Description = " " });

        validate.Should().Throw<TableQueryException>().WithMessage("*description*");
    }

    [Fact]
    public void Validate_DuplicateParameter_Refused()
    {
        SavedQueryDefinition definition = Definition(RegionRequest.Replace("\"me\"", "\"region\""),
            new SavedQueryParameter("region", SavedQueryParameterType.Text),
            new SavedQueryParameter("region", SavedQueryParameterType.Text));

        Action validate = () => SavedQueryBinder.Validate(definition);

        validate.Should().Throw<TableQueryException>().WithMessage("*declared twice*");
    }

    [Theory]
    [InlineData(SavedQueryParameterType.WholeNumber, "1.5")]
    [InlineData(SavedQueryParameterType.WholeNumber, "\"1\"")]
    [InlineData(SavedQueryParameterType.Boolean, "1")]
    [InlineData(SavedQueryParameterType.Date, "\"11/01/2026\"")]
    [InlineData(SavedQueryParameterType.Timestamp, "\"yesterday\"")]
    [InlineData(SavedQueryParameterType.Text, "null")]
    [InlineData(SavedQueryParameterType.Text, "{oops")]
    public void Validate_DefaultOfWrongType_Refused(SavedQueryParameterType type, string value)
    {
        SavedQueryDefinition definition = Definition(
            $$$$"""{"table": "{{{{Id}}}}", "where": {"column": "x", "op": "=", "value": {"param": "p"}}}""",
            new SavedQueryParameter("p", type, Default: value));

        Action validate = () => SavedQueryBinder.Validate(definition);

        validate.Should().Throw<TableQueryException>().WithMessage("*default of \"p\"*");
    }

    [Fact]
    public void Validate_LoginParameterNotTextOrWithDefault_Refused()
    {
        string request = $$$$"""{"table": "{{{{Id}}}}", "where": {"column": "x", "op": "=", "value": {"param": "me"}}}""";

        Action notText = () => SavedQueryBinder.Validate(Definition(request,
            new SavedQueryParameter("me", SavedQueryParameterType.WholeNumber, FromLogin: SavedQueryLoginField.UserId)));
        Action withDefault = () => SavedQueryBinder.Validate(Definition(request,
            new SavedQueryParameter("me", SavedQueryParameterType.Text, Default: "\"x\"", FromLogin: SavedQueryLoginField.UserId)));

        notText.Should().Throw<TableQueryException>().WithMessage("*must be text*");
        withDefault.Should().Throw<TableQueryException>().WithMessage("*can't have a default*");
    }

    [Theory]
    [InlineData(SavedQueryParameterType.Text, "\"x\"", true)]
    [InlineData(SavedQueryParameterType.WholeNumber, "42", true)]
    [InlineData(SavedQueryParameterType.WholeNumber, "4.2", false)]
    [InlineData(SavedQueryParameterType.Number, "4.2", true)]
    [InlineData(SavedQueryParameterType.Number, "\"4.2\"", false)]
    [InlineData(SavedQueryParameterType.Boolean, "false", true)]
    [InlineData(SavedQueryParameterType.Date, "\"2026-11-01\"", true)]
    [InlineData(SavedQueryParameterType.Date, "\"2026-11-01T00:00:00Z\"", false)]
    [InlineData(SavedQueryParameterType.Timestamp, "\"2026-11-01T09:00:00+02:00\"", true)]
    [InlineData(SavedQueryParameterType.Text, "null", false)]
    public void Fits_ChecksType(SavedQueryParameterType type, string value, bool fits)
    {
        SavedQueryBinder.Fits(Json(value), type).Should().Be(fits);
    }

    [Fact]
    [Trait("Size", "Medium")]
    public void Bind_RandomArguments_FillsOnlyFilterValuesOrRefuses()
    {
        // Whatever an agent sends, binding either refuses with a TableQueryException or yields the admin's request
        // with only the region filter changed: the login value stays the caller's and the table and limit are fixed.
        Gen<string> keys = Gen.OneOfConst("region", "me", "other", "table", "limit", "");
        Gen<string> values = Gen.OneOf(
            Gen.String.Select(s => JsonSerializer.Serialize(s)),
            Gen.Int.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Gen.OneOfConst("null", "true", "[]", "{}", """{"param": "me"}""", """{"column": "x"}""", "1e400"));
        Gen.Dictionary(keys, values)[0, 4]
            .Select(d => "{" + string.Join(",", d.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}:{kv.Value}")) + "}")
            .Sample(arguments =>
            {
                BoundSavedQuery bound;
                try
                {
                    bound = SavedQueryBinder.Bind(Regional(), Json(arguments), Alice);
                }
                catch (TableQueryException)
                {
                    return;
                }
                bound.TableId.Should().Be(Guid.Parse(Id));
                bound.Read!.Limit.Should().Be(10);
                bound.Read.Columns.Should().BeNull();
                ((JsonElement)Values(bound.Read.Where, "owner")[0]!).GetString().Should().Be("alice@example.com");
                ((JsonElement)Values(bound.Read.Where, "region")[0]!).ValueKind.Should().Be(JsonValueKind.String);
            }, iter: 1000);
    }
}
