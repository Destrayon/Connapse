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
    public async Task RerankAsync_VoyageProvider_ParsesResponse()
    {
        var voyageResponse = JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new { index = 1, relevance_score = 0.92 },
                new { index = 0, relevance_score = 0.41 }
            }
        });

        var reranker = CreateReranker(
            new SearchSettings
            {
                CrossEncoderProvider = "Voyage",
                CrossEncoderModel = "rerank-2.5-lite",
                CrossEncoderApiKey = "test-key"
            },
            voyageResponse);

        var hits = new List<SearchHit>
        {
            CreateHit("chunk1", 0.5f),
            CreateHit("chunk2", 0.5f)
        };

        var result = await reranker.RerankAsync("test query", hits);

        result.Should().HaveCount(2);
        result[0].ChunkId.Should().Be("chunk2");  // index=1, score=0.92
        result[1].ChunkId.Should().Be("chunk1");  // index=0, score=0.41
    }

    [Fact]
    public async Task RerankAsync_VoyageProvider_ScoresOrderedDescending()
    {
        var voyageResponse = JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new { index = 0, relevance_score = 0.1 },
                new { index = 2, relevance_score = 0.85 },
                new { index = 1, relevance_score = 0.5 }
            }
        });

        var reranker = CreateReranker(
            new SearchSettings
            {
                CrossEncoderProvider = "Voyage",
                CrossEncoderModel = "rerank-2",
                CrossEncoderApiKey = "test-key"
            },
            voyageResponse);

        var hits = new List<SearchHit>
        {
            CreateHit("chunk1", 0.3f),
            CreateHit("chunk2", 0.3f),
            CreateHit("chunk3", 0.3f)
        };

        var result = await reranker.RerankAsync("test query", hits);

        result.Should().HaveCount(3);
        result[0].ChunkId.Should().Be("chunk3");  // index=2, score=0.85
        result[1].ChunkId.Should().Be("chunk2");  // index=1, score=0.5
        result[2].ChunkId.Should().Be("chunk1");  // index=0, score=0.1
    }

    [Fact]
    public async Task RerankAsync_VoyageProvider_HttpError_FallsBackToOriginalOrder()
    {
        var handler = new MockHttpHandler(HttpStatusCode.TooManyRequests, "Rate limit exceeded");
        var httpClientFactory = CreateHttpClientFactory(handler);

        var settings = new SearchSettings
        {
            CrossEncoderProvider = "Voyage",
            CrossEncoderModel = "rerank-2.5-lite",
            CrossEncoderApiKey = "test-key"
        };
        var monitor = Substitute.For<IOptionsMonitor<SearchSettings>>();
        monitor.CurrentValue.Returns(settings);

        var reranker = new CrossEncoderReranker(monitor, httpClientFactory, new RerankerAvailability(TimeProvider.System), _logger);
        var hits = new List<SearchHit>
        {
            CreateHit("chunk1", 0.9f),
            CreateHit("chunk2", 0.5f)
        };

        var result = await reranker.RerankAsync("test query", hits);

        result.Should().HaveCount(2);
        result[0].ChunkId.Should().Be("chunk1");
        result[0].Score.Should().Be(0.9f);
        result[0].Metadata.Should().NotContainKey("reranker");
    }

    [Fact]
    public async Task RerankAsync_PassesThroughProviderScoresDirectly()
    {
        var response = JsonSerializer.Serialize(new[]
        {
            new { index = 0, score = 0.2 },
            new { index = 1, score = 0.9 },
            new { index = 2, score = 0.5 }
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

        // Provider scores passed through directly, no min-max normalization
        result[0].Score.Should().Be(0.9f);  // chunk2
        result[1].Score.Should().Be(0.5f);  // chunk3
        result[2].Score.Should().Be(0.2f);  // chunk1
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

        var reranker = new CrossEncoderReranker(monitor, httpClientFactory, new RerankerAvailability(TimeProvider.System), _logger);
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
    public async Task RerankAsync_SingleHit_RetainsProviderScore()
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

        // Single hit — provider score passed through directly
        result[0].Score.Should().Be(0.75f);
    }

    // A reranker that can't be reached is remembered for a while (#668), so searches don't each pay a
    // retry and an error log while it is down. Only connection-level failures count.
    [Fact]
    public async Task RerankAsync_ServiceUnreachable_SkipsTheServiceUntilTheCooldownEnds()
    {
        var clock = new ManualClock();
        var handler = new ThrowingHttpHandler(() => new HttpRequestException("Connection refused"));
        var reranker = CreateReranker(handler, clock);
        List<SearchHit> hits = [CreateHit("chunk1", 0.9f), CreateHit("chunk2", 0.5f)];

        (await reranker.RerankAsync("q", hits)).Should().Equal(hits);
        (await reranker.RerankAsync("q", hits)).Should().Equal(hits);
        handler.Calls.Should().Be(1, "the second search skips the unreachable service");

        clock.Advance(RerankerAvailability.Cooldown + TimeSpan.FromSeconds(1));
        await reranker.RerankAsync("q", hits);
        handler.Calls.Should().Be(2, "after the cooldown the service is tried again");
    }

    [Fact]
    public async Task RerankAsync_ServiceTimesOut_IsSkippedLikeAnUnreachableOne()
    {
        var handler = new ThrowingHttpHandler(() => new TaskCanceledException("timed out"));
        var reranker = CreateReranker(handler, new ManualClock());

        await reranker.RerankAsync("q", [CreateHit("chunk1", 0.9f)]);
        await reranker.RerankAsync("q", [CreateHit("chunk1", 0.9f)]);

        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task RerankAsync_ServiceAnswersWithAnError_IsNotSkipped()
    {
        var handler = new MockHttpHandler(HttpStatusCode.InternalServerError, "Server error");
        var reranker = CreateReranker(handler, new ManualClock());

        await reranker.RerankAsync("q", [CreateHit("chunk1", 0.9f)]);
        await reranker.RerankAsync("q", [CreateHit("chunk1", 0.9f)]);

        handler.Calls.Should().Be(2, "a service that answers is reachable; its errors are logged per search");
    }

    [Fact]
    public async Task RerankAsync_SearchCancelled_DoesNotMarkTheServiceUnreachable()
    {
        var handler = new ThrowingHttpHandler(() => new TaskCanceledException("cancelled"));
        var reranker = CreateReranker(handler, new ManualClock());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await reranker.RerankAsync("q", [CreateHit("chunk1", 0.9f)], cancelled.Token);
        int before = handler.Calls;
        await reranker.RerankAsync("q", [CreateHit("chunk1", 0.9f)]);

        handler.Calls.Should().Be(before + 1, "a cancelled search says nothing about the service");
    }

    [Fact]
    public async Task RerankAsync_ServiceRecovers_IsUsedAgainAfterTheCooldown()
    {
        var clock = new ManualClock();
        bool down = true;
        var handler = new ThrowingHttpHandler(() => down ? new HttpRequestException("Connection refused") : null,
            "[{\"index\":0,\"score\":0.8}]");
        var reranker = CreateReranker(handler, clock);

        await reranker.RerankAsync("q", [CreateHit("chunk1", 0.3f)]);
        down = false;
        clock.Advance(RerankerAvailability.Cooldown + TimeSpan.FromSeconds(1));
        List<SearchHit> result = await reranker.RerankAsync("q", [CreateHit("chunk1", 0.3f)]);
        await reranker.RerankAsync("q", [CreateHit("chunk1", 0.3f)]);

        result[0].Score.Should().Be(0.8f);
        handler.Calls.Should().Be(3, "once it answers, every search uses it again");
    }

    [Fact]
    public async Task RerankAsync_CircuitOpen_DoesNotHoldItAgainstThisEndpoint()
    {
        // The circuit is shared by every endpoint; an open one may be the previous endpoint's.
        bool open = true;
        var handler = new ThrowingHttpHandler(
            () => open ? new Polly.CircuitBreaker.BrokenCircuitException("open") : null,
            "[{\"index\":0,\"score\":0.8}]");
        var reranker = CreateReranker(handler, new ManualClock());

        (await reranker.RerankAsync("q", [CreateHit("chunk1", 0.3f)]))[0].Score.Should().Be(0.3f);
        open = false;
        List<SearchHit> result = await reranker.RerankAsync("q", [CreateHit("chunk1", 0.3f)]);

        result[0].Score.Should().Be(0.8f, "the next search goes straight to the endpoint");
    }

    [Fact]
    public void RerankerAvailability_EndpointsAreTrackedSeparately()
    {
        var availability = new RerankerAvailability(new ManualClock());

        availability.MarkUnreachable("TEI|http://a").Should().BeTrue("the first failure starts an outage");
        availability.MarkUnreachable("TEI|http://a").Should().BeFalse("later failures belong to the same outage");

        availability.IsSkipped("TEI|http://a").Should().BeTrue();
        availability.IsSkipped("TEI|http://b").Should().BeFalse();
        availability.MarkReachable("TEI|http://a").Should().BeTrue("a recovery ends the outage");
        availability.IsSkipped("TEI|http://a").Should().BeFalse();
    }

    [Fact]
    public void RerankerAvailability_Clear_ForgetsEveryOutage()
    {
        var availability = new RerankerAvailability(new ManualClock());
        availability.MarkUnreachable("TEI|http://a");
        availability.MarkUnreachable("Cohere|");

        availability.Clear();

        availability.IsSkipped("TEI|http://a").Should().BeFalse();
        availability.MarkUnreachable("TEI|http://a").Should().BeTrue("a new failure starts a new outage");
    }

    [Fact]
    public async Task RerankerAvailability_ConcurrentFirstFailures_StartOneOutage()
    {
        var availability = new RerankerAvailability(TimeProvider.System);
        using var start = new ManualResetEventSlim();

        Task<bool>[] marks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => { start.Wait(); return availability.MarkUnreachable("TEI|http://a"); }))
            .ToArray();
        start.Set();
        bool[] started = await Task.WhenAll(marks);

        started.Count(s => s).Should().Be(1, "one warning per outage");
    }

    // --- Helpers ---

    [Fact]
    public async Task RerankAsync_TeiProvider_AsksTeiToTruncateLongChunks()
    {
        var handler = new MockHttpHandler(HttpStatusCode.OK, "[{\"index\":0,\"score\":0.7}]");
        var monitor = Substitute.For<IOptionsMonitor<SearchSettings>>();
        monitor.CurrentValue.Returns(new SearchSettings { CrossEncoderProvider = "TEI" });
        var reranker = new CrossEncoderReranker(monitor, CreateHttpClientFactory(handler), new RerankerAvailability(TimeProvider.System), _logger);

        await reranker.RerankAsync("q", [CreateHit("chunk1", 0.5f)]);

        using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("truncate").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("raw_scores").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void SearchSettings_RerankerDefaults_MatchTheComposeService()
    {
        var settings = new SearchSettings();

        settings.CrossEncoderModel.Should().Be("Alibaba-NLP/gte-reranker-modernbert-base");
        settings.RerankCandidates.Should().Be(30);
        settings.CrossEncoderTimeoutSeconds.Should().Be(5);
    }

    private CrossEncoderReranker CreateReranker(SearchSettings settings, string? httpResponse = null)
    {
        var monitor = Substitute.For<IOptionsMonitor<SearchSettings>>();
        monitor.CurrentValue.Returns(settings);

        var handler = new MockHttpHandler(HttpStatusCode.OK, httpResponse ?? "[]");
        var httpClientFactory = CreateHttpClientFactory(handler);

        return new CrossEncoderReranker(monitor, httpClientFactory, new RerankerAvailability(TimeProvider.System), _logger);
    }

    private CrossEncoderReranker CreateReranker(HttpMessageHandler handler, TimeProvider clock)
    {
        var monitor = Substitute.For<IOptionsMonitor<SearchSettings>>();
        monitor.CurrentValue.Returns(new SearchSettings
        {
            CrossEncoderProvider = "TEI",
            CrossEncoderModel = "test",
            CrossEncoderBaseUrl = "http://localhost:8080"
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("CrossEncoder").Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new CrossEncoderReranker(monitor, factory, new RerankerAvailability(clock), _logger);
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
        public string? LastRequestBody { get; private set; }

        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>Throws the exception the factory returns, or answers 200 with the body when it returns null.</summary>
    private sealed class ThrowingHttpHandler(Func<Exception?> failure, string body = "[]") : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (failure() is Exception ex)
                throw ex;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
