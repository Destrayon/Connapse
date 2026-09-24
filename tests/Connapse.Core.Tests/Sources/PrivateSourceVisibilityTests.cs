using Connapse.Web.Services;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Sources;

/// <summary>A private GitHub source is listed only to those the permission check lets read its repository.</summary>
[Trait("Category", "Unit")]
public class PrivateSourceVisibilityTests
{
    private static Source With(string scope) => new(Guid.NewGuid(), "acme/infra issues", null, Guid.NewGuid(), scope, DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public void PublicAndNonGitHubSources_AreVisibleToEveryone()
    {
        PrivateSourceVisibility.IsVisible(With("""{"owner":"acme","repo":"docs","repoId":5}"""), new HashSet<string>()).Should().BeTrue();
        PrivateSourceVisibility.IsVisible(With("""{"bucketName":"b"}"""), new HashSet<string>()).Should().BeTrue();
    }

    [Fact]
    public void PrivateSource_IsVisibleOnlyWhenItsRepositoryIsGranted()
    {
        var source = With("""{"owner":"acme","repo":"infra","repoId":99,"private":true}""");

        PrivateSourceVisibility.IsVisible(source, new HashSet<string>()).Should().BeFalse();
        PrivateSourceVisibility.IsVisible(source, new HashSet<string> { "github://999/" }).Should().BeFalse();
        PrivateSourceVisibility.IsVisible(source, new HashSet<string> { "github://99/" }).Should().BeTrue();
    }

    [Fact]
    public void PrivateSourceWithoutARepositoryId_IsHidden() =>
        PrivateSourceVisibility.IsVisible(With("""{"owner":"acme","repo":"infra","private":true}"""), new HashSet<string> { "github://99/" })
            .Should().BeFalse();
}
