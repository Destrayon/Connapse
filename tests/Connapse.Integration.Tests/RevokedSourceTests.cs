using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search.Keyword;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// A source whose remote refuses access — a public repository made private — fails closed: the
/// sync engine marks it, search leaves its documents out, and a later successful read brings them
/// back. Nothing is deleted on a refusal.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class RevokedSourceTests(SharedWebAppFixture fixture)
{
    private const string Term = "quuxwidget";
    private const string Model = "revoked-test-model";

    private async Task<(Guid SourceId, Guid DocumentId)> SeedSourceWithChunkAsync(IServiceProvider sp)
    {
        var source = await sp.GetRequiredService<ISourceStore>().CreateAsync(new CreateSourceRequest(
            $"rv-{Guid.NewGuid():N}"[..20], ConnectionId: null,
            ScopeJson: """{"owner":"octocat","repo":"hello","kind":"Docs"}""", Provider: ConnectionProvider.GitHub));

        await using var db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            SourceId = source.Id,
            FileName = "readme.md",
            Path = "/readme.md",
            ContentHash = Guid.NewGuid().ToString("N"),
            Status = "Ready",
            CreatedAt = DateTime.UtcNow,
            Metadata = [],
        };
        db.Documents.Add(document);

        var chunkId = Guid.NewGuid();
        db.Chunks.Add(new ChunkEntity
        {
            Id = chunkId, DocumentId = document.Id, OwnerId = source.Id,
            Content = $"the {Term} lives here", ChunkIndex = 0, Metadata = [],
        });
        db.ChunkVectors.Add(new ChunkVectorEntity
        {
            ChunkId = chunkId, DocumentId = document.Id, OwnerId = source.Id,
            Embedding = new Vector(new float[] { 1f, 0f, 0f }), ModelId = Model,
            ContentHash = $"hash-{chunkId:N}", Dimensions = 3,
        });
        await db.SaveChangesAsync();

        return (source.Id, document.Id);
    }

    private static async Task<int> KeywordHitsAsync(IServiceProvider sp, Guid sourceId)
    {
        await using var db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var hits = await new KeywordSearchService(db, NullLogger<KeywordSearchService>.Instance).SearchAsync(
            Term, new SearchOptions(TopK: 10, ContainerId: sourceId.ToString(), Mode: SearchMode.Keyword),
            SearchScopes.Unrestricted);
        return hits.Count;
    }

    private static async Task<int> VectorHitsAsync(IServiceProvider sp, Guid sourceId)
    {
        var hits = await sp.GetRequiredService<IVectorStore>().SearchAsync(
            [1f, 0f, 0f], topK: 10,
            new Dictionary<string, string> { ["containerId"] = sourceId.ToString(), ["modelId"] = Model },
            SearchScopes.Unrestricted);
        return hits.Count;
    }

    [Fact]
    public async Task Search_SourceAccessRevoked_LeavesItsDocumentsOutUntilCleared()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var sources = sp.GetRequiredService<ISourceStore>();
        var (sourceId, _) = await SeedSourceWithChunkAsync(sp);

        (await KeywordHitsAsync(sp, sourceId)).Should().Be(1);
        (await VectorHitsAsync(sp, sourceId)).Should().Be(1);

        await sources.UpdateAccessRevokedAsync(sourceId, DateTime.UtcNow);
        (await KeywordHitsAsync(sp, sourceId)).Should().Be(0);
        (await VectorHitsAsync(sp, sourceId)).Should().Be(0);

        await sources.UpdateAccessRevokedAsync(sourceId, revokedAt: null);
        (await KeywordHitsAsync(sp, sourceId)).Should().Be(1);
        (await VectorHitsAsync(sp, sourceId)).Should().Be(1);
    }

    [Fact]
    public async Task SyncSourceAsync_AccessRefusedThenRestored_MarksAndClearsTheSource()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var sources = sp.GetRequiredService<ISourceStore>();
        var (sourceId, documentId) = await SeedSourceWithChunkAsync(sp);
        var factory = new SwitchableFactory();
        var service = new SourceSyncService(
            sp.GetRequiredService<IServiceScopeFactory>(), factory, new RecordingIngestionQueue(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<SourceSyncService>());

        factory.Refuse = true;
        var refused = await service.SyncSourceAsync((await sources.GetAsync(sourceId))!, connection: null, CancellationToken.None);

        refused.Error.Should().NotBeNull();
        (await sources.GetAsync(sourceId))!.AccessRevokedAt.Should().NotBeNull();
        (await KeywordHitsAsync(sp, sourceId)).Should().Be(0);

        await using (var db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync())
            (await db.Documents.AnyAsync(d => d.Id == documentId)).Should().BeTrue("a refusal hides content; it does not delete it");

        factory.Refuse = false;
        var restored = await service.SyncSourceAsync((await sources.GetAsync(sourceId))!, connection: null, CancellationToken.None);

        restored.Error.Should().BeNull();
        (await sources.GetAsync(sourceId))!.AccessRevokedAt.Should().BeNull();
        (await KeywordHitsAsync(sp, sourceId)).Should().Be(1);
    }

    /// <summary>A connector whose remote either refuses access or reports no changes.</summary>
    private sealed class SwitchableFactory : IConnectorFactory
    {
        public bool Refuse { get; set; }

        public IConnector Create(Source source, Connection connection, string? secret = null) =>
            throw new InvalidOperationException("connection-less only");

        public IConnector Create(Source source) => new Connector(this);

        private sealed class Connector(SwitchableFactory owner) : ISyncCursorConnector
        {
            public ConnectorType Type => ConnectorType.GitHub;
            public bool SupportsLiveWatch => false;

            public Task<SyncDelta> GetChangesAsync(string? cursor, CancellationToken ct = default) =>
                owner.Refuse
                    ? throw new SourceAccessRevokedException("the repository is no longer public", new IOException("401"))
                    : Task.FromResult(new SyncDelta([], [], "c1", RequiresFullResync: false));

            public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(false);
            public string ResolveJobPath(string relativePath) => relativePath;
            public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default) => throw new NotSupportedException();
        }
    }
}
