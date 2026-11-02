using System.Net.Http.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search.Keyword;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pgvector;

namespace Connapse.Integration.Tests;

/// <summary>
/// Chunks tied at a keyword or vector cut-off keep the earliest paths (CON-158). Every test inserts its documents
/// in reverse path order, one at a time, so heap order is the opposite of path order: without a tie-breaker the
/// cut-off keeps the last paths.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SearchTieBreakTests(SharedWebAppFixture fixture)
{
    private const string Content = "tied keyword content";

    [Theory]
    [InlineData("Bm25")]
    [InlineData("TsRank")]
    public async Task KeywordSearchAsync_TiedScores_KeepsTheEarliestPaths(string ranker)
    {
        Guid container = await CreateContainerAsync();
        try
        {
            await SeedReversedAsync(container, count: 10, embedding: null);

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await using var context = await scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
            var settings = Substitute.For<IOptionsMonitor<SearchSettings>>();
            settings.CurrentValue.Returns(new SearchSettings { KeywordRanker = ranker });
            var service = new KeywordSearchService(context, NullLogger<KeywordSearchService>.Instance, settings);

            var hits = await service.SearchAsync("tied keyword",
                new SearchOptions { TopK = 3, ContainerId = container.ToString() }, SearchScopes.Unrestricted);

            hits.Select(h => h.Metadata["path"]).Should().Equal(Path(0), Path(1), Path(2));
        }
        finally
        {
            await CleanupAsync(container);
        }
    }

    [Fact]
    public async Task VectorSearchAsync_ExactQueryTiedDistances_KeepsTheEarliestPaths()
    {
        Guid container = await CreateContainerAsync();
        try
        {
            await SeedReversedAsync(container, count: 10, embedding: [1f, 0f, 0f]);

            // No model filter: the exact query.
            var hits = await VectorSearchAsync(container, topK: 3, modelId: null);

            hits.Select(h => h.Metadata["path"]).Should().Equal(Path(0), Path(1), Path(2));
        }
        finally
        {
            await CleanupAsync(container);
        }
    }

    [Fact]
    public async Task VectorSearchAsync_NeighboursQueryTiedDistances_KeepsTheEarliestPaths()
    {
        Guid container = await CreateContainerAsync();
        try
        {
            // More tied vectors than the neighbours query's inner cut of 100, so the earliest paths are only found if
            // that cut keeps every tie.
            await SeedReversedAsync(container, count: 120, embedding: [1f, 0f, 0f]);

            var hits = await VectorSearchAsync(container, topK: 3, modelId: ModelId);

            hits.Select(h => h.Metadata["path"]).Should().Equal(Path(0), Path(1), Path(2));
        }
        finally
        {
            await CleanupAsync(container);
        }
    }

    private const string ModelId = "tie-break-model";

    private static string Path(int i) => $"/doc-{i:D3}.txt";

    private async Task<IReadOnlyList<VectorSearchResult>> VectorSearchAsync(Guid container, int topK, string? modelId)
    {
        Dictionary<string, string> filters = new() { ["containerId"] = container.ToString() };
        if (modelId is not null)
            filters["modelId"] = modelId;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IVectorStore>()
            .SearchAsync([1f, 0f, 0f], topK, filters, SearchScopes.Unrestricted);
    }

    /// <summary>One document per path, each with one identical chunk, saved one at a time from the last path back.</summary>
    private async Task SeedReversedAsync(Guid container, int count, float[]? embedding)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await using var ctx = await scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
            Guid documentId = Guid.NewGuid();
            Guid chunkId = Guid.NewGuid();
            ctx.Documents.Add(new DocumentEntity
            {
                Id = documentId,
                ContainerId = container,
                FileName = Path(i).TrimStart('/'),
                ContentType = "text/plain",
                Path = Path(i),
                ContentHash = string.Empty,
                SizeBytes = 1,
                ChunkCount = 1,
                Generation = 1,
                IngestionStatus = DocumentStatus.Ready,
                CreatedAt = DateTime.UtcNow,
                Metadata = new Dictionary<string, string>(),
            });
            ctx.Chunks.Add(new ChunkEntity
            {
                Id = chunkId,
                DocumentId = documentId,
                OwnerId = container,
                ChunkIndex = 0,
                Content = Content,
                TokenCount = 3,
                StartOffset = 0,
                EndOffset = Content.Length,
            });
            if (embedding is not null)
            {
                ctx.ChunkVectors.Add(new ChunkVectorEntity
                {
                    ChunkId = chunkId,
                    DocumentId = documentId,
                    OwnerId = container,
                    Embedding = new Vector(embedding),
                    ModelId = ModelId,
                    ContentHash = $"hash-{chunkId:N}",
                    Dimensions = embedding.Length,
                });
            }
            await ctx.SaveChangesAsync();
        }
    }

    private async Task<Guid> CreateContainerAsync()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"ties-{Guid.NewGuid():N}"[..20] });
        response.EnsureSuccessStatusCode();
        return Guid.Parse((await response.Content.ReadFromJsonAsync<ContainerDto>())!.Id);
    }

    private async Task CleanupAsync(Guid containerId)
    {
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await using var db = await scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM documents WHERE container_id = {0}", containerId);
        }
        await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
    }

    private record ContainerDto(string Id, string Name);
}
