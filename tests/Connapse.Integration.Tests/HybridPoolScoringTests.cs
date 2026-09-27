using System.Net.Http.Json;
using Connapse.Core.Interfaces;
using Connapse.Search.Keyword;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace Connapse.Integration.Tests;

/// <summary>
/// The two "score these chunks" queries hybrid search uses to give every pooled candidate a score
/// on both sides (#521), run against real Postgres/pgvector.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class HybridPoolScoringTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task VectorScoreChunksAsync_NamedChunks_ReturnsCosineSimilarityForTheModelOnly()
    {
        Guid containerId = await CreateContainerAsync("pool-vec");
        Guid docId = await SeedDocumentAsync(containerId);
        Guid same = await SeedChunkAsync(docId, containerId, "alpha", "model-A", [1f, 0f]);
        Guid orthogonal = await SeedChunkAsync(docId, containerId, "beta", "model-A", [0f, 1f]);
        Guid otherModel = await SeedChunkAsync(docId, containerId, "gamma", "model-B", [1f, 0f]);
        Guid notAsked = await SeedChunkAsync(docId, containerId, "delta", "model-A", [1f, 0f]);

        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IVectorStore>();

            var scores = await store.ScoreChunksAsync(
                [1f, 0f],
                [same.ToString(), orthogonal.ToString(), otherModel.ToString(), "not-a-guid"],
                "model-A");

            scores.Should().HaveCount(2);
            scores[same.ToString()].Should().BeApproximately(1f, 0.001f);
            scores[orthogonal.ToString()].Should().BeApproximately(0f, 0.001f);
            scores.Should().NotContainKey(otherModel.ToString());
            scores.Should().NotContainKey(notAsked.ToString());
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task KeywordScoreChunksAsync_NamedChunks_RanksMatchesAndScoresNonMatchesZero()
    {
        Guid containerId = await CreateContainerAsync("pool-kw");
        Guid docId = await SeedDocumentAsync(containerId);
        Guid match = await SeedChunkAsync(docId, containerId, "the reranker service runs locally", "model-A", [1f, 0f]);
        Guid noMatch = await SeedChunkAsync(docId, containerId, "completely unrelated text", "model-A", [0f, 1f]);

        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await using var db = await scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
            var keyword = new KeywordSearchService(db, NullLogger<KeywordSearchService>.Instance);

            var scores = await keyword.ScoreChunksAsync("reranker", [match.ToString(), noMatch.ToString()]);

            scores.Should().HaveCount(2);
            scores[match.ToString()].Should().BeGreaterThan(0f);
            scores[noMatch.ToString()].Should().Be(0f);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task KeywordSearchAsync_NoChunkHasEveryTerm_ReturnsPartialMatchesByOverlap()
    {
        Guid containerId = await CreateContainerAsync("kw-any");
        Guid docId = await SeedDocumentAsync(containerId);
        Guid both = await SeedChunkAsync(docId, containerId, "calcium intake and bone density", "model-A", [1f, 0f]);
        Guid one = await SeedChunkAsync(docId, containerId, "calcium supplements", "model-A", [1f, 0f]);
        Guid repeated = await SeedChunkAsync(docId, containerId, "calcium calcium calcium calcium", "model-A", [1f, 0f]);
        Guid none = await SeedChunkAsync(docId, containerId, "completely unrelated text", "model-A", [1f, 0f]);

        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await using var db = await scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
            var keyword = new KeywordSearchService(db, NullLogger<KeywordSearchService>.Instance);
            const string query = "does high dietary calcium intake prevent hyperparathyroidism";

            var hits = await keyword.SearchAsync(query,
                new Connapse.Core.SearchOptions { TopK = 10, ContainerId = containerId.ToString() },
                Connapse.Core.SearchScopes.Unrestricted);
            var scores = await keyword.ScoreChunksAsync(query, [both.ToString(), none.ToString()]);

            // Two distinct terms outrank one term repeated: the ts_rank_cd failure under OR.
            hits.Select(h => h.ChunkId).Should().BeEquivalentTo([both.ToString(), one.ToString(), repeated.ToString()]);
            hits[0].ChunkId.Should().Be(both.ToString());
            scores[both.ToString()].Should().BeGreaterThan(0f);
            scores[none.ToString()].Should().Be(0f);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Theory]
    [InlineData("\"bone density\"", new[] { "phrase" })]
    [InlineData("\"bone density\" marrow", new[] { "phrase", "split" })]
    [InlineData("the who", new[] { "band" })]
    [InlineData("calcium -supplements", new[] { "phrase" })]
    [InlineData("calcium -\"bone density\"", new[] { "supplements" })]
    [InlineData("supplements or marrow", new[] { "split", "supplements" })]
    [InlineData("density -of", new[] { "phrase" })]          // a stop-word exclusion still excludes
    [InlineData("concert -\"the who\"", new string[0])]     // so does a stop-word phrase
    [InlineData("\"the who\"", new[] { "band" })]            // a stop-word phrase stays a phrase
    [InlineData("\"who the\"", new string[0])]
    public async Task KeywordSearchAsync_PhrasesExclusionsAndStopWords_MatchTheExpectedChunks(string query, string[] expected)
    {
        Guid containerId = await CreateContainerAsync("kw-syntax");
        Guid docId = await SeedDocumentAsync(containerId);
        var chunks = new Dictionary<string, Guid>
        {
            ["phrase"] = await SeedChunkAsync(docId, containerId, "calcium intake and bone density", "model-A", [1f, 0f]),
            ["split"] = await SeedChunkAsync(docId, containerId, "density of bone marrow", "model-A", [1f, 0f]),
            ["band"] = await SeedChunkAsync(docId, containerId, "a concert by the who", "model-A", [1f, 0f]),
            ["supplements"] = await SeedChunkAsync(docId, containerId, "calcium supplements", "model-A", [1f, 0f]),
        };

        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await using var db = await scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
            var keyword = new KeywordSearchService(db, NullLogger<KeywordSearchService>.Instance);

            var hits = await keyword.SearchAsync(query,
                new Connapse.Core.SearchOptions { TopK = 10, ContainerId = containerId.ToString() },
                Connapse.Core.SearchScopes.Unrestricted);

            hits.Select(h => h.ChunkId).Should().BeEquivalentTo(expected.Select(name => chunks[name].ToString()));
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task KeywordSearchAsync_StopWordOnlyQuery_RanksAboveZero()
    {
        Guid containerId = await CreateContainerAsync("kw-stop");
        Guid docId = await SeedDocumentAsync(containerId);
        Guid band = await SeedChunkAsync(docId, containerId, "a concert by the who", "model-A", [1f, 0f]);
        Guid article = await SeedChunkAsync(docId, containerId, "the concert", "model-A", [1f, 0f]);

        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await using var db = await scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
            var keyword = new KeywordSearchService(db, NullLogger<KeywordSearchService>.Instance);

            var hits = await keyword.SearchAsync("the who",
                new Connapse.Core.SearchOptions { TopK = 10, ContainerId = containerId.ToString() },
                Connapse.Core.SearchScopes.Unrestricted);
            var scores = await keyword.ScoreChunksAsync("the who", [band.ToString(), article.ToString()]);

            // Any-term: both match; the chunk with both words ranks first, and nothing scores zero.
            hits.Select(h => h.ChunkId).Should().Equal(band.ToString(), article.ToString());
            hits.Should().OnlyContain(h => h.Score > 0f);
            scores[band.ToString()].Should().BeGreaterThan(scores[article.ToString()]);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<Guid> CreateContainerAsync(string prefix)
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"{prefix}-{Guid.NewGuid():N}"[..20] });
        response.EnsureSuccessStatusCode();
        var container = await response.Content.ReadFromJsonAsync<ContainerDto>();
        return Guid.Parse(container!.Id);
    }

    private async Task<Guid> SeedDocumentAsync(Guid containerId)
    {
        Guid docId = Guid.NewGuid();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var ctx = await dbFactory.CreateDbContextAsync();
        ctx.Documents.Add(new DocumentEntity
        {
            Id = docId,
            ContainerId = containerId,
            FileName = "doc.txt",
            ContentType = "text/plain",
            Path = "/doc.txt",
            ContentHash = string.Empty,
            SizeBytes = 1,
            ChunkCount = 0,
            Generation = 1,
            Status = "Ready",
            CreatedAt = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>(),
        });
        await ctx.SaveChangesAsync();
        return docId;
    }

    private async Task<Guid> SeedChunkAsync(Guid documentId, Guid containerId, string content, string modelId, float[] embedding)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var ctx = await dbFactory.CreateDbContextAsync();

        Guid chunkId = Guid.NewGuid();
        ctx.Chunks.Add(new ChunkEntity
        {
            Id = chunkId,
            DocumentId = documentId,
            OwnerId = containerId,
            ChunkIndex = 0,
            Content = content,
            TokenCount = 1,
            StartOffset = 0,
            EndOffset = content.Length,
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
        return chunkId;
    }

    private record ContainerDto(string Id, string Name);
}
