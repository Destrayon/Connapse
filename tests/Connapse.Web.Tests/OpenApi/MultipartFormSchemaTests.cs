using System.Text.Json.Nodes;
using Connapse.Web.Tests.Components;
using FluentAssertions;

namespace Connapse.Web.Tests.OpenApi;

/// <summary>
/// CON-81: every multipart/form-data request body in the committed spec is one object with a property per form part,
/// not an allOf of one object per [FromForm] parameter. Kiota sends the flat form as its MultipartBody; the allOf
/// became a model its multipart writer refused, so a generated client couldn't upload.
/// </summary>
[Trait("Category", "Unit")]
public class MultipartFormSchemaTests
{
    private static IEnumerable<(string Operation, JsonObject Schema)> MultipartBodies()
    {
        var spec = JsonNode.Parse(File.ReadAllText(Path.Combine(PageTestPaths.RepositoryRoot(), "docs", "api", "openapi.json")))!;
        foreach (var (path, item) in spec["paths"]!.AsObject())
            foreach (var (method, operation) in item!.AsObject())
                if (operation?["requestBody"]?["content"]?["multipart/form-data"]?["schema"] is JsonObject schema)
                    yield return ($"{method.ToUpperInvariant()} {path}", schema);
    }

    [Fact]
    public void MultipartBodies_AreOneObjectWithAPropertyPerPart()
    {
        var bodies = MultipartBodies().ToList();
        bodies.Select(b => b.Operation).Should().Contain("POST /api/containers/{containerId}/files");

        foreach (var (operation, schema) in bodies)
        {
            schema.ContainsKey("allOf").Should().BeFalse($"{operation}'s form must be one object for a generated client to send it");
            schema["type"]!.GetValue<string>().Should().Be("object", operation);
            schema["properties"]!.AsObject().Count.Should().BePositive(operation);
        }
    }

    [Fact]
    public void Upload_DeclaresItsFilesPathAndStrategyParts()
    {
        var (_, upload) = MultipartBodies().Single(b => b.Operation == "POST /api/containers/{containerId}/files");

        upload["properties"]!.AsObject().Select(p => p.Key).Should().BeEquivalentTo(["files", "path", "strategy"]);
        upload["required"]!.AsArray().Select(r => r!.GetValue<string>()).Should().Equal("files");
    }
}
