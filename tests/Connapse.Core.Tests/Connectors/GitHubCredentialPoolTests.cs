using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using Connapse.Storage.Connectors.GitHub;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// Reading GitHub as the App: which installation each request spends, failover when one is spent,
/// and the visibility check that stops a repository gone private from being read at all.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitHubCredentialPoolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gh-pool-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitHubApi _api = new();
    private readonly ManualClock _clock = new();

    public void Dispose()
    {
        _api.Dispose();
        if (!Directory.Exists(_root)) return;

        // Git marks pack and object files read-only, which Directory.Delete refuses on Windows.
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>A pool over an App installed at <paramref name="installations"/>, each added as a connection, minting "tok-{id}".</summary>
    private GitHubCredentialPool Pool(params long[] installations) => Pool(installations, connected: installations);

    private GitHubCredentialPool Pool(long[] installations, long[] connected)
    {
        using var rsa = RSA.Create(2048);
        var store = Substitute.For<IProviderCredentialStore>();
        store.GetGitHubAppMaterialAsync(Arg.Any<CancellationToken>()).Returns(new GitHubAppCredentialMaterial(
            new GitHubAppRegistration(42, "connapse-test", null, "octo-org", "https://github.com/apps/connapse-test"),
            rsa.ExportRSAPrivateKeyPem(), null));

        var connections = Substitute.For<IConnectionStore>();
        connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(connected
            .Select(id => new Connection(Guid.NewGuid(), $"install {id}", ConnectionProvider.GitHub,
                $$"""{"installationId":{{id}},"account":"octo-org"}""", null, DateTime.UtcNow, DateTime.UtcNow))
            .ToList());

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(connections);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new AppStub(installations)));

        var app = new ConnapseGitHubApp(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), http,
            NullLogger<ConnapseGitHubApp>.Instance, _clock) { ApiBaseUrl = "https://api.github.test" };

        return new GitHubCredentialPool(
            app, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _clock);
    }

    private static HttpResponseMessage Budget(int remaining, DateTimeOffset reset)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("x-ratelimit-remaining", remaining.ToString());
        response.Headers.Add("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString());
        return response;
    }

    private static readonly IReadOnlySet<long> None = new HashSet<long>();

    // ── Choosing an installation ───────────────────────────────────────────

    [Fact]
    public async Task AcquireAsync_InstallationWithNoConnection_IsNeverBorrowed()
    {
        var pool = Pool(installations: [1, 2], connected: [1]);
        pool.Observe(1, Budget(0, _clock.Now.AddHours(1)));
        pool.Observe(2, Budget(5000, _clock.Now.AddHours(1)));

        var act = () => pool.AcquireAsync(GitHubAccess.Public(1), None);

        await act.Should().ThrowAsync<GitHubRateLimitedException>(
            "anyone can install a public App; an installation no administrator added spends nobody's budget");
    }

    [Fact]
    public async Task AcquireAsync_PrefersTheSourcesOwnInstallation()
    {
        var pool = Pool(1, 2);
        pool.Observe(1, Budget(100, _clock.Now.AddHours(1)));
        pool.Observe(2, Budget(4000, _clock.Now.AddHours(1)));

        (await pool.AcquireAsync(GitHubAccess.Public(1), None)).InstallationId.Should().Be(1,
            "a source reads as its own installation while it has budget, even when another has more");
    }

    [Fact]
    public async Task AcquireAsync_OwnInstallationSpent_PublicReadUsesTheInstallationWithTheMostLeft()
    {
        var pool = Pool(1, 2, 3);
        pool.Observe(1, Budget(0, _clock.Now.AddMinutes(30)));
        pool.Observe(2, Budget(200, _clock.Now.AddHours(1)));
        pool.Observe(3, Budget(3000, _clock.Now.AddHours(1)));

        var lease = await pool.AcquireAsync(GitHubAccess.Public(1), None);

        lease.InstallationId.Should().Be(3);
        lease.Token.Should().Be("tok-3");
    }

    [Fact]
    public async Task AcquireAsync_PinnedInstallationSpent_NeverBorrowsAnother()
    {
        var pool = Pool(1, 2);
        var reset = _clock.Now.AddMinutes(20);
        pool.Observe(1, Budget(0, reset));

        Func<Task> act = () => pool.AcquireAsync(GitHubAccess.Pinned(1), None);

        (await act.Should().ThrowAsync<GitHubRateLimitedException>()).Which.ResetAt.Should().Be(
            DateTimeOffset.FromUnixTimeSeconds(reset.ToUnixTimeSeconds()),
            "a private repository is read only through the installation that covers it");
    }

    [Fact]
    public async Task AcquireAsync_EverythingSpent_SaysWhenTheEarliestResets()
    {
        var pool = Pool(1, 2);
        pool.Observe(1, Budget(0, _clock.Now.AddMinutes(50)));
        pool.Observe(2, Budget(0, _clock.Now.AddMinutes(10)));

        Func<Task> act = () => pool.AcquireAsync(GitHubAccess.Public(1), None);

        (await act.Should().ThrowAsync<GitHubRateLimitedException>()).Which.ResetAt!.Value
            .Should().BeCloseTo(_clock.Now.AddMinutes(10), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AcquireAsync_BudgetPastItsReset_IsWholeAgain()
    {
        var pool = Pool(1);
        pool.Observe(1, Budget(0, _clock.Now.AddMinutes(5)));

        _clock.Advance(TimeSpan.FromMinutes(6));

        (await pool.AcquireAsync(GitHubAccess.Pinned(1), None)).InstallationId.Should().Be(1);
    }

    [Fact]
    public async Task Refused_SitsTheInstallationOutForItsCooldown()
    {
        var pool = Pool(1, 2);
        pool.Refused(1);

        (await pool.AcquireAsync(GitHubAccess.Public(1), None)).InstallationId.Should().Be(2);

        _clock.Advance(TimeSpan.FromMinutes(6));
        (await pool.AcquireAsync(GitHubAccess.Public(1), None)).InstallationId.Should().Be(1);
    }

    // ── Reading as the App ─────────────────────────────────────────────────

    private GitHubConnectorConfig Config(GitHubContentKind kind) => new()
    {
        Owner = "octocat",
        Repo = "hello",
        Kind = kind,
        MirrorPath = _root,
        ApiBaseUrl = FakeGitHubApi.BaseUrl,
        RemoteUrl = Path.Combine(_root, "no-such-upstream"),
    };

    private GitHubConnector Connector(GitHubContentKind kind, GitHubCredentialPool pool, long preferred = 1) =>
        new(Config(kind), _api.CreateClient(), NullLogger.Instance, new GitHubAuth(pool, GitHubAccess.Public(preferred)));

    [Fact]
    public async Task IssuesSync_ReadsWithTheInstallationsToken()
    {
        _api.UpsertIssue(1, "Crash");

        await Connector(GitHubContentKind.IssuesAndPullRequests, Pool(1)).GetChangesAsync(null);

        _api.Tokens.Should().NotBeEmpty().And.OnlyContain(t => t == "tok-1");
    }

    [Fact]
    public async Task IssuesSync_TokenSpentMidSync_FailsOverToAnotherInstallation()
    {
        for (int n = 1; n <= 3; n++) _api.UpsertIssue(n, "Issue " + n);
        _api.SpentTokens.Add("tok-1");

        var delta = await Connector(GitHubContentKind.IssuesAndPullRequests, Pool(1, 2)).GetChangesAsync(null);

        delta.Upserted.Should().HaveCount(3);
        _api.Tokens.Where(t => t == "tok-2").Should().NotBeEmpty("the spent installation was swapped for the other");
    }

    [Fact]
    public async Task IssuesSync_RepositoryNowPrivate_IsRefusedBeforeAnyIssueIsRead()
    {
        _api.UpsertIssue(1, "Crash");
        _api.Visibility = "private";

        Func<Task> act = () => Connector(GitHubContentKind.IssuesAndPullRequests, Pool(1)).GetChangesAsync(null);

        await act.Should().ThrowAsync<GitHubRepositoryUnavailableException>();
        _api.RequestedPaths.Should().ContainSingle().Which.Should().Be("/repos/octocat/hello");
    }

    [Fact]
    public async Task IssuesSync_NameNowBelongsToADifferentRepository_IsRefused()
    {
        _api.UpsertIssue(1, "Crash");
        var connector = new GitHubConnector(Config(GitHubContentKind.IssuesAndPullRequests) with { RepoId = 999 },
            _api.CreateClient(), NullLogger.Instance, new GitHubAuth(Pool(1), GitHubAccess.Public(1)));

        Func<Task> act = () => connector.GetChangesAsync(null);

        // The fake answers the name with id 1296269: another repository took it.
        await act.Should().ThrowAsync<GitHubRepositoryUnavailableException>()
            .WithInnerException(typeof(InvalidOperationException));
    }

    [Fact]
    public async Task IssuesSync_PrivateSourceOfItsOwnRepository_Syncs()
    {
        _api.UpsertIssue(1, "Crash");
        _api.Visibility = "private";
        var connector = new GitHubConnector(
            Config(GitHubContentKind.IssuesAndPullRequests) with { RepoId = 1296269, IsPrivate = true, RequirePublic = false },
            _api.CreateClient(), NullLogger.Instance, new GitHubAuth(Pool(1), GitHubAccess.Pinned(1)));

        var delta = await connector.GetChangesAsync(null);

        delta.Upserted.Should().ContainSingle().Which.ResourceUri.Should().Be("github://1296269/issues/1.md");
    }

    [Fact]
    public async Task IssuesSync_VisibilityCheckRateLimited_FailsRatherThanPassingAsPartialProgress()
    {
        _api.UpsertIssue(1, "Crash");
        _api.Budget = 0;

        Func<Task> act = () => Connector(GitHubContentKind.IssuesAndPullRequests, Pool(1)).GetChangesAsync(null);

        // A cycle that returns counts as a successful read, which un-hides a source whose repository
        // went private; one whose visibility could not be checked must not return.
        await act.Should().ThrowAsync<GitHubRateLimitedException>();
    }

    [Fact]
    public async Task DocsSync_RepositoryMadePrivateDuringTheFetch_ReturnsNothing()
    {
        string upstream = Path.Combine(_root, "upstream");
        LibGit2Sharp.Repository.Init(upstream);
        using (var repo = new LibGit2Sharp.Repository(upstream))
        {
            File.WriteAllText(Path.Combine(upstream, "README.md"), "# Hello");
            LibGit2Sharp.Commands.Stage(repo, "README.md");
            var who = new LibGit2Sharp.Signature("t", "t@example.com", DateTimeOffset.UtcNow);
            repo.Commit("first", who, who, new LibGit2Sharp.CommitOptions());
        }

        _api.VisibilityAnswers.Enqueue("public");  // before the fetch
        _api.VisibilityAnswers.Enqueue("private"); // after it
        var connector = new GitHubConnector(
            Config(GitHubContentKind.Docs) with { RemoteUrl = upstream, MirrorPath = Path.Combine(_root, "mirror") },
            _api.CreateClient(), NullLogger.Instance, new GitHubAuth(Pool(1), GitHubAccess.Public(1)));

        Func<Task> act = () => connector.GetChangesAsync(null);

        await act.Should().ThrowAsync<GitHubRepositoryUnavailableException>(
            "content fetched from a repository that is no longer public must not reach the index");
    }

    [Theory]
    [InlineData("private")]
    [InlineData("internal")]
    public async Task DocsSync_RepositoryNotPublic_IsRefusedBeforeFetching(string visibility)
    {
        _api.Visibility = visibility;

        Func<Task> act = () => Connector(GitHubContentKind.Docs, Pool(1)).GetChangesAsync(null);

        await act.Should().ThrowAsync<GitHubRepositoryUnavailableException>();
        Directory.Exists(_root).Should().BeFalse("nothing was fetched");
    }

    // ── Conditional probes ─────────────────────────────────────────────────

    /// <summary>Syncs until the probes hold an ETag for everything, returning the last cursor.</summary>
    private async Task<string?> SettleAsync(GitHubConnector connector)
    {
        string? cursor = (await connector.GetChangesAsync(null)).NextCursor;
        cursor = (await connector.GetChangesAsync(cursor)).NextCursor;
        return cursor;
    }

    [Fact]
    public async Task IssuesSync_IdleRepository_SpendsOnlyFree304s()
    {
        _api.UpsertIssue(1, "Crash");
        _api.AddComment(1, "alice", "Me too");
        var connector = Connector(GitHubContentKind.IssuesAndPullRequests, Pool(1));
        string? cursor = await SettleAsync(connector);
        int requests = _api.Requests, unchanged = _api.NotModified;

        var idle = await connector.GetChangesAsync(cursor);

        idle.Upserted.Should().BeEmpty();
        (_api.Requests - requests).Should().Be(4, "the repository, issues, comments, and review comments are each probed");
        (_api.NotModified - unchanged).Should().Be(4, "an authenticated 304 is not counted against the rate limit");
    }

    [Fact]
    public async Task IssuesSync_CommentEditedOnAnIdleRepository_IsSeenNextCycle()
    {
        _api.UpsertIssue(1, "Crash");
        long id = _api.AddComment(1, "alice", "Tpyo");
        var connector = Connector(GitHubContentKind.IssuesAndPullRequests, Pool(1));
        string? cursor = await SettleAsync(connector);

        _api.EditComment(id, "Typo fixed"); // moves the comment, not its issue

        var next = await connector.GetChangesAsync(cursor);

        next.Upserted.Select(f => f.Path).Should().Equal(["/issues/1.md"],
            "the comment probe sees the edit without waiting for the hourly sweep");
    }

    [Fact]
    public async Task IssuesSync_BudgetRunsOutMidSweep_TheNextCycleSweepsAgainInsteadOfTrustingAStaleProbe()
    {
        _api.UpsertIssue(1, "First");
        var connector = Connector(GitHubContentKind.IssuesAndPullRequests, Pool(1));
        string? cursor = await SettleAsync(connector);

        for (int n = 2; n <= 6; n++) _api.UpsertIssue(n, "Issue " + n);
        _api.Budget = _api.Requests + 3; // repository and issue probes, one page of issues, then spent

        var partial = await connector.GetChangesAsync(cursor);
        partial.Upserted.Should().BeEmpty();

        _api.Budget = null;
        var resumed = await connector.GetChangesAsync(partial.NextCursor);

        resumed.Upserted.Select(f => f.Path).Should().BeEquivalentTo(
            ["/issues/2.md", "/issues/3.md", "/issues/4.md", "/issues/5.md", "/issues/6.md"]);
    }

    [Fact]
    public async Task IssuesSync_EditInTheSameSecondAsTheNewestIssue_IsCaughtByTheHourlySweep()
    {
        _api.UpsertIssue(1, "First");
        _api.UpsertIssue(2, "Second");
        var source = new GitHubRecordSource(
            Config(GitHubContentKind.IssuesAndPullRequests), _api.CreateClient(), NullLogger.Instance, _clock,
            new GitHubAuth(Pool(1), GitHubAccess.Public(1)));
        string? cursor = (await source.GetChangesAsync(null, default)).NextCursor;
        cursor = (await source.GetChangesAsync(cursor, default)).NextCursor;

        _api.EditIssueInTheSameSecond(1, "First, edited"); // sorts behind #2, which the probe sees
        var probed = await source.GetChangesAsync(cursor, default);
        probed.Upserted.Should().BeEmpty("a one-item probe cannot see an edit tied behind the newest item");

        _clock.Advance(TimeSpan.FromHours(1));
        var swept = await source.GetChangesAsync(probed.NextCursor, default);

        swept.Upserted.Select(f => f.Path).Should().Contain("/issues/1.md");
    }

    [Fact]
    public async Task IssuesSync_RepositoryTurnsPrivateAfterSettling_IsStillRefused()
    {
        _api.UpsertIssue(1, "Crash");
        var connector = Connector(GitHubContentKind.IssuesAndPullRequests, Pool(1));
        string? cursor = await SettleAsync(connector);

        _api.Visibility = "private";

        Func<Task> act = () => connector.GetChangesAsync(cursor);
        await act.Should().ThrowAsync<GitHubRepositoryUnavailableException>(
            "a stored ETag must not let a changed repository answer 304");
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    /// <summary>GitHub's App endpoints: the installation list, and a token per installation.</summary>
    private sealed class AppStub(long[] installations) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            object body = path == "/app/installations"
                ? installations.Select(id => new
                {
                    id, account = new { login = "org-" + id, type = "Organization" },
                    repository_selection = "all", html_url = "https://github.com/x",
                }).ToArray()
                : new { token = "tok-" + path.Split('/')[3], expires_at = DateTimeOffset.UtcNow.AddYears(1) };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body)),
            });
        }
    }
}
