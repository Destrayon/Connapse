using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using Connapse.Storage.Data;
using Connapse.Web.Services;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// A connection-less GitHub docs source synced end to end through <see cref="SourceSyncService"/>
/// against real PostgreSQL: the delta path, the stored SHA cursor, and deletion by clone diff.
/// <para>
/// The remote is a local repository rather than github.com, so CI needs no network and is not
/// subject to the anonymous rate limit. The connector is otherwise the real one — the seam is
/// only where <see cref="IConnectorFactory"/> would have built the public clone URL.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class GitHubSourceSyncIntegrationTests(SharedWebAppFixture fixture) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gh-sync-" + Guid.NewGuid().ToString("N"));

    private string Upstream => Path.Combine(_root, "upstream");

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            System.IO.File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>Builds a GitHub connector for whatever source it is handed, fetching from the local upstream.</summary>
    private sealed class LocalGitHubConnectorFactory(string upstream, string mirrorRoot, IReadOnlyList<string>? exclude = null) : IConnectorFactory
    {
        public IConnector Create(Source source, Connection connection, string? secret = null) =>
            throw new InvalidOperationException("a public GitHub source has no connection");

        public IConnector Create(Source source) => new GitHubConnector(new GitHubConnectorConfig
        {
            Owner = "octocat",
            Repo = "docs",
            MirrorPath = Path.Combine(mirrorRoot, source.Id.ToString("N")),
            RemoteUrl = upstream,
            ExcludePatterns = exclude ?? [],
        });
    }

    private static void DeleteDirectory(string path)
    {
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            System.IO.File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    private static async Task<List<string>> IndexedPathsAsync(IServiceProvider sp, Guid sourceId)
    {
        var dbFactory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var ctx = await dbFactory.CreateDbContextAsync();
        return await ctx.Documents.Where(d => d.SourceId == sourceId).Select(d => d.Path).ToListAsync();
    }

    private string Commit(IDictionary<string, string>? write = null, params string[] delete)
    {
        if (!Repository.IsValid(Upstream))
            Repository.Init(Upstream);

        using var repo = new Repository(Upstream);
        foreach (var (path, content) in write ?? new Dictionary<string, string>())
        {
            string full = Path.Combine(Upstream, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllText(full, content);
            Commands.Stage(repo, path);
        }

        foreach (string path in delete)
        {
            System.IO.File.Delete(Path.Combine(Upstream, path));
            Commands.Stage(repo, path);
        }

        var who = new Signature("t", "t@example.com", DateTimeOffset.UtcNow);
        return repo.Commit("change", who, who).Sha;
    }

    private (SourceSyncService Service, RecordingIngestionQueue Queue) BuildService(
        IServiceProvider sp, IReadOnlyList<string>? exclude = null)
    {
        var queue = new RecordingIngestionQueue();
        var service = new SourceSyncService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new LocalGitHubConnectorFactory(Upstream, Path.Combine(_root, "mirrors"), exclude),
            queue,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<SourceSyncService>());

        return (service, queue);
    }

    private static Task<Source> SeedGitHubSourceAsync(IServiceProvider sp) =>
        sp.GetRequiredService<ISourceStore>().CreateAsync(new CreateSourceRequest(
            $"gh-{Guid.NewGuid():N}"[..20],
            ConnectionId: null,
            ScopeJson: """{"owner":"octocat","repo":"docs","kind":"docs"}""",
            Provider: ConnectionProvider.GitHub));

    [Fact]
    public async Task SyncSourceAsync_ConnectionLessGitHubSource_IngestsDocsAndStoresTheHeadSha()
    {
        string head = Commit(new Dictionary<string, string>
        {
            ["README.md"] = "# Readme",
            ["docs/guide.md"] = "guide",
            ["src/app.cs"] = "code is out of scope",
        });

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var source = await SeedGitHubSourceAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);

        var result = await service.SyncSourceAsync(source, connection: null, CancellationToken.None);

        result.Error.Should().BeNull();
        result.UsedDeltaPath.Should().BeTrue("the GitHub connector reports changes against a commit SHA");
        result.Upserted.Should().Be(2);
        queue.Jobs.Select(j => j.Path).Should().BeEquivalentTo("/README.md", "/docs/guide.md");
        (await sources.GetAsync(source.Id))!.SyncCursor.Should().Be(head);
    }

    [Fact]
    public async Task SyncSourceAsync_SecondCycleWithUnchangedHead_IsANoOp()
    {
        string head = Commit(new Dictionary<string, string> { ["a.md"] = "a" });

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var source = await SeedGitHubSourceAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);

        await service.SyncSourceAsync(source, connection: null, CancellationToken.None);
        var synced = (await sources.GetAsync(source.Id))!;
        queue.Jobs.Clear();

        var second = await service.SyncSourceAsync(synced, connection: null, CancellationToken.None);

        second.Upserted.Should().Be(0);
        second.Deleted.Should().Be(0);
        queue.Jobs.Should().BeEmpty();
        (await sources.GetAsync(source.Id))!.SyncCursor.Should().Be(head);
    }

    [Fact]
    public async Task SyncSourceAsync_DocDeletedUpstream_RemovesItsDocumentAndAdvancesTheCursor()
    {
        Commit(new Dictionary<string, string> { ["keep.md"] = "k", ["gone.md"] = "g" });

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var source = await SeedGitHubSourceAsync(scope.ServiceProvider);
        var (service, _) = BuildService(scope.ServiceProvider);

        await service.SyncSourceAsync(source, connection: null, CancellationToken.None);
        string second = Commit(delete: "gone.md");

        var result = await service.SyncSourceAsync(
            (await sources.GetAsync(source.Id))!, connection: null, CancellationToken.None);

        result.Deleted.Should().Be(1);
        await using var ctx = await dbFactory.CreateDbContextAsync();
        (await ctx.Documents.Where(d => d.SourceId == source.Id).Select(d => d.Path).ToListAsync())
            .Should().Equal("/keep.md");
        (await sources.GetAsync(source.Id))!.SyncCursor.Should().Be(second);
    }

    [Fact]
    public async Task SyncSourceAsync_MirrorLost_TheResyncDeletesWhatWentAwayMeanwhile()
    {
        Commit(new Dictionary<string, string> { ["a.md"] = "a", ["b.md"] = "b", ["gone.md"] = "g" });

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var source = await SeedGitHubSourceAsync(scope.ServiceProvider);
        var (service, _) = BuildService(scope.ServiceProvider);
        await service.SyncSourceAsync(source, connection: null, CancellationToken.None);

        // History rewritten upstream (a force-push to a new root) and the mirror lost: the stored
        // SHA exists nowhere, so the cursor is cleared and a full listing runs.
        DeleteDirectory(Upstream);
        Commit(new Dictionary<string, string> { ["a.md"] = "a", ["b.md"] = "b" });
        DeleteDirectory(Path.Combine(_root, "mirrors"));
        var resync = await service.SyncSourceAsync(
            (await sources.GetAsync(source.Id))!, connection: null, CancellationToken.None);
        resync.RequiredResync.Should().BeTrue();

        var full = await service.SyncSourceAsync(
            (await sources.GetAsync(source.Id))!, connection: null, CancellationToken.None);

        full.Deleted.Should().Be(1);
        (await IndexedPathsAsync(scope.ServiceProvider, source.Id)).Should().BeEquivalentTo("/a.md", "/b.md");
    }

    [Fact]
    public async Task SyncSourceAsync_PatternsNarrowed_RemovesNewlyExcludedDocsWithoutANewCommit()
    {
        Commit(new Dictionary<string, string> { ["guide.md"] = "g", ["CHANGELOG.md"] = "c" });

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var source = await SeedGitHubSourceAsync(scope.ServiceProvider);
        var (service, _) = BuildService(scope.ServiceProvider, exclude: ["CHANGELOG.md"]);
        await service.SyncSourceAsync(source, connection: null, CancellationToken.None);
        (await IndexedPathsAsync(scope.ServiceProvider, source.Id)).Should().BeEquivalentTo("/guide.md");

        // The test factory reads patterns from its own argument, so widening is simulated by a new
        // factory; the scope edit is what clears the cursor.
        await sources.UpdateAsync(source.Id, new UpdateSourceRequest(
            ScopeJson: """{"owner":"octocat","repo":"docs","kind":"docs","excludePatterns":["guide.md"]}"""));
        var (narrowed, _) = BuildService(scope.ServiceProvider, exclude: ["guide.md"]);

        await narrowed.SyncSourceAsync((await sources.GetAsync(source.Id))!, connection: null, CancellationToken.None);

        (await IndexedPathsAsync(scope.ServiceProvider, source.Id)).Should().BeEquivalentTo("/CHANGELOG.md");
    }

    [Fact]
    public async Task SyncAllAsync_IncludesConnectionLessSources()
    {
        string head = Commit(new Dictionary<string, string> { ["a.md"] = "a" });

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var source = await SeedGitHubSourceAsync(scope.ServiceProvider);
        var (service, _) = BuildService(scope.ServiceProvider);

        await service.SyncAllAsync(CancellationToken.None);

        (await sources.GetAsync(source.Id))!.SyncCursor.Should().Be(head,
            "the scheduled cycle must reach sources that have no connection");
    }
}
