using Connapse.Identity.Services;
using FluentAssertions;

namespace Connapse.Identity.Tests;

/// <summary>
/// A confirmation code claims its Entra link once, however many requests carry it at the same moment.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class AzureLinkConfirmationConcurrencyTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task Consume_WhenSimultaneousRequestsAllReadTheCode_YieldsLinkToExactlyOneCaller(int requests)
    {
        // Regression: TryGetValue + Remove is not atomic, so two concurrent /azure/confirm
        // requests carrying the same code could both retrieve the parked link before either
        // removed it and both go on to save it — the advertised single-use boundary would be
        // false. The interlocked claim flag must let exactly one caller win.
        using var cache = new RendezvousCache(requests);
        var confirmations = new AzureLinkConfirmations(cache);
        var link = new PendingAzureLink(Guid.NewGuid(), "oid-1", "tid-1", "Ada Lovelace");
        string code = confirmations.Start(link);

        PendingAzureLink?[] claimed = await cache.SendSimultaneously(() => confirmations.Consume(code));

        claimed.Where(result => result is not null).Should().ContainSingle().Which.Should().Be(link);
    }
}
