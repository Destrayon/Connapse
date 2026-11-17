using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Vectors;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Storage.Tests.Vectors;

[Trait("Category", "Unit")]
public class EmbeddingThrottleTests
{
    private readonly EmbeddingSettings _settings = new() { MaxConcurrentIngestionRequests = 1, MaxConcurrentQueryRequests = 1 };
    private readonly EmbeddingThrottle _throttle;

    public EmbeddingThrottleTests()
    {
        var monitor = Substitute.For<IOptionsMonitor<EmbeddingSettings>>();
        monitor.CurrentValue.Returns(_ => _settings);
        _throttle = new EmbeddingThrottle(monitor);
    }

    [Fact]
    public async Task AcquireAsync_IngestionLaneFull_QueryStillGetsASlotAtOnce()
    {
        using var ingestion = await _throttle.AcquireAsync(EmbeddingInputType.Document, CancellationToken.None);

        Task<IDisposable> query = _throttle.AcquireAsync(EmbeddingInputType.Query, CancellationToken.None);

        query.IsCompletedSuccessfully.Should().BeTrue("a busy ingestion lane must not hold up a search");
        (await query).Dispose();
    }

    [Fact]
    public async Task AcquireAsync_IngestionLaneFull_NextIngestionRequestWaitsForARelease()
    {
        IDisposable first = await _throttle.AcquireAsync(EmbeddingInputType.Document, CancellationToken.None);

        Task<IDisposable> second = _throttle.AcquireAsync(EmbeddingInputType.Document, CancellationToken.None);
        second.IsCompleted.Should().BeFalse();

        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task EmbedBatchAsync_LargeDocumentEmbedding_SmallDocumentRunsBetweenItsRequests()
    {
        // CON-354: the slot was taken once around a whole document's batch, so a large PDF's thousands of sentences
        // held it for the hours they took and a small upload's embedding waited behind all of them.
        _settings.BatchSize = 2;
        var inner = new RecordingProvider();
        var provider = new ThrottledEmbeddingProvider(inner, _throttle);

        Task large = provider.EmbedBatchAsync(Enumerable.Range(0, 10).Select(i => $"large {i}"), EmbeddingInputType.Document);
        await inner.Started.WaitAsync(TimeSpan.FromSeconds(5));
        Task small = provider.EmbedBatchAsync(["small 0"], EmbeddingInputType.Document);
        inner.Release();
        await Task.WhenAll(large, small).WaitAsync(TimeSpan.FromSeconds(5));

        inner.Requests.Should().HaveCount(6, "each provider request of BatchSize texts takes the slot on its own");
        inner.Requests.FindIndex(r => r[0] == "small 0").Should().BeLessThan(5,
            "the small document must not wait for the large document's last request");
    }

    [Fact]
    public async Task EmbedBatchAsync_SplitIntoRequests_ReturnsVectorsInInputOrder()
    {
        _settings.BatchSize = 3;
        var inner = new RecordingProvider();
        inner.Release();
        var provider = new ThrottledEmbeddingProvider(inner, _throttle);

        IReadOnlyList<float[]> vectors = await provider.EmbedBatchAsync(
            Enumerable.Range(0, 7).Select(i => $"t {i}"), EmbeddingInputType.Document);

        vectors.Select(v => (int)v[0]).Should().Equal(0, 1, 2, 3, 4, 5, 6);
    }

    /// <summary>Records each request; the first waits for <see cref="Release"/>. A vector is the text's number.</summary>
    private sealed class RecordingProvider : IEmbeddingProvider
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<IReadOnlyList<string>> Requests { get; } = [];
        public Task Started => _started.Task;
        public int Dimensions => 1;
        public string ModelId => "recording";

        public void Release() => _release.TrySetResult();

        public Task<float[]> EmbedAsync(string text, EmbeddingInputType inputType, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IEnumerable<string> texts, EmbeddingInputType inputType, CancellationToken ct = default)
        {
            List<string> batch = texts.ToList();
            lock (Requests) Requests.Add(batch);
            _started.TrySetResult();
            await _release.Task;
            return batch.Select(t => new[] { float.Parse(t.Split(' ')[1]) }).ToList();
        }
    }

    [Fact]
    public async Task AcquireAsync_LimitRaised_TakesEffectOnTheNextRequest()
    {
        using var held = await _throttle.AcquireAsync(EmbeddingInputType.Document, CancellationToken.None);

        _settings.MaxConcurrentIngestionRequests = 2;
        Task<IDisposable> next = _throttle.AcquireAsync(EmbeddingInputType.Document, CancellationToken.None);

        next.IsCompletedSuccessfully.Should().BeTrue();
        (await next).Dispose();
    }
}
