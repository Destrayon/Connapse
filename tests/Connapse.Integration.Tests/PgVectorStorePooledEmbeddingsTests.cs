using Connapse.Core;
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
            var result = await vectorStore.GetPooledDocumentEmbeddingsAsync(containerId, maxDocuments: 100, CancellationToken.None);

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
            var result = await vectorStore.GetPooledDocumentEmbeddingsAsync(containerId, maxDocuments: 100, CancellationToken.None);

            result.Should().HaveCount(2);
            result.Select(r => r.DocumentId).Should().BeEquivalentTo(new[] { docA1, docA2 });
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task GetPooledDocumentEmbeddingsAsync_OneModelTwoSizes_PoolsOnlyTheDominantSize()
    {
        // The same model id at two sizes (its dimensions were reconfigured): vectors of different
        // sizes can't be averaged together, so only the dominant size is pooled.
        Guid containerId = await CreateContainerAsync("pool-sizes");
        Guid docA1 = await SeedDocumentAsync(containerId, "/a1.txt");
        Guid docA2 = await SeedDocumentAsync(containerId, "/a2.txt");
        Guid docB = await SeedDocumentAsync(containerId, "/b.txt");

        await SeedChunkVectorAsync(docA1, containerId, "model-A", new float[] { 1f, 0f, 0f });
        await SeedChunkVectorAsync(docA2, containerId, "model-A", new float[] { 0f, 1f, 0f });
        await SeedChunkVectorAsync(docB, containerId, "model-A", new float[] { 1f, 0f });
        await SeedChunkVectorAsync(docB, containerId, "model-A", new float[] { 0f, 1f, 0f });

        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var result = await vectorStore.GetPooledDocumentEmbeddingsAsync(containerId, maxDocuments: 100, CancellationToken.None);

            result.Select(r => r.DocumentId).Should().BeEquivalentTo(new[] { docA1, docA2, docB });
            result.Should().OnlyContain(r => r.Embedding.Length == 3);
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
            var result = await vectorStore.GetPooledDocumentEmbeddingsAsync(containerId, maxDocuments: 100, CancellationToken.None);

            result.Should().BeEmpty();
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task GetPooledDocumentEmbeddingsAsync_MoreDocsThanMax_ReturnsTheSameBoundedSampleEachTime()
    {
        Guid containerId = await CreateContainerAsync("pool-sample");
        var docs = new List<Guid>();
        for (int i = 0; i < 12; i++)
        {
            Guid doc = await SeedDocumentAsync(containerId, $"/s{i}.txt");
            await SeedChunkVectorAsync(doc, containerId, "model-A", new float[] { 1f, i });
            docs.Add(doc);
        }

        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var first = await vectorStore.GetPooledDocumentEmbeddingsAsync(containerId, maxDocuments: 5, CancellationToken.None);
            var second = await vectorStore.GetPooledDocumentEmbeddingsAsync(containerId, maxDocuments: 5, CancellationToken.None);

            first.Should().HaveCount(5);
            first.Select(r => r.DocumentId).Should().OnlyContain(id => docs.Contains(id));
            second.Select(r => r.DocumentId).Should().BeEquivalentTo(first.Select(r => r.DocumentId));
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task GetDocumentSetFingerprintAsync_FolderOverTenThousandDocs_CountsAndHashesEveryDocument()
    {
        // CON-176: the rollup once read only the newest 10,000 documents. The fingerprint must see them all.
        const int documentCount = 10_050;
        Guid containerId = await CreateContainerAsync("fp-large");
        var ids = new List<Guid>(documentCount);
        await using (var seedScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var factoryDb = seedScope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
            await using var ctx = await factoryDb.CreateDbContextAsync();
            for (int i = 0; i < documentCount; i++)
            {
                Guid id = Guid.NewGuid();
                ids.Add(id);
                ctx.Documents.Add(NewDocument(id, containerId, $"/bulk-{i}.txt", contentHash: $"h{i}"));
            }
            await ctx.SaveChangesAsync();
        }

        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var docStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            DocumentSetFingerprint fingerprint = await docStore.GetDocumentSetFingerprintAsync(containerId, CancellationToken.None);

            fingerprint.DocumentCount.Should().Be(documentCount);

            // The hash covers the oldest documents too: change one and the hash changes.
            await using (var editScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var factoryDb = editScope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
                await using var ctx = await factoryDb.CreateDbContextAsync();
                await ctx.Documents.Where(d => d.Id == ids[0])
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.ContentHash, "changed"));
            }
            DocumentSetFingerprint after = await docStore.GetDocumentSetFingerprintAsync(containerId, CancellationToken.None);
            after.DocumentCount.Should().Be(documentCount);
            after.Hash.Should().NotBe(fingerprint.Hash);
        }
        finally
        {
            // Container delete refuses a container that still has documents, and ten thousand
            // leftover rows would weigh on every later test sharing this database.
            await using (var cleanupScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var factoryDb = cleanupScope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
                await using var ctx = await factoryDb.CreateDbContextAsync();
                await ctx.Documents.Where(d => d.ContainerId == containerId).ExecuteDeleteAsync();
            }
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task ListSummarizedSampleAsync_MoreSummarizedThanMax_ReturnsSameBoundedSampleOfSummarizedDocs()
    {
        // CON-176: summary clustering samples summarized documents from the whole folder.
        Guid containerId = await CreateContainerAsync("sum-sample");
        await using (var seedScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var factoryDb = seedScope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
            await using var ctx = await factoryDb.CreateDbContextAsync();
            for (int i = 0; i < 12; i++)
            {
                DocumentEntity doc = NewDocument(Guid.NewGuid(), containerId, $"/s-{i}.txt", contentHash: $"h{i}");
                // Two documents have no summary and one has an empty one; none may be sampled or counted.
                doc.Summary = i switch { 0 or 1 => null, 2 => "", _ => $"summary {i}" };
                ctx.Documents.Add(doc);
            }
            await ctx.SaveChangesAsync();
        }

        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var docStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

            DocumentSetFingerprint fingerprint =
                await docStore.GetSummarizedDocumentSetFingerprintAsync(containerId, CancellationToken.None);
            fingerprint.DocumentCount.Should().Be(9);

            IReadOnlyList<Document> first = await docStore.ListSummarizedSampleAsync(containerId, 5, CancellationToken.None);
            IReadOnlyList<Document> second = await docStore.ListSummarizedSampleAsync(containerId, 5, CancellationToken.None);

            first.Should().HaveCount(5);
            first.Should().OnlyContain(d => !string.IsNullOrEmpty(d.Summary));
            second.Select(d => d.Id).Should().BeEquivalentTo(first.Select(d => d.Id));
        }
        finally
        {
            await using (var cleanupScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var factoryDb = cleanupScope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
                await using var ctx = await factoryDb.CreateDbContextAsync();
                await ctx.Documents.Where(d => d.ContainerId == containerId).ExecuteDeleteAsync();
            }
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static DocumentEntity NewDocument(Guid docId, Guid containerId, string path, string contentHash) => new()
    {
        Id = docId,
        ContainerId = containerId,
        FileName = path.TrimStart('/'),
        ContentType = "text/plain",
        Path = path,
        ContentHash = contentHash,
        SizeBytes = 1,
        ChunkCount = 0,
        Generation = 1,
        IngestionStatus = DocumentStatus.Queued,
        CreatedAt = DateTime.UtcNow,
        Metadata = new Dictionary<string, string>(),
    };

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
            IngestionStatus = DocumentStatus.Queued,
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
