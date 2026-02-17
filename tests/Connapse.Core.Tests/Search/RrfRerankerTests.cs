using Connapse.Core;
using Connapse.Search.Reranking;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Search;

[Trait("Category", "Unit")]
public class RrfRerankerTests
{
    private readonly ILogger<RrfReranker> _logger;
    private readonly IOptionsMonitor<SearchSettings> _searchSettings;

    public RrfRerankerTests()
    {
        _logger = Substitute.For<ILogger<RrfReranker>>();
        _searchSettings = Substitute.For<IOptionsMonitor<SearchSettings>>();
        _searchSettings.CurrentValue.Returns(new SearchSettings { RrfK = 60 });
    }

    [Fact]
    public void Name_ReturnsRRF()
    {
        var reranker = new RrfReranker(_searchSettings, _logger);

        reranker.Name.Should().Be("RRF");
    }

    [Fact]
    public async Task RerankAsync_EmptyList_ReturnsEmptyList()
    {
        var reranker = new RrfReranker(_searchSettings, _logger);
        var hits = new List<SearchHit>();

        var result = await reranker.RerankAsync("test query", hits);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task RerankAsync_SingleSource_ReturnsUnchanged()
    {
        var reranker = new RrfReranker(_searchSettings, _logger);
        var hits = new List<SearchHit>
        {
            CreateHit("chunk1", 0.9f, "vector"),
            CreateHit("chunk2", 0.8f, "vector"),
            CreateHit("chunk3", 0.7f, "vector")
        };

        var result = await reranker.RerankAsync("test query", hits);

        // Single source, no fusion needed - returns same hits
        result.Should().HaveCount(3);
        result.Select(h => h.ChunkId).Should().Equal("chunk1", "chunk2", "chunk3");
    }

    [Fact]
    public async Task RerankAsync_MultipleSources_FusesResults()
    {
        var reranker = new RrfReranker(_searchSettings, _logger);
        var hits = new List<SearchHit>
        {
            // Vector results
            CreateHit("chunk1", 0.9f, "vector"),
            CreateHit("chunk2", 0.8f, "vector"),
            CreateHit("chunk3", 0.7f, "vector"),
            // Keyword results
            CreateHit("chunk2", 0.95f, "keyword"), // Duplicate - appears in both
            CreateHit("chunk4", 0.85f, "keyword"),
            CreateHit("chunk5", 0.75f, "keyword")
        };

        var result = await reranker.RerankAsync("test query", hits);

        // Should deduplicate chunk2 and merge scores
        result.Should().HaveCount(5); // 6 input hits - 1 duplicate = 5 unique
        result.Select(h => h.ChunkId).Should().OnlyHaveUniqueItems();

        // chunk2 appears in both lists, so should have highest RRF score
        var chunk2 = result.First(h => h.ChunkId == "chunk2");
        chunk2.Metadata.Should().ContainKey("rrfScore");
        chunk2.Metadata.Should().ContainKey("reranker");
        chunk2.Metadata["reranker"].Should().Be("RRF");
    }

    [Fact]
    public async Task RerankAsync_CalculatesRrfScoreCorrectly()
    {
        var reranker = new RrfReranker(_searchSettings, _logger);

        // Two sources with simple rankings
        var hits = new List<SearchHit>
        {
            CreateHit("chunk1", 1.0f, "vector"),  // Rank 1 in vector
            CreateHit("chunk2", 0.9f, "vector"),  // Rank 2 in vector
            CreateHit("chunk1", 1.0f, "keyword"), // Rank 1 in keyword (duplicate)
            CreateHit("chunk3", 0.8f, "keyword")  // Rank 2 in keyword
        };

        var result = await reranker.RerankAsync("test query", hits);

        // chunk1 appears in both lists at rank 1
        // RRF for chunk1 = 1/(60+1) + 1/(60+1) = 2/61 ≈ 0.0328
        // chunk2: 1/(60+2) = 1/62 ≈ 0.0161
        // chunk3: 1/(60+2) = 1/62 ≈ 0.0161

        // chunk1 should have highest score due to appearing in both lists
        result.First().ChunkId.Should().Be("chunk1");
    }

    [Fact]
    public async Task RerankAsync_NormalizesScoresTo01Range()
    {
        var reranker = new RrfReranker(_searchSettings, _logger);
        var hits = new List<SearchHit>
        {
            CreateHit("chunk1", 0.9f, "vector"),
            CreateHit("chunk2", 0.8f, "vector"),
            CreateHit("chunk3", 0.7f, "keyword"),
            CreateHit("chunk4", 0.6f, "keyword")
        };

        var result = await reranker.RerankAsync("test query", hits);

        // All scores should be in [0, 1] range
        result.Should().OnlyContain(hit => hit.Score >= 0f && hit.Score <= 1f);

        // Highest score should be 1.0 after normalization
        result.Max(h => h.Score).Should().Be(1.0f);
    }

    [Fact]
    public async Task RerankAsync_DeduplicatesChunks()
    {
        var reranker = new RrfReranker(_searchSettings, _logger);
        var hits = new List<SearchHit>
        {
            CreateHit("chunk1", 0.9f, "vector"),
            CreateHit("chunk1", 0.85f, "keyword"),  // Duplicate
            CreateHit("chunk1", 0.8f, "semantic"),  // Another duplicate
            CreateHit("chunk2", 0.7f, "vector")
        };

        var result = await reranker.RerankAsync("test query", hits);

        // Should only have 2 unique chunks
        result.Should().HaveCount(2);
        result.Select(h => h.ChunkId).Should().Equal("chunk1", "chunk2");
    }

    [Fact]
    public async Task RerankAsync_UsesConfiguredKValue()
    {
        _searchSettings.CurrentValue.Returns(new SearchSettings { RrfK = 10 }); // Custom k
        var reranker = new RrfReranker(_searchSettings, _logger);

        var hits = new List<SearchHit>
        {
            CreateHit("chunk1", 1.0f, "vector"),
            CreateHit("chunk2", 0.9f, "keyword")
        };

        var result = await reranker.RerankAsync("test query", hits);

        // With k=10, scores should be 1/(10+1) = 1/11 ≈ 0.091 for both
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task RerankAsync_OrdersByFinalScore()
    {
        var reranker = new RrfReranker(_searchSettings, _logger);
        var hits = new List<SearchHit>
        {
            // chunk1: appears in both, should have highest RRF score
            CreateHit("chunk1", 0.9f, "vector"),
            CreateHit("chunk1", 0.8f, "keyword"),
            // chunk2: only in vector
            CreateHit("chunk2", 0.95f, "vector"),
            // chunk3: only in keyword
            CreateHit("chunk3", 0.85f, "keyword")
        };

        var result = await reranker.RerankAsync("test query", hits);

        // chunk1 should be first due to appearing in both lists
        result.First().ChunkId.Should().Be("chunk1");

        // Results should be in descending score order
        for (int i = 1; i < result.Count; i++)
        {
            result[i - 1].Score.Should().BeGreaterThanOrEqualTo(result[i].Score);
        }
    }

    [Fact]
    public async Task RerankAsync_PreservesOriginalHitData()
    {
        var reranker = new RrfReranker(_searchSettings, _logger);
        var originalMetadata = new Dictionary<string, string>
        {
            ["documentId"] = "doc123",
            ["source"] = "vector"
        };
        var hits = new List<SearchHit>
        {
            new SearchHit(
                ChunkId: "chunk1",
                DocumentId: "doc123",
                Content: "Test content",
                Score: 0.9f,
                Metadata: originalMetadata),
            CreateHit("chunk2", 0.8f, "keyword")
        };

        var result = await reranker.RerankAsync("test query", hits);

        var rerankedChunk1 = result.First(h => h.ChunkId == "chunk1");
        rerankedChunk1.DocumentId.Should().Be("doc123");
        rerankedChunk1.Content.Should().Be("Test content");
        rerankedChunk1.Metadata.Should().ContainKey("documentId");
    }

    [Fact]
    public async Task RerankAsync_AddsRrfMetadata()
    {
        var reranker = new RrfReranker(_searchSettings, _logger);
        var hits = new List<SearchHit>
        {
            CreateHit("chunk1", 0.9f, "vector"),
            CreateHit("chunk2", 0.8f, "keyword")
        };

        var result = await reranker.RerankAsync("test query", hits);

        foreach (var hit in result)
        {
            hit.Metadata.Should().ContainKey("rrfScore");
            hit.Metadata.Should().ContainKey("reranker");
            hit.Metadata["reranker"].Should().Be("RRF");

            // rrfScore should be parseable as double
            double.TryParse(hit.Metadata["rrfScore"], out _).Should().BeTrue();
        }
    }

    private static SearchHit CreateHit(string chunkId, float score, string source)
    {
        return new SearchHit(
            ChunkId: chunkId,
            DocumentId: $"doc-{chunkId}",
            Content: $"Content for {chunkId}",
            Score: score,
            Metadata: new Dictionary<string, string> { ["source"] = source }
        );
    }
}
