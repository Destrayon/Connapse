using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.CloudScope;

[Trait("Category", "Unit")]
public class CloudScopeResultTests
{
    [Fact]
    public void FullAccess_IsPathAllowed_AlwaysTrue()
    {
        var result = CloudScopeResult.FullAccess();
        result.IsPathAllowed("/anything/here").Should().BeTrue();
        result.IsPathAllowed("/").Should().BeTrue();
    }

    [Fact]
    public void Allow_SpecificPrefix_MatchesSubPaths()
    {
        var result = CloudScopeResult.Allow(["/docs/"]);
        result.IsPathAllowed("/docs/file.md").Should().BeTrue();
        result.IsPathAllowed("/docs/sub/file.md").Should().BeTrue();
        result.IsPathAllowed("/other/file.md").Should().BeFalse();
    }

    [Fact]
    public void Deny_IsPathAllowed_AlwaysFalse()
    {
        var result = CloudScopeResult.Deny("no access");
        result.IsPathAllowed("/anything").Should().BeFalse();
    }

    [Fact]
    public void Allow_MultiplePrefixes_MatchesAny()
    {
        var result = CloudScopeResult.Allow(["/docs/", "/reports/"]);
        result.IsPathAllowed("/docs/file.md").Should().BeTrue();
        result.IsPathAllowed("/reports/q4.pdf").Should().BeTrue();
        result.IsPathAllowed("/private/secret.md").Should().BeFalse();
    }

    [Fact]
    public void Deny_HasCorrectError()
    {
        var result = CloudScopeResult.Deny("test error");
        result.HasAccess.Should().BeFalse();
        result.Error.Should().Be("test error");
        result.AllowedPrefixes.Should().BeEmpty();
    }
}
