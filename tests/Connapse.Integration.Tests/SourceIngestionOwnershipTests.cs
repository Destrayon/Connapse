using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Reindex;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Pins the two ownership bugs found while planning PR 3b. Before #351, IngestionPipeline
/// set documents.container_id unconditionally — so ingesting into a source would have
/// violated the ck_documents_single_owner CHECK — and PgVectorStore defaulted a missing
/// owner to Guid.Empty, which writes a vector no owner-scoped query can ever match.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SourceIngestionOwnershipTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string p) => $"{p}-{Guid.NewGuid():N}"[..20];

    private static async Task<Guid> SeedSourceAsync(IServiceProvider sp)
    {
        var connections = sp.GetRequiredService<IConnectionStore>();
        var sources = sp.GetRequiredService<ISourceStore>();

        var connection = await connections.CreateAsync(
            new CreateConnectionRequest(ShortName("c"), ConnectionProvider.S3, """{"region":"us-east-1"}"""),
            createdByUserId: null);

        var source = await sources.CreateAsync(
            new CreateSourceRequest(ShortName("s"), connection.Id, """{"bucketName":"b"}"""));

        return source.Id;
    }

    [Fact]
    public async Task Ingest_ForSourceOwner_WritesSourceIdNotContainerId()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid sourceId = await SeedSourceAsync(scope.ServiceProvider);

        var pipeline = scope.ServiceProvider.GetRequiredService<IKnowledgeIngester>();
        using var content = new MemoryStream("owner test content for the source"u8.ToArray());

        await pipeline.IngestAsync(content, new IngestionOptions(
            FileName: "owned.md",
            ContentType: "text/markdown",
            Path: "/owned.md")
        {
            Owner = OwnerRef.ForSource(sourceId)
        }, CancellationToken.None);

        await using var ctx = await factory.CreateDbContextAsync();
        var doc = await ctx.Documents.AsNoTracking().SingleAsync(d => d.SourceId == sourceId);

        doc.ContainerId.Should().BeNull("the XOR check forbids both owners being set");
        doc.SourceId.Should().Be(sourceId);
        doc.OwnerId.Should().Be(sourceId);
    }

    [Fact]
    public async Task Ingest_ForSourceOwner_WritesChunksWithSourceOwnerNotEmpty()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid sourceId = await SeedSourceAsync(scope.ServiceProvider);

        var pipeline = scope.ServiceProvider.GetRequiredService<IKnowledgeIngester>();
        using var content = new MemoryStream("chunk owner content for the source document"u8.ToArray());

        await pipeline.IngestAsync(content, new IngestionOptions(
            FileName: "chunks.md",
            ContentType: "text/markdown",
            Path: "/chunks.md")
        {
            Owner = OwnerRef.ForSource(sourceId)
        }, CancellationToken.None);

        await using var ctx = await factory.CreateDbContextAsync();

        (await ctx.Chunks.AsNoTracking().AnyAsync(c => c.OwnerId == sourceId))
            .Should().BeTrue("chunks must carry the source as their owner");
        (await ctx.Chunks.AsNoTracking().AnyAsync(c => c.OwnerId == Guid.Empty))
            .Should().BeFalse("a zero owner is unreachable by every owner-scoped query");
    }

    [Fact]
    public async Task Ingest_WithNoOwnerAndNoContainerId_Throws()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IKnowledgeIngester>();
        using var content = new MemoryStream("no owner"u8.ToArray());

        // Failing loudly beats writing a document nobody owns.
        Func<Task> act = async () => await pipeline.IngestAsync(
            content, new IngestionOptions(FileName: "orphan.md", Path: "/orphan.md"), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Reindex_AttemptingToMoveSourceDocumentToContainer_IsRejected()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid sourceId = await SeedSourceAsync(scope.ServiceProvider);
        var pipeline = scope.ServiceProvider.GetRequiredService<IKnowledgeIngester>();

        using var first = new MemoryStream("original source-owned content"u8.ToArray());
        var created = await pipeline.IngestAsync(first, new IngestionOptions(
            FileName: "move.md", ContentType: "text/markdown", Path: "/move.md")
        {
            Owner = OwnerRef.ForSource(sourceId)
        }, CancellationToken.None);

        // Re-ingesting the same document under a container owner would clear source_id and
        // set container_id. The CHECK still passes — exactly one column is set — so nothing
        // at the database level catches it, and the content silently crosses an
        // authorization boundary.
        using var second = new MemoryStream("reindexed content"u8.ToArray());
        Func<Task> act = async () => await pipeline.IngestAsync(second, new IngestionOptions(
            DocumentId: created.DocumentId, FileName: "move.md", Path: "/move.md")
        {
            Owner = OwnerRef.ForContainer(Guid.NewGuid())
        }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var ctx = await factory.CreateDbContextAsync();
        var doc = await ctx.Documents.AsNoTracking().SingleAsync(d => d.Id == Guid.Parse(created.DocumentId));
        doc.SourceId.Should().Be(sourceId, "a rejected reindex must not move the document");
        doc.ContainerId.Should().BeNull();
    }

    [Fact]
    public async Task Reindex_WithMatchingOwner_Succeeds()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid sourceId = await SeedSourceAsync(scope.ServiceProvider);
        var pipeline = scope.ServiceProvider.GetRequiredService<IKnowledgeIngester>();

        using var first = new MemoryStream("first revision of the document"u8.ToArray());
        var created = await pipeline.IngestAsync(first, new IngestionOptions(
            FileName: "same.md", ContentType: "text/markdown", Path: "/same.md")
        {
            Owner = OwnerRef.ForSource(sourceId)
        }, CancellationToken.None);

        // Same owner is the normal re-sync case and must keep working.
        using var second = new MemoryStream("second revision of the document"u8.ToArray());
        await pipeline.IngestAsync(second, new IngestionOptions(
            DocumentId: created.DocumentId, FileName: "same.md", Path: "/same.md")
        {
            Owner = OwnerRef.ForSource(sourceId)
        }, CancellationToken.None);

        await using var ctx = await factory.CreateDbContextAsync();
        var doc = await ctx.Documents.AsNoTracking().SingleAsync(d => d.Id == Guid.Parse(created.DocumentId));
        doc.SourceId.Should().Be(sourceId);
        doc.ContainerId.Should().BeNull();
    }

    [Fact]
    public async Task Reindex_WithoutRemoteSignature_PreservesTheOneAlreadyStored()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid sourceId = await SeedSourceAsync(scope.ServiceProvider);
        var pipeline = scope.ServiceProvider.GetRequiredService<IKnowledgeIngester>();

        // Ingested by the sync service, which records what the remote looked like.
        using var first = new MemoryStream("synced content"u8.ToArray());
        var created = await pipeline.IngestAsync(first, new IngestionOptions(
            FileName: "sig.md", ContentType: "text/markdown", Path: "/sig.md",
            Metadata: new Dictionary<string, string>
            {
                ["RemoteLastModified"] = "2026-08-16T00:00:00.0000000Z",
                ["RemoteSize"] = "1234",
            })
        {
            Owner = OwnerRef.ForSource(sourceId)
        }, CancellationToken.None);

        // A reindex knows nothing about the remote and carries no signature. The pipeline
        // replaces metadata wholesale, so without carry-forward the baseline is erased — and
        // the next sync would treat every file as changed and re-embed the whole source.
        using var second = new MemoryStream("reindexed content"u8.ToArray());
        await pipeline.IngestAsync(second, new IngestionOptions(
            DocumentId: created.DocumentId, FileName: "sig.md", Path: "/sig.md")
        {
            Owner = OwnerRef.ForSource(sourceId)
        }, CancellationToken.None);

        await using var ctx = await factory.CreateDbContextAsync();
        var doc = await ctx.Documents.AsNoTracking().SingleAsync(d => d.Id == Guid.Parse(created.DocumentId));

        doc.Metadata!["RemoteLastModified"].Should().Be("2026-08-16T00:00:00.0000000Z");
        doc.Metadata["RemoteSize"].Should().Be("1234");
    }

    [Fact]
    public async Task ForcedReindex_OfSourceOwnedDocument_EnqueuesItAsSourceOwned()
    {
        // The reindex deletes a document's chunks before enqueueing it. If the job it then
        // enqueues cannot be routed, the chunks are gone for good: the pipeline throws, and
        // the next sync sees an unchanged remote signature and does not re-ingest the file.
        // So what the job carries is the whole safety property here.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid sourceId = await SeedSourceAsync(scope.ServiceProvider);

        var pipeline = scope.ServiceProvider.GetRequiredService<IKnowledgeIngester>();
        using var content = new MemoryStream("content to be force-reindexed"u8.ToArray());
        var created = await pipeline.IngestAsync(content, new IngestionOptions(
            FileName: "reindexed.md", ContentType: "text/markdown", Path: "/reindexed.md")
        {
            Owner = OwnerRef.ForSource(sourceId)
        }, CancellationToken.None);

        var queue = new RecordingIngestionQueue();
        await using var ctx = await factory.CreateDbContextAsync();
        var reindex = new ReindexService(
            ctx,
            scope.ServiceProvider.GetRequiredService<IKnowledgeFileSystem>(),
            scope.ServiceProvider.GetRequiredService<IManagedStorageProvider>(),
            scope.ServiceProvider.GetRequiredService<IContainerStore>(),
            queue,
            scope.ServiceProvider.GetRequiredService<IOptionsMonitor<ChunkingSettings>>(),
            scope.ServiceProvider.GetRequiredService<IOptionsMonitor<EmbeddingSettings>>(),
            NullLogger<ReindexService>.Instance);

        await reindex.ReindexAsync(
            new ReindexOptions { Force = true, DocumentIds = [created.DocumentId] },
            CancellationToken.None);

        IngestionJob job = queue.Jobs.Should().ContainSingle().Subject;
        job.Options.Owner.Should().Be(OwnerRef.ForSource(sourceId),
            "the pipeline routes source documents by Owner, and nothing else on the job says so");
        job.Options.ContainerId.Should().BeNull(
            "Nullable<Guid>.ToString() yields \"\", which reads as a container id that is merely blank");
    }

    [Fact]
    public async Task ForcedReindex_WhenTheEnqueueFails_LeavesTheDocumentSearchable()
    {
        // The reindex used to delete a document's chunks up front. Everything that could go
        // wrong afterwards — Hangfire down, the SFTP server refusing the connection — then left
        // a document that looked indexed and matched nothing, with no job coming to rebuild it.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid sourceId = await SeedSourceAsync(scope.ServiceProvider);

        var pipeline = scope.ServiceProvider.GetRequiredService<IKnowledgeIngester>();
        using var content = new MemoryStream("content that must survive a failed reindex"u8.ToArray());
        var created = await pipeline.IngestAsync(content, new IngestionOptions(
            FileName: "survives.md", ContentType: "text/markdown", Path: "/survives.md")
        {
            Owner = OwnerRef.ForSource(sourceId)
        }, CancellationToken.None);

        var documentId = Guid.Parse(created.DocumentId);
        await using var ctx = await factory.CreateDbContextAsync();
        int chunksBefore = await ctx.Chunks.CountAsync(c => c.DocumentId == documentId);
        chunksBefore.Should().BeGreaterThan(0, "the test is meaningless without an index to lose");

        var reindex = new ReindexService(
            ctx,
            scope.ServiceProvider.GetRequiredService<IKnowledgeFileSystem>(),
            scope.ServiceProvider.GetRequiredService<IManagedStorageProvider>(),
            scope.ServiceProvider.GetRequiredService<IContainerStore>(),
            new ThrowingIngestionQueue(),
            scope.ServiceProvider.GetRequiredService<IOptionsMonitor<ChunkingSettings>>(),
            scope.ServiceProvider.GetRequiredService<IOptionsMonitor<EmbeddingSettings>>(),
            NullLogger<ReindexService>.Instance);

        var result = await reindex.ReindexAsync(
            new ReindexOptions { Force = true, DocumentIds = [created.DocumentId] },
            CancellationToken.None);

        result.FailedCount.Should().Be(1, "the enqueue threw and that is not a success");

        await using var after = await factory.CreateDbContextAsync();
        (await after.Chunks.CountAsync(c => c.DocumentId == documentId))
            .Should().Be(chunksBefore, "a reindex that never started must not cost the old index");

        var doc = await after.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId);
        doc.Status.Should().NotBe("Pending",
            "Pending means a job is coming; the sync engine skips those, so it would strand the document");
    }

    [Fact]
    public async Task IngestionFailure_LeavesAStatusTheSyncEngineWillLookAtAgain()
    {
        // A reindex sets Status to "Pending" and a source job then fails before the pipeline
        // loads the row — the SFTP server refused the connection, say. Only ingestion_state was
        // written, so Status stayed "Pending", and HasRemoteChanged skips Pending documents on
        // the assumption a job is still coming. Nothing was, and the document stopped updating
        // even when its remote did.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid sourceId = await SeedSourceAsync(scope.ServiceProvider);

        var pipeline = scope.ServiceProvider.GetRequiredService<IKnowledgeIngester>();
        using var content = new MemoryStream("a document whose next sync fails"u8.ToArray());
        var created = await pipeline.IngestAsync(content, new IngestionOptions(
            FileName: "stranded.md", ContentType: "text/markdown", Path: "/stranded.md")
        {
            Owner = OwnerRef.ForSource(sourceId)
        }, CancellationToken.None);

        var documentId = Guid.Parse(created.DocumentId);
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        await using (var pending = await factory.CreateDbContextAsync())
        {
            var row = await pending.Documents.SingleAsync(d => d.Id == documentId);
            row.Status = "Pending";
            await pending.SaveChangesAsync();
        }

        await store.MarkIngestionFailedAsync(created.DocumentId, "the server refused the connection");

        await using var after = await factory.CreateDbContextAsync();
        var doc = await after.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId);

        doc.Status.Should().Be("Failed",
            "the sync engine reads Status, and skips anything still marked Pending");
        doc.IngestionState.Should().Be(IngestionState.Failed);
        doc.ErrorMessage.Should().Contain("refused");
    }

    /// <summary>A queue that is down, which is the failure the test above is about.</summary>
    private sealed class ThrowingIngestionQueue : RecordingIngestionQueue
    {
        public override Task EnqueueAsync(IngestionJob job, CancellationToken ct = default) =>
            throw new InvalidOperationException("the queue is unavailable");
    }

}
