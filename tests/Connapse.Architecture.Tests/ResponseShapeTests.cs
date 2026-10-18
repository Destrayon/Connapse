namespace Connapse.Architecture.Tests;

/// <summary>
/// Proof test for epic #783, problem P3: anonymous objects as response bodies reach OpenAPI untyped.
/// Entries are "file: count" so line moves don't churn the list; a typed rewrite lowers the count.
/// </summary>
[Trait("Category", "Unit")]
public class ResponseShapeTests
{
    [Fact]
    public void Endpoints_NoAnonymousResponses_ExceptAllowList()
    {
        string endpoints = Path.Combine(RepoPaths.WebProject, "Endpoints");
        var counts = Directory.EnumerateFiles(endpoints, "*.cs", SearchOption.AllDirectories)
            .Select(f => (Path: RepoPaths.RelativeToWeb(f), Count: SourceScan.CountAnonymousResponses(File.ReadAllText(f))))
            .Where(x => x.Count > 0)
            .Select(x => $"{x.Path}: {x.Count}");

        ExactMatchList.AssertMatches(
            "anonymous-responses.json",
            counts,
            "Return a named record through TypedResults instead of an anonymous object.");
    }
}
