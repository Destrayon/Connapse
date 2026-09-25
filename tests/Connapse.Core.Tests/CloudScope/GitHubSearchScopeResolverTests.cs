using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using Connapse.Storage.Connectors.GitHub;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.CloudScope;

/// <summary>
/// Which private GitHub repositories a user may search, row by row of the design's decision table:
/// only an explicit read-or-higher answer for the user's linked account opens a repository.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitHubSearchScopeResolverTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid ConnectionId = Guid.NewGuid();
    private readonly GitHubStub _github = new();
    private readonly IGitHubIdentityLinkReader _links = Substitute.For<IGitHubIdentityLinkReader>();
    private readonly List<Source> _sources = [];

    private static Source SourceFor(long repoId, bool isPrivate, string repo = "infra") => new(
        Guid.NewGuid(), $"acme/{repo} issues", null, ConnectionId,
        JsonSerializer.Serialize(new { owner = "acme", repo, repoId, kind = "IssuesAndPullRequests", @private = isPrivate }),
        DateTime.UtcNow, DateTime.UtcNow);

    private GitHubSearchScopeResolver Resolver()
    {
        var sources = Substitute.For<ISourceStore>();
        sources.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ => _sources.ToList());
        var connections = Substitute.For<IConnectionStore>();
        connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new Connection(ConnectionId, "github-acme", ConnectionProvider.GitHub, """{"installationId":7,"account":"acme"}""",
                null, DateTime.UtcNow, DateTime.UtcNow),
        ]);

        using var rsa = RSA.Create(2048);
        var store = Substitute.For<IProviderCredentialStore>();
        store.GetGitHubAppMaterialAsync(Arg.Any<CancellationToken>()).Returns(new GitHubAppCredentialMaterial(
            new GitHubAppRegistration(42, "connapse", "Iv1.x", "acme", "https://github.com/apps/connapse"), rsa.ExportRSAPrivateKeyPem(), "s"));
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(connections);
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_github, disposeHandler: false));
        var app = new ConnapseGitHubApp(scopes, http, NullLogger<ConnapseGitHubApp>.Instance) { ApiBaseUrl = "https://api.github.test" };
        var access = new GitHubRepositoryAccess(new GitHubCredentialPool(app, scopes), http,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<GitHubRepositoryAccess>.Instance) { ApiBaseUrl = "https://api.github.test" };

        return new GitHubSearchScopeResolver(sources, connections, _links, access, NullLogger<GitHubSearchScopeResolver>.Instance);
    }

    private void Linked(long id = 583231, string login = "octocat") =>
        _links.GetLinkAsync(User, Arg.Any<CancellationToken>()).Returns(new GitHubIdentityRef(id, login));

    [Fact]
    public async Task NoPrincipal_GrantsNothing() =>
        (await Resolver().ResolveAsync(null)).Matches.Should().BeEmpty();

    [Fact]
    public async Task UnlinkedUser_GrantsNothing()
    {
        _sources.Add(SourceFor(100, isPrivate: true));

        (await Resolver().ResolveAsync(User)).Matches.Should().BeEmpty();
        _github.PermissionChecks.Should().Be(0, "an unlinked user is denied without asking GitHub");
    }

    [Theory]
    [InlineData("read", null, true)]
    [InlineData("write", null, true)]
    [InlineData("admin", null, true)]
    [InlineData("read", "triage", true)]
    [InlineData("none", null, false)]
    [InlineData("something-new", null, false)]
    public async Task LinkedUser_OnlyReadOrHigherOpensTheRepository(string permission, string? role, bool allowed)
    {
        _sources.Add(SourceFor(100, isPrivate: true));
        Linked();
        _github.Permission = (permission, role);

        var result = await Resolver().ResolveAsync(User);

        if (allowed) result.Matches.Select(m => m.Value).Should().Equal("github://100/");
        else result.Matches.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task LinkedUser_GitHubCannotAnswer_GrantsNothing(HttpStatusCode status)
    {
        _sources.Add(SourceFor(100, isPrivate: true));
        Linked();
        _github.PermissionStatus = status;

        (await Resolver().ResolveAsync(User)).Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task PublicSources_AreNotChecked()
    {
        _sources.Add(SourceFor(200, isPrivate: false, repo: "docs"));
        Linked();

        (await Resolver().ResolveAsync(User)).Matches.Should().BeEmpty();
        _github.PermissionChecks.Should().Be(0);
    }

    [Fact]
    public async Task RenamedAccount_IsCheckedUnderItsCurrentLogin()
    {
        _sources.Add(SourceFor(100, isPrivate: true));
        Linked(login: "old-name");
        _github.CurrentLogin = "new-name";

        (await Resolver().ResolveAsync(User)).Matches.Select(m => m.Value).Should().Equal("github://100/");
        _github.CheckedLogins.Should().Equal("new-name");
    }

    [Fact]
    public async Task Permission_IsAskedThroughTheDocumentedRoute()
    {
        _sources.Add(SourceFor(100, isPrivate: true));
        Linked();

        await Resolver().ResolveAsync(User);

        _github.PermissionPaths.Should().ContainSingle().Which.Should().Be("/repos/acme/infra/collaborators/octocat/permission");
    }

    [Fact]
    public async Task NameNowBelongingToADifferentRepository_GrantsNothing()
    {
        _sources.Add(SourceFor(100, isPrivate: true));
        Linked();
        _github.RepositoryIdAtName = 999; // the indexed repository was transferred and another took its name

        (await Resolver().ResolveAsync(User)).Matches.Should().BeEmpty();
        _github.PermissionChecks.Should().Be(0, "no permission on another repository may stand in for the indexed one");
    }

    [Fact]
    public async Task LoginNowBelongingToADifferentAccount_GrantsNothing()
    {
        _sources.Add(SourceFor(100, isPrivate: true));
        Linked();
        _github.AnsweredUserId = 999; // the linked account was renamed and a collaborator took its old login

        (await Resolver().ResolveAsync(User)).Matches.Should().BeEmpty(
            "an answer about another account must not grant the linked one");
    }

    [Fact]
    public async Task AnswersAreCached_PerRepositoryAndAccount()
    {
        _sources.Add(SourceFor(100, isPrivate: true));
        Linked();
        var resolver = Resolver();

        await resolver.ResolveAsync(User);
        await resolver.ResolveAsync(User);

        _github.PermissionChecks.Should().Be(1);
    }

    /// <summary>GitHub: tokens for installation 7, user lookup by id, and the permission endpoint.</summary>
    private sealed class GitHubStub : HttpMessageHandler
    {
        public (string Permission, string? Role) Permission { get; set; } = ("read", null);
        public HttpStatusCode PermissionStatus { get; set; } = HttpStatusCode.OK;
        public string CurrentLogin { get; set; } = "octocat";
        public int PermissionChecks { get; private set; }
        public List<string> CheckedLogins { get; } = [];
        public List<string> PermissionPaths { get; } = [];
        public long? RepositoryIdAtName { get; set; }
        public long AnsweredUserId { get; set; } = 583231;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/app/installations")
                return Json(new[] { new { id = 7, account = new { login = "acme", type = "Organization" }, repository_selection = "selected", html_url = "https://github.com/x" } });
            if (path == "/app/installations/7/access_tokens")
                return Json(new { token = "tok-7", expires_at = DateTimeOffset.UtcNow.AddHours(1) });
            if (path.StartsWith("/user/"))
                return Json(new { login = CurrentLogin, id = 583231 });
            if (path.StartsWith("/repos/acme/") && path.Split('/').Length == 4)
                return Json(new { id = RepositoryIdAtName ?? long.Parse(path.Split('/')[3] == "infra" ? "100" : "200") });
            if (path.Contains("/collaborators/") && path.EndsWith("/permission"))
            {
                PermissionChecks++;
                PermissionPaths.Add(path);
                CheckedLogins.Add(path.Split('/')[^2]);
                if (PermissionStatus != HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(PermissionStatus) { Content = new StringContent("""{"message":"x"}""") });
                return Json(new { permission = Permission.Permission, role_name = Permission.Role, user = new { login = path.Split('/')[^2], id = AnsweredUserId } });
            }

            throw new InvalidOperationException("unexpected " + request.RequestUri);
        }

        private static Task<HttpResponseMessage> Json(object body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) });
    }
}
