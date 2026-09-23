using System.Text.Json;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using Connapse.Storage.Connectors;
using Connapse.Web.Components.Settings;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.Sources;

/// <summary>
/// The public GitHub repository fields of the New source dialog. As with <see cref="SourceFormTests"/>, the
/// scope it writes must be what <c>ConnectorFactory</c> reads, so the requests are fed back through
/// the real factory rather than compared against hand-written JSON.
/// </summary>
[Trait("Category", "Unit")]
public class GitHubRepositoryFormTests
{
    [Theory]
    [InlineData("octocat/Hello-World", "octocat", "Hello-World")]
    [InlineData("https://github.com/octocat/Hello-World", "octocat", "Hello-World")]
    [InlineData("https://github.com/octocat/Hello-World.git", "octocat", "Hello-World")]
    [InlineData("https://www.github.com/octocat/Hello-World/tree/main/docs", "octocat", "Hello-World")]
    [InlineData("github.com/octocat/hello.world/", "octocat", "hello.world")]
    [InlineData("  octocat/repo_1  ", "octocat", "repo_1")]
    public void TryParse_AcceptsOwnerRepoAndGitHubAddresses(string input, string owner, string repo)
    {
        GitHubRepositoryForm.TryParse(input, out string o, out string r).Should().BeTrue();
        o.Should().Be(owner);
        r.Should().Be(repo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("octocat")]
    [InlineData("https://gitlab.com/octocat/repo")]
    [InlineData("https://github.com.evil.test/octocat/repo")]
    [InlineData("ftp://github.com/octocat/repo")]
    [InlineData("octocat/..")]
    [InlineData("-bad/repo")]
    [InlineData("octo cat/repo")]
    public void TryParse_RefusesAnythingElse(string input)
    {
        GitHubRepositoryForm.TryParse(input, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Validate_NothingSelected_SaysSo()
    {
        new GitHubRepositoryForm { Repository = "o/r", IncludeDocs = false, IncludeIssues = false }
            .Validate().Should().Contain("Choose");
    }

    [Fact]
    public void ToRequests_Defaults_CreateBothConnectionLessSources()
    {
        var requests = new GitHubRepositoryForm { Repository = "https://github.com/octocat/Hello-World" }.ToRequests();

        requests.Select(r => r.Name).Should().Equal("octocat/Hello-World docs", "octocat/Hello-World issues");
        requests.Should().OnlyContain(r => r.ConnectionId == null && r.Provider == ConnectionProvider.GitHub);
        requests[0].SyncIntervalSeconds.Should().BeNull("docs are fetched over git, outside the API budget");
        requests[1].SyncIntervalSeconds.Should().Be(GitHubRepositoryForm.IssuesSyncIntervalSeconds);
    }

    [Fact]
    public void ToRequests_ScopesBuildTheIntendedConnectors()
    {
        var requests = new GitHubRepositoryForm
        {
            Repository = "octocat/Hello-World",
            DocPatterns = "*.md\n*.rst",
            IncludeComments = false,
        }.ToRequests();

        var docs = Build(requests[0]).Config;
        docs.Kind.Should().Be(GitHubContentKind.Docs);
        docs.Owner.Should().Be("octocat");
        docs.Repo.Should().Be("Hello-World");
        docs.IncludePatterns.Should().Equal("*.md", "*.rst");

        var issues = Build(requests[1]).Config;
        issues.Kind.Should().Be(GitHubContentKind.IssuesAndPullRequests);
        issues.IncludeComments.Should().BeFalse();
    }

    [Fact]
    public void ToRequests_LongestLegalNames_StayWithinTheStoreLimit()
    {
        string owner = new('o', 39);
        string repo = new('r', 100);

        var requests = new GitHubRepositoryForm { Repository = $"{owner}/{repo}" }.ToRequests();

        requests.Should().OnlyContain(r => r.Name.Length <= 128);
        requests.Select(r => r.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void GitHubScope_IsSummarisedAsRepositoryAndKind()
    {
        var requests = new GitHubRepositoryForm { Repository = "octocat/Hello-World" }.ToRequests();

        SourceScopeSummary.Describe(requests[0].ScopeJson).Should().Be("octocat/Hello-World · docs");
        SourceScopeSummary.Describe(requests[1].ScopeJson).Should().Be("octocat/Hello-World · issues and pull requests");
    }

    private static GitHubConnector Build(CreateSourceRequest request)
    {
        var source = new Source(
            Guid.NewGuid(), request.Name, request.Description, request.ConnectionId, request.ScopeJson,
            DateTime.UtcNow, DateTime.UtcNow, Provider: request.Provider);

        var security = Substitute.For<IOptionsMonitor<SourceSecuritySettings>>();
        security.CurrentValue.Returns(new SourceSecuritySettings());
        var gitHub = Substitute.For<IOptionsMonitor<GitHubSourceSettings>>();
        gitHub.CurrentValue.Returns(new GitHubSourceSettings());
        var azure = Substitute.For<IOptionsMonitor<AzureProviderSettings>>();
        azure.CurrentValue.Returns(new AzureProviderSettings());
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());

        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IProviderCredentialStore>());
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var factory = new ConnectorFactory(
            security, gitHub, Substitute.For<ISshHostKeyStore>(),
            new ConnapseAwsCredentials(scopes, NullLogger<ConnapseAwsCredentials>.Instance),
            new ConnapseAzureCredentials(azure), http, NullLogger<ConnectorFactory>.Instance);

        return (GitHubConnector)factory.Create(source);
    }
}
