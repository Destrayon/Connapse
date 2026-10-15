using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>A content-only custom source syncs with its own token, and deletes by document-ID prefix.</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CustomSourceSyncIntegrationTests(SharedWebAppFixture fixture)
{
    private const string Token = "middle-man-token";

    [Fact]
    public async Task Sync_ContentOnlySource_PassesItsTokenAndDeletesByPrefixButKeepsTheNewPath()
    {
        Source source = await SeedSourceAsync();
        await SeedDocumentsAsync(source.Id, ["/a/old-name.md", "/b/b.md", "/c/c.md"]);
        await SeedDocumentsAsync(source.Id, Enumerable.Range(0, 30).Select(i => $"/keep{i}/doc.md").ToArray());

        // "a" was renamed, "b" deleted, "c" untouched.
        DeltaConnector connector = new(new SyncDelta(
            Upserted: [new ConnectorFile("/a/new-name.md", 10, DateTime.UtcNow, "text/markdown")],
            DeletedPaths: [],
            NextCursor: "next",
            RequiresFullResync: false,
            DeletedPrefixes: ["/a/", "/b/"]));
        CapturingFactory factory = new(connector);

        SourceSyncResult result = await SyncAsync(source, factory);

        result.Error.Should().BeNull();
        factory.Secret.Should().Be(Token);
        IReadOnlyList<string> paths = await PathsAsync(source.Id);
        paths.Should().Contain(["/a/new-name.md", "/c/c.md"]);
        paths.Should().NotContain(["/a/old-name.md", "/b/b.md"]);
    }

    private async Task<Source> SeedSourceAsync()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISourceStore>().CreateAsync(new CreateSourceRequest(
            $"custom-sync-{Guid.NewGuid():N}"[..24],
            ConnectionId: null,
            ScopeJson: new CustomSourceScope("https://bridge.acme.test", "eng").ToJson(),
            Provider: ConnectionProvider.Custom,
            Secret: Token));
    }

    private async Task SeedDocumentsAsync(Guid sourceId, IReadOnlyList<string> paths)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        await using KnowledgeDbContext context = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        foreach (string path in paths)
        {
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, ingestion_status, created_at) "
                + "VALUES ({0}, NULL, {1}, {2}, {3}, '', 1, 'Ready', now())",
                Guid.NewGuid(), sourceId, Path.GetFileName(path), path);
        }
    }

    private async Task<IReadOnlyList<string>> PathsAsync(Guid sourceId)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        await using KnowledgeDbContext context = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        return await context.Documents.Where(d => d.SourceId == sourceId).Select(d => d.Path).ToListAsync();
    }

    private async Task<SourceSyncResult> SyncAsync(Source source, IConnectorFactory factory)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        SourceSyncService service = new(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            factory,
            new RecordingIngestionQueue(),
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<SourceSyncService>());
        return await service.SyncSourceAsync(source, connection: null, CancellationToken.None);
    }

    private sealed class CapturingFactory(IConnector connector) : IConnectorFactory
    {
        public string? Secret { get; private set; }

        public IConnector Create(Source source, Connection connection, string? secret = null) =>
            throw new NotSupportedException("custom sources have no connection");

        public IConnector Create(Source source, string? secret = null)
        {
            Secret = secret;
            return connector;
        }
    }

    private sealed class DeltaConnector(SyncDelta delta) : ISyncCursorConnector
    {
        public ConnectorType Type => ConnectorType.Custom;
        public bool SupportsLiveWatch => false;
        public Task<SyncDelta> GetChangesAsync(string? cursor, CancellationToken ct = default) => Task.FromResult(delta);
        public string ResolveJobPath(string relativePath) => relativePath;
        public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConnectorFile>>([]);
        public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream());
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(false);
        public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
