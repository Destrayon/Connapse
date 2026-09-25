using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Storage.Connectors;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// A connection-less GitHub issues-and-pull-requests source synced end to end through
/// <see cref="SourceSyncService"/> against real PostgreSQL. GitHub itself is the in-memory
/// <see cref="FakeGitHubApi"/>, so CI needs no network and spends no anonymous budget.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class GitHubIssuesSyncIntegrationTests(SharedWebAppFixture fixture) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gh-issues-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitHubApi _api = new();

    public void Dispose()
    {
        _api.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Builds the real issues connector, reading the fake API instead of api.github.com.</summary>
    private sealed class FakeApiConnectorFactory(FakeGitHubApi api, string root) : IConnectorFactory
    {
        public IConnector Create(Source source, Connection connection, string? secret = null) =>
            throw new InvalidOperationException("a public GitHub source has no connection");

        public IConnector Create(Source source) => new GitHubConnector(
            new GitHubConnectorConfig
            {
                Owner = "octocat",
                Repo = "hello",
                Kind = GitHubContentKind.IssuesAndPullRequests,
                MirrorPath = Path.Combine(root, source.Id.ToString("N")),
                ApiBaseUrl = FakeGitHubApi.BaseUrl,
            },
            api.CreateClient());
    }

    private (SourceSyncService Service, RecordingIngestionQueue Queue) BuildService(IServiceProvider sp)
    {
        var queue = new RecordingIngestionQueue();
        var service = new SourceSyncService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new FakeApiConnectorFactory(_api, _root),
            queue,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<SourceSyncService>());

        return (service, queue);
    }

    private static Task<Source> SeedIssuesSourceAsync(IServiceProvider sp) =>
        sp.GetRequiredService<ISourceStore>().CreateAsync(new CreateSourceRequest(
            $"ghi-{Guid.NewGuid():N}"[..20],
            ConnectionId: null,
            ScopeJson: """{"owner":"octocat","repo":"hello","kind":"IssuesAndPullRequests"}""",
            Provider: ConnectionProvider.GitHub));

    [Fact]
    public async Task SyncSourceAsync_IssuesSource_EnqueuesRecordsCarryingEdgeMetadata()
    {
        _api.UpsertIssue(1, "Crash on start", labels: ["bug"]);
        _api.AddComment(1, "alice", "Me too");
        _api.UpsertIssue(2, "Fix the crash", "Fixes #1", pullRequest: true, merged: true);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var source = await SeedIssuesSourceAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);

        var result = await service.SyncSourceAsync(source, connection: null, CancellationToken.None);

        result.Error.Should().BeNull();
        result.UsedDeltaPath.Should().BeTrue();
        queue.Jobs.Select(j => j.Path).Should().BeEquivalentTo("/issues/1.md", "/pulls/2.md");

        // The connector's metadata reaches the job, next to the sync's own keys.
        var pull = queue.Jobs.Single(j => j.Path == "/pulls/2.md").Options.Metadata!;
        pull["github:closes"].Should().Be("1");
        pull["github:state"].Should().Be("merged");
        pull["Source"].Should().Be("SourceSync");
        queue.Jobs.Single(j => j.Path == "/issues/1.md").Options.Metadata!["github:labels"].Should().Be("bug");
        queue.Jobs.Should().OnlyContain(j => j.Options.Strategy == ChunkingStrategy.Record,
            "records are chunked as records even though their paths end in .md");

        (await sources.GetAsync(source.Id))!.SyncCursor.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SyncSourceAsync_IdleSecondCycle_IsANoOpThatKeepsTheCursorMoving()
    {
        _api.UpsertIssue(1, "Crash");

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var source = await SeedIssuesSourceAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);

        await service.SyncSourceAsync(source, connection: null, CancellationToken.None);
        var synced = (await sources.GetAsync(source.Id))!;
        queue.Jobs.Clear();

        var second = await service.SyncSourceAsync(synced, connection: null, CancellationToken.None);

        second.Error.Should().BeNull();
        second.Upserted.Should().Be(0);
        queue.Jobs.Should().BeEmpty();
        (await sources.GetAsync(source.Id))!.LastSyncStatus.Should().Be(SyncStatus.Succeeded);
    }

    [Fact]
    public async Task SyncSourceAsync_RepositoryGone_RecordsTheFailureAndKeepsTheCursor()
    {
        _api.UpsertIssue(1, "Crash");

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var source = await SeedIssuesSourceAsync(scope.ServiceProvider);
        var (service, _) = BuildService(scope.ServiceProvider);

        await service.SyncSourceAsync(source, connection: null, CancellationToken.None);
        var synced = (await sources.GetAsync(source.Id))!;
        _api.Gone = true;

        var result = await service.SyncSourceAsync(synced, connection: null, CancellationToken.None);

        result.Error.Should().Contain("can no longer be read");
        var after = (await sources.GetAsync(source.Id))!;
        after.LastSyncStatus.Should().Be(SyncStatus.Failed);
        after.SyncCursor.Should().Be(synced.SyncCursor);
    }
}
