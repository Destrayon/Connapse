using Connapse.Storage.Backfill;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The backfill is the one irreversible step in the connector/source split, so these
/// tests exist to prove it cannot shift what search returns. The argument is structural:
/// documents.owner_id is a generated COALESCE(container_id, source_id), and the migrated
/// source reuses the container's GUID, so moving a document between those columns leaves
/// owner_id — the column every search path filters on — byte-identical.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class BackfillSearchParityTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task Backfill_DoesNotChangeChunkOrVectorOwnership()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();

        Guid containerId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();
        Guid chunkId = Guid.NewGuid();

        await using (var seed = await factory.CreateDbContextAsync())
        {
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO containers (id, name, connector_type, connector_config, created_at, updated_at) VALUES ({0}, {1}, 3, CAST({2} AS jsonb), now(), now())",
                containerId, $"parity-{containerId:N}"[..20], """{"bucketName":"b","region":"us-east-1"}""");

            seed.Documents.Add(new DocumentEntity
            {
                Id = documentId,
                ContainerId = containerId,
                FileName = "p.md",
                Path = "/p.md",
                ContentHash = string.Empty,
                SizeBytes = 1,
                Status = "Ready",
                CreatedAt = DateTime.UtcNow,
                Metadata = new Dictionary<string, string>(),
            });
            seed.Chunks.Add(new ChunkEntity
            {
                Id = chunkId,
                DocumentId = documentId,
                OwnerId = containerId,
                Content = "parity probe content",
                ChunkIndex = 0,
                TokenCount = 3,
                StartOffset = 0,
                EndOffset = 20,
            });
            seed.ChunkVectors.Add(new ChunkVectorEntity
            {
                ChunkId = chunkId,
                DocumentId = documentId,
                OwnerId = containerId,
                Embedding = new Vector(new float[] { 1f, 0f, 0f }),
                ModelId = "parity-model",
                ContentHash = $"h-{chunkId:N}",
                Dimensions = 3,
            });
            await seed.SaveChangesAsync();
        }

        await backfill.RunAsync(CancellationToken.None);

        await using var after = await factory.CreateDbContextAsync();

        // The owner never moved. Chunks and vectors were not rewritten, so anything
        // filtering on owner_id — every search path — returns exactly what it did before.
        (await after.Chunks.AsNoTracking().Where(c => c.Id == chunkId).Select(c => c.OwnerId).SingleAsync())
            .Should().Be(containerId);
        (await after.ChunkVectors.AsNoTracking().Where(v => v.ChunkId == chunkId).Select(v => v.OwnerId).SingleAsync())
            .Should().Be(containerId);
        (await after.Documents.AsNoTracking().Where(d => d.Id == documentId).Select(d => d.OwnerId).SingleAsync())
            .Should().Be(containerId);

        // And the document is now source-owned rather than container-owned.
        var doc = await after.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId);
        doc.ContainerId.Should().BeNull();
        doc.SourceId.Should().Be(containerId);
    }

    [Fact]
    public async Task Backfill_EmbeddingSurvivesIntactAndQueryable()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();

        Guid containerId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();
        Guid chunkId = Guid.NewGuid();
        var embedding = new float[] { 0.25f, 0.5f, 0.75f };

        await using (var seed = await factory.CreateDbContextAsync())
        {
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO containers (id, name, connector_type, connector_config, created_at, updated_at) VALUES ({0}, {1}, 4, CAST({2} AS jsonb), now(), now())",
                containerId, $"vec-{containerId:N}"[..20], """{"storageAccountName":"a","containerName":"c"}""");

            seed.Documents.Add(new DocumentEntity
            {
                Id = documentId,
                ContainerId = containerId,
                FileName = "v.md",
                Path = "/v.md",
                ContentHash = string.Empty,
                SizeBytes = 1,
                Status = "Ready",
                CreatedAt = DateTime.UtcNow,
                Metadata = new Dictionary<string, string>(),
            });
            seed.Chunks.Add(new ChunkEntity
            {
                Id = chunkId,
                DocumentId = documentId,
                OwnerId = containerId,
                Content = "vector probe",
                ChunkIndex = 0,
                TokenCount = 2,
                StartOffset = 0,
                EndOffset = 12,
            });
            seed.ChunkVectors.Add(new ChunkVectorEntity
            {
                ChunkId = chunkId,
                DocumentId = documentId,
                OwnerId = containerId,
                Embedding = new Vector(embedding),
                ModelId = "vec-model",
                ContentHash = $"h-{chunkId:N}",
                Dimensions = 3,
            });
            await seed.SaveChangesAsync();
        }

        await backfill.RunAsync(CancellationToken.None);

        await using var after = await factory.CreateDbContextAsync();
        var stored = await after.ChunkVectors.AsNoTracking().SingleAsync(v => v.ChunkId == chunkId);

        stored.Embedding.ToArray().Should().BeEquivalentTo(embedding);
        stored.Dimensions.Should().Be(3);
        stored.ModelId.Should().Be("vec-model");
    }

    [Fact]
    public async Task Backfill_OwnerScopedChunkQueryReturnsIdenticalHits()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();

        Guid containerId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();

        await using (var seed = await factory.CreateDbContextAsync())
        {
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO containers (id, name, connector_type, connector_config, created_at, updated_at) VALUES ({0}, {1}, 3, CAST({2} AS jsonb), now(), now())",
                containerId, $"kw-{containerId:N}"[..20], """{"bucketName":"b","region":"us-east-1"}""");

            seed.Documents.Add(new DocumentEntity
            {
                Id = documentId,
                ContainerId = containerId,
                FileName = "k.md",
                Path = "/k.md",
                ContentHash = string.Empty,
                SizeBytes = 1,
                Status = "Ready",
                CreatedAt = DateTime.UtcNow,
                Metadata = new Dictionary<string, string>(),
            });
            seed.Chunks.Add(new ChunkEntity
            {
                Id = Guid.NewGuid(),
                DocumentId = documentId,
                OwnerId = containerId,
                Content = "zarquon distinctive keyword",
                ChunkIndex = 0,
                TokenCount = 3,
                StartOffset = 0,
                EndOffset = 27,
            });
            await seed.SaveChangesAsync();
        }

        // The keyword path filters chunks by owner_id, so query that directly before and
        // after. Going through the HTTP search endpoint would require an embedding
        // provider and would test the provider rather than the migration.
        async Task<int> HitCountAsync()
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.Chunks.AsNoTracking()
                .Where(c => c.OwnerId == containerId && c.Content.Contains("zarquon"))
                .CountAsync();
        }

        int before = await HitCountAsync();
        await backfill.RunAsync(CancellationToken.None);
        int afterCount = await HitCountAsync();

        before.Should().Be(1);
        afterCount.Should().Be(before);
    }
}
