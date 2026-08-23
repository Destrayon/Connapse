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

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class DeleteGuardIntegrationTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<Source> SeedSourceAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();

        var connection = await connections.CreateAsync(
            new CreateConnectionRequest(ShortName("conn"), ConnectionProvider.S3, """{"region":"us-east-1"}"""),
            createdByUserId: null);

        return await sources.CreateAsync(
            new CreateSourceRequest(ShortName("src"), connection.Id, """{"bucketName":"b"}"""));
    }

    /// <summary>
    /// Inserts <paramref name="count"/> source-owned documents directly via SQL, following the
    /// raw-insert pattern in OwnerBridgeSchemaTests: <c>IDocumentStore.StoreAsync</c> always
    /// populates <c>container_id</c> and so cannot express a source-owned row.
    /// </summary>
    private async Task SeedDocumentsAsync(Guid sourceId, int count)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await dbFactory.CreateDbContextAsync();

        for (int i = 0; i < count; i++)
        {
            string path = $"/doc-{i}.md";
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, created_at) "
                + "VALUES ({0}, NULL, {1}, {2}, {3}, '', 1, now())",
                Guid.NewGuid(), sourceId, $"doc-{i}.md", path);
        }
    }

    /// <summary>
    /// Hands the service a fixed connector, following <c>FixedConnectorFactory</c> in
    /// SourceSyncIntegrationTests, which exists for exactly this purpose.
    /// </summary>
    private sealed class FixedConnectorFactory(IConnector connector) : IConnectorFactory
    {
        public IConnector Create(Source source, Connection connection) => connector;
    }

    private async Task<SourceSyncResult> SyncWithConnectorAsync(
        Source source, IConnector connector, bool applyWithheldDeletions = false)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var connection = (await connections.GetAsync(source.ConnectionId))!;

        var service = new SourceSyncService(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            new FixedConnectorFactory(connector),
            scope.ServiceProvider.GetRequiredService<IIngestionQueue>(),
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<SourceSyncService>());

        return await service.SyncSourceAsync(
            source, connection, CancellationToken.None, applyWithheldDeletions);
    }

    /// <summary>
    /// A connector whose listing is empty, without erroring — the shape a narrowed bucket
    /// policy or an unmounted directory actually takes. Before the guard this deleted every
    /// document the source owned.
    /// </summary>
    private sealed class EmptyListingConnector : IConnector
    {
        public ConnectorType Type => ConnectorType.S3;
        public bool SupportsLiveWatch => false;
        public string ResolveJobPath(string relativePath) => "/" + relativePath.TrimStart('/');
        public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConnectorFile>>([]);
        public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(false);
        public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task Sync_ListingCollapsesToEmpty_WithholdsDeletionsAndKeepsDocuments()
    {
        var source = await SeedSourceAsync();
        await SeedDocumentsAsync(source.Id, count: 40);

        var result = await SyncWithConnectorAsync(source, new EmptyListingConnector());

        result.Deleted.Should().Be(0, "an empty listing is not evidence that 40 files were deleted");
        result.WithheldDeletions.Should().Be(40);

        using var scope = fixture.Factory.Services.CreateScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        (await documents.ListAsync(source.Id, take: int.MaxValue)).Should().HaveCount(40);

        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        (await sources.GetAsync(source.Id))!.WithheldDeletions.Should().Be(40);
    }

    [Fact]
    public async Task Sync_WithOverride_AppliesTheWithheldDeletions()
    {
        var source = await SeedSourceAsync();
        await SeedDocumentsAsync(source.Id, count: 40);

        await SyncWithConnectorAsync(source, new EmptyListingConnector());
        var result = await SyncWithConnectorAsync(source, new EmptyListingConnector(), applyWithheldDeletions: true);

        result.Deleted.Should().Be(40);
        result.WithheldDeletions.Should().Be(0);

        using var scope = fixture.Factory.Services.CreateScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        (await documents.ListAsync(source.Id, take: int.MaxValue)).Should().BeEmpty();

        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        (await sources.GetAsync(source.Id))!.WithheldDeletions
            .Should().BeNull("the pending decision is resolved, so the button must stop showing");
    }

    [Fact]
    public async Task Sync_SmallDeletionSet_AppliesWithoutWithholding()
    {
        // Five of five is below the floor: small sources must stay able to tidy themselves.
        var source = await SeedSourceAsync();
        await SeedDocumentsAsync(source.Id, count: 5);

        var result = await SyncWithConnectorAsync(source, new EmptyListingConnector());

        result.Deleted.Should().Be(5);
        result.WithheldDeletions.Should().Be(0);
    }

    [Fact]
    public async Task UpdateWithheldDeletionsAsync_RoundTripsTheCount()
    {
        var source = await SeedSourceAsync();

        using var scope = fixture.Factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();

        (await sources.GetAsync(source.Id))!.WithheldDeletions
            .Should().BeNull("a source with nothing pending must not claim a count of zero");

        await sources.UpdateWithheldDeletionsAsync(source.Id, 42);
        (await sources.GetAsync(source.Id))!.WithheldDeletions.Should().Be(42);

        // Clearing must return to null, not zero: the UI distinguishes "nothing pending"
        // from "a decision was made", and zero would leave the button showing forever.
        await sources.UpdateWithheldDeletionsAsync(source.Id, null);
        (await sources.GetAsync(source.Id))!.WithheldDeletions.Should().BeNull();
    }

    [Fact]
    public async Task GetSource_AfterWithholding_ReportsTheCount()
    {
        var source = await SeedSourceAsync();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
            await sources.UpdateWithheldDeletionsAsync(source.Id, 40);
        }

        var response = await fixture.AdminClient.GetAsync($"/api/sources/{source.Id}");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("withheldDeletions").GetInt32().Should().Be(40);
    }

    [Fact]
    public async Task GetSource_WithNothingWithheld_ReportsNull()
    {
        // Null rather than 0, because the page keys the approval button off "is there a
        // pending decision" — a zero would leave it showing forever.
        var source = await SeedSourceAsync();

        var response = await fixture.AdminClient.GetAsync($"/api/sources/{source.Id}");
        var body = await response.Content.ReadAsStringAsync();

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("withheldDeletions").ValueKind
            .Should().Be(System.Text.Json.JsonValueKind.Null);
    }
}
