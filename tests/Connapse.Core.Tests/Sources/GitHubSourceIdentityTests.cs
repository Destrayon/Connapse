using Connapse.Web.Endpoints;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Sources;

/// <summary>
/// A GitHub source's private flag and repository id are fixed at creation: changing either in place
/// would leave already-indexed documents with the old addressing until a re-sync.
/// </summary>
[Trait("Category", "Unit")]
public class GitHubSourceIdentityTests
{
    private const string Public = """{"owner":"acme","repo":"infra","repoId":99,"kind":"Docs"}""";
    private const string Private = """{"owner":"acme","repo":"infra","repoId":99,"private":true,"kind":"Docs"}""";

    [Theory]
    [InlineData(Public, Private, true)]
    [InlineData(Private, Public, true)]
    [InlineData(Private, """{"owner":"acme","repo":"infra","repoId":7,"private":true}""", true)]
    [InlineData(Private, """{"owner":"acme","repo":"infra","repoId":99,"private":true,"includePatterns":["*.md"]}""", false)]
    [InlineData("""{"bucketName":"b"}""", """{"bucketName":"c","private":true}""", false)]
    public void GitHubIdentityChanged_OnlyThePrivateFlagOrRepositoryIdCount(string before, string after, bool changed) =>
        SourcesEndpoints.GitHubIdentityChanged(before, after).Should().Be(changed);
}
