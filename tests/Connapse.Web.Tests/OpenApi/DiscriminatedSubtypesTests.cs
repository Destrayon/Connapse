using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Connapse.Web.Tests.Components;
using FluentAssertions;

namespace Connapse.Web.Tests.OpenApi;

/// <summary>
/// CON-77: every polymorphic type the API takes or returns is, in the committed spec, a base with a discriminator
/// and one subtype per derived type, each <c>allOf</c> the base and its own members. As an <c>anyOf</c>, a client
/// generator such as Kiota merged them into one class that knew only the first discriminator value, so a generated
/// client couldn't describe a GitHub or Jira source scope. A value is checked against one subtype only, in SourceScopeSpecTests. The types come from the code, so a new provider's scope
/// is checked without listing it here.
/// </summary>
[Trait("Category", "Unit")]
public class DiscriminatedSubtypesTests
{
    public static TheoryData<string> PolymorphicTypes()
    {
        var data = new TheoryData<string>();
        foreach (var type in typeof(Connapse.Core.SourceScope).Assembly.GetTypes()
                     .Where(t => t.GetCustomAttribute<JsonPolymorphicAttribute>() is not null && t.GetCustomAttributes<JsonDerivedTypeAttribute>().Any()))
            data.Add(type.FullName!);
        return data;
    }

    private static JsonObject Schemas() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(PageTestPaths.RepositoryRoot(), "docs", "api", "openapi.json")))!["components"]!["schemas"]!.AsObject();

    [Fact]
    public void PolymorphicTypes_IncludeSourceScope() =>
        PolymorphicTypes().Cast<object[]>().Select(row => (string)row[0]).Should().Contain(typeof(Connapse.Core.SourceScope).FullName);

    [Theory]
    [MemberData(nameof(PolymorphicTypes))]
    public void PolymorphicType_IsADiscriminatedBaseWithOneSubtypePerDerivedType(string typeName)
    {
        var type = typeof(Connapse.Core.SourceScope).Assembly.GetType(typeName)!;
        string property = type.GetCustomAttribute<JsonPolymorphicAttribute>()!.TypeDiscriminatorPropertyName ?? "$type";
        var values = type.GetCustomAttributes<JsonDerivedTypeAttribute>().Select(d => d.TypeDiscriminator!.ToString()!).ToList();
        var schemas = Schemas();
        var schema = schemas[type.Name]!.AsObject();

        schema.ContainsKey("anyOf").Should().BeFalse("an anyOf is what client generators merge into one class");
        schema.ContainsKey("oneOf").Should().BeFalse();
        schema["discriminator"]!["propertyName"]!.GetValue<string>().Should().Be(property);
        schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).Should().Contain(property);
        schema["properties"]![property]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()).Should().BeEquivalentTo(values);

        // Nothing but the subtypes refers to the base: everywhere else it is a oneOf of the subtypes, which a
        // validator checks against the one subtype a value names.
        string document = File.ReadAllText(Path.Combine(PageTestPaths.RepositoryRoot(), "docs", "api", "openapi.json"));
        CountOf(document, $"\"#/components/schemas/{type.Name}\"").Should().Be(values.Count, "only the subtypes' allOf refers to the base");

        var mapping = schema["discriminator"]!["mapping"]!.AsObject();
        mapping.Select(m => m.Key).Should().BeEquivalentTo(values, "every derived type is a subtype, and nothing else is");
        foreach (var (value, target) in mapping)
        {
            string subtype = target!.GetValue<string>().Split('/')[^1];
            var allOf = schemas[subtype]!["allOf"]!.AsArray();
            allOf[0]!["$ref"]!.GetValue<string>().Should().Be($"#/components/schemas/{type.Name}", $"{value}'s subtype inherits the base");
            // Its own value only, so a subtype can't carry another subtype's discriminator.
            allOf[1]!["properties"]![property]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()).Should().Equal([value]);
        }
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
