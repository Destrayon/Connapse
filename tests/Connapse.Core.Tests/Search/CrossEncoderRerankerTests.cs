using System.Net;
using System.Text.Json;
using Connapse.Core;
using Connapse.Search.Reranking;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Search;

[Trait("Category", "Unit")]
public class CrossEncoderRerankerTests
{
    private readonly ILogger<CrossEncoderReranker> _logger;

    public CrossEncoderRerankerTests()
    {
        _logger = Substitute.For<ILogger<CrossEncoderReranker>>();
    }

    [Fact]
    public void Name_ReturnsCrossEncoder()
    {
        var reranker = CreateReranker(new SearchSettings());
        reranker.Name.Should().Be("CrossEncoder");
    }

    [Fact]
    public async Task RerankAsync_EmptyList_ReturnsEmptyList()
    {
        var reranker = CreateReranker(new SearchSettings { CrossEncoderModel = "test-model" });
        var result = await reranker.RerankAsync("test query", []);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task RerankAsync_NoCrossEncoderModel_ReturnsOriginalHits()
    {
        var reranker = CreateReranker(new SearchSettings { CrossEncoderModel = null });
        var hits = new List<SearchHit> { CreateHit("chunk1", 0.5f) };

        var result = await reranker.RerankAsync("test query", hits);

        result.Should().HaveCount(1);
        result[0].ChunkId.Should().Be("chunk1");
        result[0].Score.Should().Be(0.5f);
        result[0].Metadata.Should().NotContainKey("reranker");
    }

    [Fact]
    public async Task RerankAsync_TeiProvider_ScoresAndReorders()
    {
        // TEI returns scores in [{"index":0,"score":0.2},{"index":1,"score":0.9}]
        var teiResponse = JsonSerializer.Serialize(new[]
        {
            new { index = 0, score = 0.2 },
            new { index = 1, score = 0.9 },
            new { index = 2, score = 0.5 }
        });

        var reranker = CreateReranker(
            new SearchSettings
            {
                CrossEncoderProvider = "TEI",
                CrossEncoderModel = "bge-reranker-large",
                CrossEncoderBaseUrl = "http://localhost:8080"
            },
            teiResponse);

        var hits = new List<SearchHit>
        {
            CreateHit("chunk1", 0.9f),
            CreateHit("chunk2", 0.1f),
            CreateHit("chunk3", 0.5f)
        };

        var result = await reranker.RerankAsync("test query", hits);

        result.Should().HaveCount(3);
        // Reordered by cross-encoder score: chunk2 (0.9) > chunk3 (0.5) > chunk1 (0.2)
        result[0].ChunkId.Should().Be("chunk2");
        result[1].ChunkId.Should().Be("chunk3");
        result[2].ChunkId.Should().Be("chunk1");
    }

    [Fact]
    public async Task RerankAsync_CohereProvider_ParsesResponse()
    {
        var cohereResponse = JsonSerializer.Serialize(new
        {
            results = new[]
            {
                new { index = 1, relevance_score = 0.95 },
                new { index = 0, relevance_score = 0.3 }
            }
        });

        var reranker = CreateReranker(
            new SearchSettings
            {
                CrossEncoderProvider = "Cohere",
                CrossEncoderModel = "rerank-v3.5",
                CrossEncoderApiKey = "test-key"
            },
            cohereResponse);

        var hits = new List<SearchHit>
        {
            CreateHit("chunk1", 0.5f),
            CreateHit("chunk2", 0.5f)
        };

        var result = await reranker.RerankAsync("test query", hits);

        result.Should().HaveCount(2);
        result[0].ChunkId.Should().Be("chunk2");
        result[1].ChunkId.Should().Be("chunk1");
    }

    [Fact]
    public async Task RerankAsync_JinaProvider_ParsesResponse()
    {
        var jinaResponse = JsonSerializer.Serialize(new
        {
            results = new[]
            {
                new { index = 0, relevance_score = 0.8 },
                new { index = 1, relevance_score = 0.6 }
            }
        });

        var reranker = CreateReranker(
            new SearchSettings
            {
                CrossEncoderProvider = "Jina",
                CrossEncoderModel = "jina-reranker-v3",
                CrossEncoderApiKey = "test-key"
            },
            jinaResponse);

        var hits = new List<SearchHit>
        {
            CreateHit("chunk1", 0.1f),
            CreateHit("chunk2", 0.9f)
        };

        var result = await reranker.RerankAsync("test query", hits);

        result.Should().HaveCount(2);
        result[0].ChunkId.Should().Be("chunk1");
        result[1].ChunkId.Should().Be("chunk2");
    }

    [Fact]
    public async Task RerankAsync_NormalizesScoresToZeroOneRange()
    {
        var response = JsonSerializer.Serialize(new[]
        {
            new { index = 0, score = -2.0 },
            new { index = 1, score = 5.0 },
            new { index = 2, score = 1.5 }
        });

        var reranker = CreateReranker(
            new SearchSettings
            {
                CrossEncoderProvider = "TEI",
                CrossEncoderModel = "test-model",
                CrossEncoderBaseUrl = "http://localhost:8080"
            },
            response);

        var hits = new List<SearchHit>
        {
            CreateHit("chunk1", 0.5f),
            CreateHit("chunk2", 0.5f),
            CreateHit("chunk3", 0.5f)
        };

        var result = await reranker.RerankAsync("test query", hits);

        // Scores normalized: -2→0.0, 5→1.0, 1.5→0.5
        result.Should().AllSatisfy(h =>
        {
            h.Score.Should().BeInRange(0f, 1f);
        });
        result[0].Score.Should().Be(1.0f); // highest (5.0)
        result[^1].Score.Should().Be(0.0f); // lowest (-2.0)
    }

    [Fact]
    public async Task RerankAsync_AddsCorrectMetadata()
    {
        var response = JsonSerializer.Serialize(new[]
        {
            new { index = 0, score = 0.8 }
        });

        var reranker = CreateReranker(
            new SearchSettings
            {
                CrossEncoderProvider = "TEI",
                CrossEncoderModel = "bge-reranker",
                CrossEncoderBaseUrl = "http://localhost:8080"
            },
            response);

        var hits = new List<SearchHit> { CreateHit("chunk1", 0.5f) };

        var result = await reranker.RerankAsync("test query", hits);

        result[0].Metadata.Should().ContainKey("crossEncoderScore");
        result[0].Metadata.Should().ContainKey("reranker");
        result[0].Metadata["reranker"].Should().Be("CrossEncoder");
        result[0].Metadata.Should().ContainKey("crossEncoderProvider");
        result[0].Metadata["crossEncoderProvider"].Should().Be("TEI");
        float.TryParse(result[0].Metadata["crossEncoderScore"], out _).Should().BeTrue();
    }

    [Fact]
    public async Task RerankAsync_PreservesOriginalHitData()
    {
        var response = JsonSerializer.Serialize(new[]
        {
            new { index = 0, score = 0.9 },
            new { index = 1, score = 0.1 }
        });

        var reranker = CreateReranker(
            new SearchSettings
            {
                CrossEncoderProvider = "TEI",
                CrossEncoderModel = "test",
                CrossEncoderBaseUrl = "http://localhost:8080"
            },
            response);

        var hits = new List<SearchHit>
        {
            new("id-1", "doc-1", "Content A", 0.5f, new Dictionary<string, string> { ["source"] = "vector" }),
            new("id-2", "doc-2", "Content B", 0.3f, new Dictionary<string, string> { ["source"] = "keyword" })
        };

        var result = await reranker.RerankAsync("test query", hits);

        var first = result.First(h => h.ChunkId == "id-1");
        first.DocumentId.Should().Be("doc-1");
        first.Content.Should().Be("Content A");
        first.Metadata["source"].Should().Be("vector");
    }

    [Fact]
    public async Task RerankAsync_HttpError_FallsBackToOriginalOrder()
    {
        var handler = new MockHttpHandler(HttpStatusCode.InternalServerError, "Server error");
        var httpClientFactory = CreateHttpClientFactory(handler);

        var settings = new SearchSettings
        {
            CrossEncoderProvider = "TEI",
            CrossEncoderModel = "test",
            CrossEncoderBaseUrl = "http://localhost:8080"
        };
        var monitor = Substitute.For<IOptionsMonitor<SearchSettings>>();
        monitor.CurrentValue.Returns(settings);

        var reranker = new CrossEncoderReranker(monitor, httpClientFactory, _logger);
        var hits = new List<SearchHit>
        {
            CreateHit("chunk1", 0.9f),
            CreateHit("chunk2", 0.5f)
        };

        var result = await reranker.RerankAsync("test query", hits);

        // Falls back to original hits unchanged
        result.Should().HaveCount(2);
        result[0].ChunkId.Should().Be("chunk1");
        result[0].Score.Should().Be(0.9f);
        result[0].Metadata.Should().NotContainKey("reranker");
    }

    [Fact]
    public async Task RerankAsync_SingleHit_NormalizesToOne()
    {
        var response = JsonSerializer.Serialize(new[]
        {
            new { index = 0, score = 0.75 }
        });

        var reranker = CreateReranker(
            new SearchSettings
            {
                CrossEncoderProvider = "TEI",
                CrossEncoderModel = "test",
                CrossEncoderBaseUrl = "http://localhost:8080"
            },
            response);

        var hits = new List<SearchHit> { CreateHit("chunk1", 0.3f) };

        var result = await reranker.RerankAsync("test query", hits);

        // Single hit → scoreRange = 0, normalized to 1.0
        result[0].Score.Should().Be(1.0f);
    }

    // --- Helpers ---

    private CrossEncoderReranker CreateReranker(SearchSettings settings, string? httpResponse = null)
    {
        var monitor = Substitute.For<IOptionsMonitor<SearchSettings>>();
        monitor.CurrentValue.Returns(settings);

        var handler = new MockHttpHandler(HttpStatusCode.OK, httpResponse ?? "[]");
        var httpClientFactory = CreateHttpClientFactory(handler);

        return new CrossEncoderReranker(monitor, httpClientFactory, _logger);
    }

    private static IHttpClientFactory CreateHttpClientFactory(MockHttpHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("CrossEncoder").Returns(_ => new HttpClient(handler));
        return factory;
    }

    private static SearchHit CreateHit(string chunkId, float score)
    {
        return new SearchHit(
            ChunkId: chunkId,
            DocumentId: $"doc-{chunkId}",
            Content: $"Content for {chunkId}",
            Score: score,
            Metadata: new Dictionary<string, string> { ["source"] = "vector" }
        );
    }

    /// <summary>
    /// Simple mock HTTP handler that returns a fixed response.
    /// </summary>
    private class MockHttpHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
