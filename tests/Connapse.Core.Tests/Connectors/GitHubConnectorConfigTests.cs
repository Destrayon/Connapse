using Connapse.Storage.Connectors;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public class GitHubConnectorConfigTests
{
    [Theory]
    [InlineData("octocat", true)]
    [InlineData("octo-org", true)]
    [InlineData("a", true)]
    [InlineData("A1-b2-C3", true)]
    [InlineData("-leading", false)]
    [InlineData("trailing-", false)]
    [InlineData("double--hyphen", false)]
    [InlineData("under_score", false)]
    [InlineData("dot.ted", false)]
    [InlineData("has/slash", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidOwner_FollowsGitHubAccountNameRules(string? owner, bool expected)
    {
        GitHubConnectorConfig.IsValidOwner(owner).Should().Be(expected);
    }

    [Fact]
    public void IsValidOwner_LongerThan39Characters_IsRejected()
    {
        GitHubConnectorConfig.IsValidOwner(new string('a', 39)).Should().BeTrue();
        GitHubConnectorConfig.IsValidOwner(new string('a', 40)).Should().BeFalse();
    }

    [Theory]
    [InlineData("Hello-World", true)]
    [InlineData("docs.site", true)]
    [InlineData("under_score", true)]
    [InlineData(".github", true)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("a?b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidRepo_FollowsGitHubRepositoryNameRules(string? repo, bool expected)
    {
        GitHubConnectorConfig.IsValidRepo(repo).Should().Be(expected);
    }

    [Fact]
    public void EffectiveRemoteUrl_WithoutOverride_IsThePublicCloneUrl()
    {
        var config = new GitHubConnectorConfig { Owner = "octocat", Repo = "Hello-World" };

        config.EffectiveRemoteUrl.Should().Be("https://github.com/octocat/Hello-World.git");
        config.WebUrl.Should().Be("https://github.com/octocat/Hello-World");
    }
}
