using System.Text.Json;
using Connapse.Web.Tests.Components;
using FluentAssertions;

namespace Connapse.Web.Tests.OpenApi;

/// <summary>
/// Every API error is a problem with an <c>error</c> member, and some add <c>message</c>, <c>field</c> or an
/// <c>errors</c> list. The spec's ProblemDetails has to say so, or a client generated from it can't read them.
/// </summary>
[Trait("Category", "Unit")]
public class ProblemDetailsSchemaTests
{
    private static JsonElement ProblemDetails()
    {
        string path = Path.Combine(PageTestPaths.RepositoryRoot(), "docs", "api", "openapi.json");
        using JsonDocument spec = JsonDocument.Parse(File.ReadAllText(path));
        return spec.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("ProblemDetails").Clone();
    }

    [Fact]
    public void ProblemDetails_DeclaresTheMembersApiErrorsCarry()
    {
        JsonElement properties = ProblemDetails().GetProperty("properties");

        foreach (string name in new[] { "error", "message", "field", "troubleshootingUrl", "traceId" })
            properties.TryGetProperty(name, out JsonElement member).Should().BeTrue($"API errors carry \"{name}\"");
        // A handler's list of reasons, or request validation's messages per field.
        properties.GetProperty("errors").GetProperty("anyOf").EnumerateArray()
            .Select(shape => shape.GetProperty("type").ToString()).Should().BeEquivalentTo(["array", "object"]);
    }

    [Fact]
    public void ProblemDetails_RequiresError()
    {
        ProblemDetails().TryGetProperty("required", out JsonElement required).Should().BeTrue();
        required.EnumerateArray().Select(r => r.GetString()).Should().Contain("error");
    }
}
