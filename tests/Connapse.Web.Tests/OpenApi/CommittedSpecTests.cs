using Connapse.Web.Tests.Components;
using FluentAssertions;

namespace Connapse.Web.Tests.OpenApi;

/// <summary>
/// Proof test for epic #783, problem P5 (#857): the REST API's shape is a committed file, so a change to
/// it shows up in review instead of going unnoticed. The build writes the generated document; this
/// compares it with the committed one.
/// </summary>
[Trait("Category", "Unit")]
public class CommittedSpecTests
{
    private const string UpdateCommand = "dotnet build src/Connapse.Web -p:UpdateOpenApiSpec=true";

    [Fact]
    public void CommittedSpec_MatchesGenerated()
    {
        string root = PageTestPaths.RepositoryRoot();
        string generated = Path.Combine(root, "src", "Connapse.Web", "obj", "openapi", "Connapse.Web.json");
        string committed = Path.Combine(root, "docs", "api", "openapi.json");

        File.Exists(generated).Should().BeTrue($"building Connapse.Web writes the OpenAPI document to {generated}");
        File.Exists(committed).Should().BeTrue($"the API's shape is committed at docs/api/openapi.json; create it with `{UpdateCommand}`");

        Normalized(committed).Should().Be(Normalized(generated),
            $"docs/api/openapi.json must match what the code generates. If the API change is intended, run `{UpdateCommand}` and commit the file");
    }

    private static string Normalized(string path) => File.ReadAllText(path).ReplaceLineEndings("\n").TrimEnd();
}
