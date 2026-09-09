using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Identity.Tests;

[Trait("Category", "Unit")]
public class AzureLinkConfirmationsTests
{
    private static AzureLinkConfirmations NewStore() =>
        new(new MemoryCache(new MemoryCacheOptions()));

    private static PendingAzureLink SampleLink() =>
        new(Guid.NewGuid(), "oid-1", "tid-1", "Ada Lovelace");

    [Fact]
    public void Consume_ReturnsParkedLink_ThenNullOnReuse()
    {
        var store = NewStore();
        PendingAzureLink link = SampleLink();

        string code = store.Start(link);

        store.Consume(code).Should().BeEquivalentTo(link);
        store.Consume(code).Should().BeNull(); // single use
    }

    [Fact]
    public void Consume_UnknownOrBlankCode_ReturnsNull()
    {
        var store = NewStore();

        store.Consume(null).Should().BeNull();
        store.Consume("").Should().BeNull();
        store.Consume("not-a-real-code").Should().BeNull();
    }

    [Fact]
    public void Consume_UnderConcurrency_YieldsLinkToExactlyOneCaller()
    {
        // Regression: TryGetValue + Remove is not atomic, so two concurrent /azure/confirm
        // requests carrying the same code could both retrieve the parked link before either
        // removed it and both go on to save it — the advertised single-use boundary would be
        // false. The interlocked claim flag must let exactly one caller win.
        const int racers = 16;
        var store = NewStore();
        string code = store.Start(SampleLink());

        int winners = 0;
        using var barrier = new Barrier(racers);
        Parallel.For(0, racers, _ =>
        {
            barrier.SignalAndWait(); // release all threads into Consume at once
            if (store.Consume(code) is not null)
                Interlocked.Increment(ref winners);
        });

        winners.Should().Be(1);
    }
}
