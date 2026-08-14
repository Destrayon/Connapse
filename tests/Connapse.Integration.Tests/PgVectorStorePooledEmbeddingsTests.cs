using System.Net.Http.Json;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for <see cref="IVectorStore.GetPooledDocumentEmbeddingsAsync"/>.
/// Exercises the pgvector AVG aggregation path used by the document-clustering
/// container summary method.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class PgVectorStorePooledEmbeddingsTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task GetPooledDocumentEmbeddingsAsync_OneModelTwoDocs_PoolsAndNormalizes()
    {
        // Arrange — create a container, seed 2 docs each with 2 chunk vectors under one model.
        Guid containerId = await CreateContainerAsync("pool-basic");
        Guid doc1 = await SeedDocumentAsync(containerId, "/doc1.txt");
        Guid doc2 = await SeedDocumentAsync(containerId, "/doc2.txt");
        const string modelId = "pool-test-model";

        await SeedChunkVectorAsync(doc1, containerId, modelId, new float[] { 1f, 0f, 0f });
        await SeedChunkVectorAsync(doc1, containerId, modelId, new float[] { 0f, 1f, 0f });
        await SeedChunkVectorAsync(doc2, containerId, modelId, new float[] { 0f, 0f, 1f });
        await SeedChunkVectorAsync(doc2, containerId, modelId, new float[] { 0f, 0f, 1f });

        try
        {
            // Act
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var result = await vectorStore.GetPooledDocumentEmbeddingsAsync(containerId, CancellationToken.None);

            // Assert
            result.Should().HaveCount(2);

            // doc1: avg((1,0,0), (0,1,0)) = (0.5, 0.5, 0) → L2-normalized → (~0.707, ~0.707, 0)
            var doc1Pooled = result.Single(r => r.DocumentId == doc1).Embedding;
            doc1Pooled[0].Should().BeApproximately(0.7071f, 0.001f);
            doc1Pooled[1].Should().BeApproximately(0.7071f, 0.001f);
            doc1Pooled[2].Should().BeApproximately(0f, 0.001f);

            // doc2: avg((0,0,1), (0,0,1)) = (0,0,1) → L2-normalized → (0,0,1)
            var doc2Pooled = result.Single(r => r.DocumentId == doc2).Embedding;
            doc2Pooled[2].Should().BeApproximately(1f, 0.001f);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task GetPooledDocumentEmbeddingsAsync_MixedModels_UsesDominant()
    {
        Guid containerId = await CreateContainerAsync("pool-mixed");
        Guid docA1 = await SeedDocumentAsync(containerId, "/a1.txt");
        Guid docA2 = await SeedDocumentAsync(containerId, "/a2.txt");
        Guid docB = await SeedDocumentAsync(containerId, "/b.txt");

        // Dominant model: model-A with 2 vectors
        await SeedChunkVectorAsync(docA1, containerId, "model-A", new float[] { 1f, 0f });
        await SeedChunkVectorAsync(docA2, containerId, "model-A", new float[] { 0f, 1f });
        // Non-dominant: model-B with 1 vector (different dimensionality, but it gets filtered out)
        await SeedChunkVectorAsync(docB, containerId, "model-B", new float[] { 1f, 0f, 0f });

        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var result = await vectorStore.GetPooledDocumentEmbeddingsAsync(containerId, CancellationToken.None);

            result.Should().HaveCount(2);
            result.Select(r => r.DocumentId).Should().BeEquivalentTo(new[] { docA1, docA2 });
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task GetPooledDocumentEmbeddingsAsync_NoVectors_ReturnsEmpty()
    {
        Guid containerId = await CreateContainerAsync("pool-empty");
        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var result = await vectorStore.GetPooledDocumentEmbeddingsAsync(containerId, CancellationToken.None);

            result.Should().BeEmpty();
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<Guid> CreateContainerAsync(string nameSuffix)
    {
        // Use unique name to avoid collisions across reruns in the same fixture lifetime.
        string name = $"{nameSuffix}-{Guid.NewGuid():N}".Substring(0, Math.Min(63, nameSuffix.Length + 10));
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = name });
        response.EnsureSuccessStatusCode();
        var container = await response.Content.ReadFromJsonAsync<ContainerDto>();
        return Guid.Parse(container!.Id);
    }

    private async Task<Guid> SeedDocumentAsync(Guid containerId, string path)
    {
        Guid docId = Guid.NewGuid();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factoryDb = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var ctx = await factoryDb.CreateDbContextAsync();
        ctx.Documents.Add(new DocumentEntity
        {
            Id = docId,
            ContainerId = containerId,
            FileName = path.TrimStart('/'),
            ContentType = "text/plain",
            Path = path,
            ContentHash = string.Empty,
            SizeBytes = 1,
            ChunkCount = 0,
            Generation = 1,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>(),
        });
        await ctx.SaveChangesAsync();
        return docId;
    }

    private async Task SeedChunkVectorAsync(Guid documentId, Guid containerId, string modelId, float[] embedding)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factoryDb = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var ctx = await factoryDb.CreateDbContextAsync();

        Guid chunkId = Guid.NewGuid();
        // Chunks have an FK from chunk_vectors → chunks. Seed the chunk row first.
        ctx.Chunks.Add(new ChunkEntity
        {
            Id = chunkId,
            DocumentId = documentId,
            OwnerId = containerId,
            ChunkIndex = 0,
            Content = "test",
            TokenCount = 1,
            StartOffset = 0,
            EndOffset = 4,
        });
        ctx.ChunkVectors.Add(new ChunkVectorEntity
        {
            ChunkId = chunkId,
            DocumentId = documentId,
            OwnerId = containerId,
            Embedding = new Vector(embedding),
            ModelId = modelId,
            ContentHash = $"hash-{chunkId:N}",
            Dimensions = embedding.Length,
        });
        await ctx.SaveChangesAsync();
    }

    private record ContainerDto(string Id, string Name);
}
