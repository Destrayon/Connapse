using System.Text.Json.Nodes;
using Connapse.Storage.Connectors.GitHub;
using Connapse.Web.Services;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Web;

/// <summary>An API-created GitHub source takes its repository id and privacy from GitHub, not from the caller.</summary>
[Trait("Category", "Unit")]
public class GitHubSourceScopeTests
{
    [Fact]
    public void Apply_PrivateRepository_MarksPrivateAndRecordsTheId()
    {
        var scope = new JsonObject { ["owner"] = "acme", ["repo"] = "infra", ["repoId"] = 1, ["private"] = false };

        GitHubSourceScope.Apply(scope, new GitHubRepositoryInfo(99, "acme/infra", "private"));

        scope["repoId"]!.GetValue<long>().Should().Be(99, "a caller-supplied id could point the source's addresses at another repository");
        scope["private"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Apply_PublicRepository_DropsAClaimedPrivateFlag()
    {
        var scope = new JsonObject { ["owner"] = "acme", ["repo"] = "docs", ["private"] = true };

        GitHubSourceScope.Apply(scope, new GitHubRepositoryInfo(5, "acme/docs", "public"));

        scope.ContainsKey("private").Should().BeFalse();
        scope["repoId"]!.GetValue<long>().Should().Be(5);
    }
}
