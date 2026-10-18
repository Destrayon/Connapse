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
    private const string Token = "source-server-token";

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

    [Fact]
    public async Task Sync_ConnectedSource_UsesTheProvidersTokenAndStoresUrisAndKeysWithoutReingestingForAKeyChange()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        CustomProvider provider = await scope.ServiceProvider.GetRequiredService<ICustomProviderStore>().CreateAsync(
            new SaveCustomProviderRequest($"p-{Guid.NewGuid():N}"[..20], "https://bridge.acme.test", "provider-token", 100, true, null));
        Connection connection = await scope.ServiceProvider.GetRequiredService<IConnectionStore>().CreateAsync(
            new CreateConnectionRequest($"c-{Guid.NewGuid():N}"[..20], ConnectionProvider.Custom, CustomConnectionConfig.ToJson(provider.Id)), null);
        Source source = await scope.ServiceProvider.GetRequiredService<ISourceStore>().CreateAsync(new CreateSourceRequest(
            $"custom-conn-{Guid.NewGuid():N}"[..24], connection.Id,
            new CustomSourceScope("https://bridge.acme.test", "eng").ToJson()));

        DateTime modified = new(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
        string uri = Connapse.Core.Utilities.CustomResourceUri.For(source.Id, "a");
        ConnectorFile first = new("/a/a.md", 10, modified, "text/markdown", uri, AccessKeys: ["folder:", "team:eng"]);
        CapturingFactory factory = new(new DeltaConnector(new SyncDelta([first], [], "c1", false)));
        RecordingIngestionQueue queue = new();

        (await SyncAsync(source, factory, connection, queue)).Error.Should().BeNull();
        int enqueued = queue.Jobs.Count;

        factory.Secret.Should().Be("provider-token", "a source on a provider's connection reads with the provider's token");
        (string? storedUri, string[]? storedKeys) = await RowAsync(source.Id, "/a/a.md");
        storedUri.Should().Be(uri);
        storedKeys.Should().Equal("folder:", "team:eng");

        // Only the keys change: same size and time, so nothing is re-ingested, but the keys are replaced.
        source = (await scope.ServiceProvider.GetRequiredService<ISourceStore>().GetAsync(source.Id))!;
        await MarkReadyAsync(source.Id);
        CapturingFactory second = new(new DeltaConnector(new SyncDelta([first with { AccessKeys = ["team:ops"] }], [], "c2", false)));
        (await SyncAsync(source, second, connection, queue)).Error.Should().BeNull();

        queue.Jobs.Count.Should().Be(enqueued, "a key-only change must not re-embed the document");
        (await RowAsync(source.Id, "/a/a.md")).AccessKeys.Should().Equal("team:ops");
    }

    [Fact]
    public async Task Sync_SourceOnAConnectionWithoutAProvider_UsesTheConnectionsTokenAndStaysContentOnly()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        Connection connection = await scope.ServiceProvider.GetRequiredService<IConnectionStore>().CreateAsync(
            new CreateConnectionRequest($"c-{Guid.NewGuid():N}"[..20], ConnectionProvider.Custom,
                CustomConnectionConfig.ForMiddleMan("https://bridge.acme.test"), Secret: "connection-token"), null);
        Source source = await scope.ServiceProvider.GetRequiredService<ISourceStore>().CreateAsync(new CreateSourceRequest(
            $"custom-plain-{Guid.NewGuid():N}"[..24], connection.Id, new CustomSourceScope("https://bridge.acme.test", "eng").ToJson()));
        CapturingFactory factory = new(new DeltaConnector(new SyncDelta(
            [new ConnectorFile("/a/a.md", 10, DateTime.UtcNow, "text/markdown")], [], "c1", false)));

        (await SyncAsync(source, factory, connection)).Error.Should().BeNull();

        factory.Secret.Should().Be("connection-token");
        (await RowAsync(source.Id, "/a/a.md")).ResourceUri.Should().BeNull("a connection without a provider checks nothing per user");
    }

    [Fact]
    public async Task Sync_TokenTheMiddleManRefuses_MarksTheConnectionUntilASyncSucceeds()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        IConnectionStore connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        Connection connection = await connections.CreateAsync(
            new CreateConnectionRequest($"c-{Guid.NewGuid():N}"[..20], ConnectionProvider.Custom,
                CustomConnectionConfig.ForMiddleMan("https://bridge.acme.test"), Secret: "rotated-away"), null);
        Source source = await scope.ServiceProvider.GetRequiredService<ISourceStore>().CreateAsync(new CreateSourceRequest(
            $"custom-refused-{Guid.NewGuid():N}"[..24], connection.Id, new CustomSourceScope("https://bridge.acme.test", "eng").ToJson()));

        (await SyncAsync(source, new CapturingFactory(new RefusingConnector()), connection)).Error.Should().NotBeNull();

        Connection refused = (await connections.GetAsync(connection.Id))!;
        refused.Health.Should().NotBeNull();
        refused.Health!.CredentialRejected.Should().BeTrue();
        refused.Health.Origin.Should().Be("sync");
        refused.Health.Message.Should().Contain("Replace it on this connection");

        CapturingFactory working = new(new DeltaConnector(new SyncDelta([], [], "c1", false)));
        (await SyncAsync(source, working, refused)).Error.Should().BeNull();

        Connection recovered = (await connections.GetAsync(connection.Id))!;
        recovered.Health!.CredentialRejected.Should().BeFalse();
        recovered.Health.Succeeded.Should().BeTrue();
    }

    private sealed class RefusingConnector : ISyncCursorConnector
    {
        public ConnectorType Type => ConnectorType.Custom;
        public bool SupportsLiveWatch => false;
        public Task<SyncDelta> GetChangesAsync(string? cursor, CancellationToken ct = default) =>
            throw new Connapse.Storage.Connectors.Custom.CustomSourceUnauthorizedException("The source server refused the token (401).");
        public string ResolveJobPath(string relativePath) => relativePath;
        public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConnectorFile>>([]);
        public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream());
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(false);
        public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private async Task<(string? ResourceUri, string[]? AccessKeys)> RowAsync(Guid sourceId, string path)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        await using KnowledgeDbContext context = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var row = await context.Documents.Where(d => d.SourceId == sourceId && d.Path == path)
            .Select(d => new { d.ResourceUri, d.AccessKeys }).SingleAsync();
        return (row.ResourceUri, row.AccessKeys);
    }

    private async Task MarkReadyAsync(Guid sourceId)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        await using KnowledgeDbContext context = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE documents SET ingestion_status = 'Ready', metadata = jsonb_build_object('RemoteLastModified', '2026-10-13T00:00:00.0000000Z', 'RemoteSize', '10') WHERE source_id = {0}",
            sourceId);
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

    private async Task<SourceSyncResult> SyncAsync(
        Source source, IConnectorFactory factory, Connection? connection = null, RecordingIngestionQueue? queue = null)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        SourceSyncService service = new(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            factory,
            queue ?? new RecordingIngestionQueue(),
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<SourceSyncService>());
        return await service.SyncSourceAsync(source, connection, CancellationToken.None);
    }

    private sealed class CapturingFactory(IConnector connector) : IConnectorFactory
    {
        public string? Secret { get; private set; }

        public IConnector Create(Source source, Connection connection, string? secret = null)
        {
            Secret = secret;
            return connector;
        }

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
