using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
}
