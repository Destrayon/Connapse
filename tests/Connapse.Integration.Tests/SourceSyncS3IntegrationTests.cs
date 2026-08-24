using Amazon.S3.Model;
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

/// <summary>
/// Sync against a real S3 remote, backed by LocalStack.
/// <para>
/// The other sync tests substitute <see cref="IConnectorFactory"/> for a fake, so nothing
/// there exercises the step where a connection's credentials and a source's scope are
/// recombined into a working connector. This resolves the real factory from DI, so a
/// mistake in that recombination — a dropped region, a mis-joined prefix — fails here
/// rather than in production.
/// </para>
/// <para>
/// Lives in the shared collection and owns its LocalStack container directly, rather than
/// taking it as a collection fixture: a second collection would get its own
/// <see cref="SharedWebAppFixture"/>, and with it a duplicate PostgreSQL and MinIO pair.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SourceSyncS3IntegrationTests(SharedWebAppFixture fixture) : IAsyncLifetime
{
    private readonly LocalStackFixture _localStack = new();
    private string _bucketName = null!;

    public async Task InitializeAsync()
    {
        await _localStack.InitializeAsync();
        _bucketName = $"connapse-sync-{Guid.NewGuid():N}"[..32];
        await _localStack.CreateBucketAsync(_bucketName);
    }

    public async Task DisposeAsync() => await _localStack.DisposeAsync();

    private async Task SeedObjectAsync(string key, string content) =>
        await _localStack.S3Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            ContentBody = content,
            ContentType = "text/markdown"
        });

    /// <summary>Creates the connection and source pair a real deployment would configure.</summary>
    private async Task<(Source Source, Connection Connection)> CreateSourceAsync(IServiceProvider sp)
    {
        var connections = sp.GetRequiredService<IConnectionStore>();
        var sources = sp.GetRequiredService<ISourceStore>();

        // The connection carries the credential and endpoint, the source carries the scope.
        var connection = await connections.CreateAsync(
            new CreateConnectionRequest(
                $"s3-{Guid.NewGuid():N}"[..20],
                ConnectionProvider.S3,
                $$"""{"region":"{{LocalStackFixture.Region}}"}"""),
            createdByUserId: null);

        var source = await sources.CreateAsync(
            new CreateSourceRequest(
                $"src-{Guid.NewGuid():N}"[..20],
                connection.Id,
                $$"""{"bucketName":"{{_bucketName}}"}"""));

        return (source, connection);
    }

    private SourceSyncService BuildService(IServiceProvider sp) => new(
        sp.GetRequiredService<IServiceScopeFactory>(),
        // The real factory, not a fake — this is what the test exists to cover.
        sp.GetRequiredService<IConnectorFactory>(),
        sp.GetRequiredService<IIngestionQueue>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<SourceSyncService>());

    [Fact]
    public async Task SyncSourceAsync_RealS3Bucket_EnqueuesEveryObject()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await CreateSourceAsync(scope.ServiceProvider);

        await SeedObjectAsync("alpha.md", "first");
        await SeedObjectAsync("nested/beta.md", "second");
        await SeedObjectAsync("nested/deeper/gamma.md", "third");

        var result = await BuildService(scope.ServiceProvider)
            .SyncSourceAsync(source, connection, CancellationToken.None);

        result.Error.Should().BeNull("the connector must be constructible from the connection and source alone");
        result.Upserted.Should().Be(3, "every object in the bucket is new to this source");
        result.UsedDeltaPath.Should().BeFalse("S3 has no delta API, so this is the list-and-diff path");

        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        (await sources.GetAsync(source.Id))!.LastSyncStatus.Should().Be(SyncStatus.Succeeded);
    }

    [Fact]
    public async Task SyncSourceAsync_ObjectDeletedFromRealBucket_RemovesItsDocument()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var (source, connection) = await CreateSourceAsync(scope.ServiceProvider);

        await SeedObjectAsync("kept.md", "still here");

        // A document indexed from an object that has since been removed from the bucket.
        // Seeded directly because the ingestion worker does not run in these tests.
        var strandedId = Guid.NewGuid();
        await using (var seed = await dbFactory.CreateDbContextAsync())
        {
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, created_at) VALUES ({0}, NULL, {1}, 'removed.md', '/removed.md', '', 1, now())",
                strandedId, source.Id);
        }

        var result = await BuildService(scope.ServiceProvider)
            .SyncSourceAsync(source, connection, CancellationToken.None);

        result.Deleted.Should().Be(1, "the object backing that document is no longer in the bucket");

        await using var after = await dbFactory.CreateDbContextAsync();
        (await after.Documents.AnyAsync(d => d.Id == strandedId)).Should().BeFalse();
    }

    [Fact]
    public async Task SyncSourceAsync_SecondCycleOverRealBucket_SkipsUnchangedObjects()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var (source, connection) = await CreateSourceAsync(scope.ServiceProvider);

        await SeedObjectAsync("stable.md", "unchanging");

        var service = BuildService(scope.ServiceProvider);
        (await service.SyncSourceAsync(source, connection, CancellationToken.None)).Upserted.Should().Be(1);

        // Stand in for the ingestion worker, recording the signature the job carried. S3's
        // LastModified has second precision, so this is also a check that the round trip
        // through the connector and back does not perturb the value enough to look changed.
        var listed = await scope.ServiceProvider.GetRequiredService<IConnectorFactory>()
            .Create(source, connection).ListFilesAsync(null, CancellationToken.None);
        var file = listed.Single();

        await using (var seed = await dbFactory.CreateDbContextAsync())
        {
            await seed.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, status, created_at, metadata)
                VALUES ({0}, NULL, {1}, 'stable.md', {2}, '', {3}, 'Ready', now(), {4}::jsonb)
                """,
                Guid.NewGuid(), source.Id, file.Path, file.SizeBytes,
                $$"""{"{{SourceSyncService.RemoteLastModifiedKey}}":"{{file.LastModified:O}}","{{SourceSyncService.RemoteSizeKey}}":"{{file.SizeBytes}}"}""");
        }

        var second = await service.SyncSourceAsync(source, connection, CancellationToken.None);

        second.Upserted.Should().Be(0, "an object untouched in S3 must not be re-embedded every cycle");
    }

    /// <summary>
    /// The seam nothing crossed. Sync tests assert what is <em>enqueued</em>; pipeline tests
    /// start from <c>IngestAsync</c>. Between them sat <c>IngestByIdAsync</c>, which resolved
    /// ownership only through a container — so every job a source enqueued failed, for every
    /// provider, and the Sources page showed files queued with the document count stuck at zero
    /// (#398).
    /// </summary>
    [Fact]
    public async Task IngestByIdAsync_SourceOwnedDocument_ReadsItThroughTheSourcesConnector()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, _) = await CreateSourceAsync(scope.ServiceProvider);

        await SeedObjectAsync("notes.md", "content that must reach the index");

        var pipeline = scope.ServiceProvider.GetRequiredService<IKnowledgeIngester>();
        var documentId = Guid.NewGuid();

        var result = await pipeline.IngestByIdAsync(
            documentId.ToString(),
            new IngestionOptions(
                DocumentId: documentId.ToString(),
                FileName: "notes.md",
                ContentType: "text/markdown",
                Path: "notes.md")
            {
                // No ContainerId, because a source does not have one. That is the whole case.
                Owner = OwnerRef.ForSource(source.Id),
            });

        result.ChunkCount.Should().BeGreaterThan(0, "the file must actually have been read and indexed");

        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
        document.Should().NotBeNull();
        document!.SourceId.Should().Be(source.Id);
        document.ContainerId.Should().BeNull("ownership is exclusive — a source document has no container");
    }
}

