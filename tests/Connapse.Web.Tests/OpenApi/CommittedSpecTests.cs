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

        AssertSpecMatches(File.ReadAllText(committed), File.ReadAllText(generated));
    }

    [Fact]
    public void AssertSpecMatches_ChangedSpec_FailsWithTheDifferingLines()
    {
        string committed = File.ReadAllText(Path.Combine(PageTestPaths.RepositoryRoot(), "docs", "api", "openapi.json"));
        string[] lines = committed.ReplaceLineEndings("\n").Split('\n');
        int changed = Array.FindIndex(lines, line => line.Contains("\"type\": \"string\""));
        string original = lines[changed];
        lines[changed] = original.Replace("\"string\"", "\"integer\"");
        string generated = string.Join("\n", lines);
        string lineNumber = (changed + 1).ToString().PadLeft(6);

        Action act = () => AssertSpecMatches(committed, generated);

        string message = act.Should().Throw<Xunit.Sdk.XunitException>().Which.Message;
        message.Should().Contain(UpdateCommand);
        message.Should().Contain(
            $"in 1 place (- committed, + generated):\n" +
            $"@@ committed line {changed + 1}, generated line {changed + 1} @@\n" +
            $"- {lineNumber} | {original}\n" +
            $"+ {lineNumber} | {lines[changed]}");
    }

    [Fact]
    public void Describe_EditsFarApart_ListsEachEditSeparately()
    {
        string committed = File.ReadAllText(Path.Combine(PageTestPaths.RepositoryRoot(), "docs", "api", "openapi.json"));
        string[] lines = committed.ReplaceLineEndings("\n").Split('\n');
        int first = Array.FindIndex(lines, line => line.Contains("\"type\": \"string\""));
        int second = Array.FindIndex(lines, first + 500, line => line.Contains("\"type\": \"string\""));
        string firstOriginal = lines[first];
        string secondOriginal = lines[second];
        lines[first] = firstOriginal.Replace("\"string\"", "\"integer\"");
        lines[second] = secondOriginal.Replace("\"string\"", "\"boolean\"");
        lines = [.. lines[..(second + 1)], "        \"added\": true,", .. lines[(second + 1)..]];

        string? difference = SpecDifference.Describe(committed, string.Join("\n", lines));

        difference.Should().NotBeNull();
        difference!.Should().Contain("in 2 places");
        difference.Should().Contain($"- {(first + 1).ToString().PadLeft(6)} | {firstOriginal}");
        difference.Should().Contain($"+ {(first + 1).ToString().PadLeft(6)} | {lines[first]}");
        difference.Should().Contain($"- {(second + 1).ToString().PadLeft(6)} | {secondOriginal}");
        difference.Should().Contain($"+ {(second + 1).ToString().PadLeft(6)} | {lines[second]}");
        difference.Should().Contain($"+ {(second + 2).ToString().PadLeft(6)} |         \"added\": true,");
        difference.Split('\n').Should().HaveCount(8, "only the changed lines are listed, never the unchanged ones between them");
    }

    [Fact]
    public void Describe_SameSpecWithDifferentLineEndings_ReturnsNull()
    {
        string spec = "{\n  \"openapi\": \"3.1.1\"\n}\n";

        SpecDifference.Describe(spec, spec.ReplaceLineEndings("\r\n")).Should().BeNull();
    }

    [Fact]
    public void Describe_InsertedLine_ListsOnlyTheInsertion()
    {
        string committed = "{\n  \"a\": 1,\n  \"c\": 3\n}";
        string generated = "{\n  \"a\": 1,\n  \"b\": 2,\n  \"c\": 3\n}";

        string? difference = SpecDifference.Describe(committed, generated);

        difference.Should().Be(
            "The committed spec differs from the generated one in 1 place (- committed, + generated):\n" +
            "@@ committed line 3, generated line 3 @@\n" +
            "+      3 |   \"b\": 2,");
    }

    [Fact]
    public void Describe_MoreThanTheEditLimit_SaysWhereTheDifferenceStarts()
    {
        string committed = "{\n" + string.Join("\n", Enumerable.Range(0, 3000).Select(i => $"  \"a{i}\": 1,"));
        string generated = "{\n" + string.Join("\n", Enumerable.Range(0, 3000).Select(i => $"  \"b{i}\": 1,"));

        string? difference = SpecDifference.Describe(committed, generated);

        difference.Should().Be(
            "The committed spec differs from the generated one in more than 2000 lines, starting at line 2 " +
            "(committed: 3001 lines, generated: 3001 lines).");
    }

    // Fails through Assert.Fail with plain text: FluentAssertions' string diff reads the JSON's braces as
    // format placeholders and throws a FormatException instead of showing the difference (CON-126).
    private static void AssertSpecMatches(string committed, string generated)
    {
        string? difference = SpecDifference.Describe(committed, generated);
        if (difference is not null)
            Assert.Fail($"docs/api/openapi.json must match what the code generates. If the API change is intended, run `{UpdateCommand}` and commit the file.\n{difference}");
    }
}
