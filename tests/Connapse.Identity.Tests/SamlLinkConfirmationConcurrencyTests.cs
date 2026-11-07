using Connapse.Identity.Services;
using FluentAssertions;

namespace Connapse.Identity.Tests;

/// <summary>
/// A confirmation code claims its link once, however many requests carry it at the same moment.
/// </summary>
/// <remarks>
/// The window is the gap between reading a parked link and removing it, a few instructions wide, so
/// threads released together rarely land inside it. <see cref="RendezvousCache"/> holds every caller
/// in that gap until all of them have read the entry, which makes the overlap certain rather than
/// lucky: a store that treats the removal as its single-use boundary hands the link to every caller.
/// </remarks>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class SamlLinkConfirmationConcurrencyTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task Consume_WhenSimultaneousRequestsAllReadTheCode_YieldsLinkToExactlyOneCaller(int requests)
    {
        using var cache = new RendezvousCache(requests);
        var confirmations = new SamlLinkConfirmations(cache);
        var link = new PendingIdentityLink(Guid.NewGuid(), "dir-1", "person", null);
        string code = confirmations.Start(link);

        PendingIdentityLink?[] claimed = await cache.SendSimultaneously(() => confirmations.Consume(code));

        claimed.Where(result => result is not null).Should().ContainSingle().Which.Should().Be(link);
    }
}
