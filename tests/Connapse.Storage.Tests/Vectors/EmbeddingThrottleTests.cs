using Connapse.Core;
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
    public async Task AcquireAsync_LimitRaised_TakesEffectOnTheNextRequest()
    {
        using var held = await _throttle.AcquireAsync(EmbeddingInputType.Document, CancellationToken.None);

        _settings.MaxConcurrentIngestionRequests = 2;
        Task<IDisposable> next = _throttle.AcquireAsync(EmbeddingInputType.Document, CancellationToken.None);

        next.IsCompletedSuccessfully.Should().BeTrue();
        (await next).Dispose();
    }
}
