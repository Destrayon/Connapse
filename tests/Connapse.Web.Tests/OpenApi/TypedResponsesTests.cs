using System.Text.Json;
using Connapse.Web.Tests.Components;
using FluentAssertions;

namespace Connapse.Web.Tests.OpenApi;

/// <summary>
/// Proof test for epic #783, problem P3 (CON-58 to CON-64): every operation in the committed spec says what it
/// answers, so a client generated from it gets a type for each body. A new operation that returns a body without
/// declaring it, or declares it as an untyped object, fails here, whether or not any test ever calls it.
/// </summary>
[Trait("Category", "Unit")]
public class TypedResponsesTests
{
    /// <summary>The operations allowed an untyped success, and why. Exact: a fixed one must leave the list.</summary>
    private static readonly Dictionary<string, string> Untyped = new()
    {
        ["GET /health"] = "An empty 200 by design: the health check has no body.",
        ["POST /api/v1/identity/register"] = MapIdentityApi,
        ["GET /api/v1/identity/confirmEmail"] = MapIdentityApi,
        ["POST /api/v1/identity/resendConfirmationEmail"] = MapIdentityApi,
        ["POST /api/v1/identity/forgotPassword"] = MapIdentityApi,
        ["POST /api/v1/identity/resetPassword"] = MapIdentityApi,
    };

    private const string MapIdentityApi = "ASP.NET Identity's MapIdentityApi endpoint; whether Connapse keeps that API is the user's decision.";

    [Fact]
    public void EveryOperation_DeclaresTypedResponses_ExceptAllowList()
    {
        string path = Path.Combine(PageTestPaths.RepositoryRoot(), "docs", "api", "openapi.json");
        using JsonDocument spec = JsonDocument.Parse(File.ReadAllText(path));

        var untyped = new SortedSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty route in spec.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (JsonProperty operation in route.Value.EnumerateObject())
            {
                if (!IsTyped(operation.Value.GetProperty("responses")))
                    untyped.Add($"{operation.Name.ToUpperInvariant()} {route.Name}");
            }
        }

        untyped.Should().BeEquivalentTo(Untyped.Keys,
            "every operation declares its success, a 200 or 201 with a typed body or a body-less 204 or 3xx, and every "
            + "body it declares has a schema; declare it with Produces<T>, or add it to the list with the reason it can't");
    }

    /// <summary>
    /// A success status, every 200 and 201 with content, and every declared body with a real schema: a $ref, a type,
    /// or a composition, never the empty schema an untyped object gets.
    /// </summary>
    private static bool IsTyped(JsonElement responses)
    {
        bool success = false;
        foreach (JsonProperty response in responses.EnumerateObject())
        {
            bool hasContent = response.Value.TryGetProperty("content", out JsonElement content);
            if (response.Name is "200" or "201" && !hasContent)
                return false;
            if (response.Name[0] is '2' or '3')
                success = true;
            if (!hasContent)
                continue;
            foreach (JsonProperty media in content.EnumerateObject())
            {
                if (!media.Value.TryGetProperty("schema", out JsonElement schema) || !HasShape(schema))
                    return false;
            }
        }
        return success;
    }

    private static bool HasShape(JsonElement schema) =>
        new[] { "$ref", "type", "anyOf", "oneOf", "allOf" }.Any(keyword => schema.TryGetProperty(keyword, out _));
}
