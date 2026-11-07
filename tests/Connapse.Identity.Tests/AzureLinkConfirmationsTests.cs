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
}
