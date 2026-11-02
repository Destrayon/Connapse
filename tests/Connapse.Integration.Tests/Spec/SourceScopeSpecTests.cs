using FluentAssertions;

namespace Connapse.Integration.Tests.Spec;

/// <summary>
/// CON-77: the committed spec describes a source's typed scope exactly. A scope must be one provider's, with that
/// provider's required members, and a scope can't claim one provider's type with another's members. The spec now
/// gives each provider's scope its own subtype for client generators, and this proves the rewrite kept the
/// validation the old anyOf had.
/// </summary>
[Trait("Category", "Unit")]
public class SourceScopeSpecTests
{
    private static readonly CommittedSpec Spec = CommittedSpec.Load(Path.Combine(RepoRoot(), "docs", "api", "openapi.json"));

    private const string CreateSource = "POST /api/sources";

    private static string Create(string scope) =>
        $$"""{"name":"docs","connectionId":"7b1c9e2a-0000-4000-8000-000000000001","scope":{{scope}}}""";

    [Theory]
    [InlineData("""{"type":"s3","bucketName":"b"}""")]
    [InlineData("""{"type":"s3","bucketName":"b","prefix":"reports/"}""")]
    [InlineData("""{"type":"github","owner":"octo","repo":"docs","kind":"Docs"}""")]
    [InlineData("""{"type":"jira","projectId":"10001"}""")]
    [InlineData("null")]
    public void CreateSource_AProvidersCompleteScope_IsAccepted(string scope)
    {
        Spec.AcceptsRequest(CreateSource, Create(scope), out var errors).Should().BeTrue(errors);
    }

    [Theory]
    [InlineData("""{"type":"s3"}""", "an S3 scope names its bucket")]
    [InlineData("""{"type":"github","owner":"octo"}""", "a GitHub scope names its repository and kind")]
    [InlineData("""{"type":"github","bucketName":"b"}""", "GitHub's type with S3's members is neither scope")]
    [InlineData("""{"type":"dropbox","path":"/"}""", "there is no such provider")]
    [InlineData("""{"bucketName":"b"}""", "a scope says which provider it is")]
    public void CreateSource_AnIncompleteOrMixedScope_IsRejected(string scope, string because)
    {
        Spec.AcceptsRequest(CreateSource, Create(scope), out _).Should().BeFalse(because);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Connapse.slnx not found above the test output.");
    }
}
