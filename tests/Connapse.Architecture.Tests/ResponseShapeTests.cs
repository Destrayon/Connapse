namespace Connapse.Architecture.Tests;

/// <summary>
/// Proof test for epic #783, problem P3: anonymous objects as response bodies reach OpenAPI untyped.
/// Entries are "file: route or member: count", so moving lines doesn't churn the list but a new anonymous body
/// in a listed handler does. Every web file is scanned, Program.cs included.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class ResponseShapeTests
{
    [Fact]
    public void Endpoints_NoAnonymousResponses_ExceptAllowList()
    {
        var files = RepoPaths.WebCSharpFiles().ToList();
        var routes = SourceScan.QualifiedRoutes(files.Select(f => f.Source));
        ExactMatchList.AssertMatches(
            "anonymous-responses.json",
            files.SelectMany(f => Rules.AnonymousResponses(f.Path, f.Source, routes)),
            "Return a named record through TypedResults instead of an anonymous object.");
    }
}
