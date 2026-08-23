using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Sources;

/// <summary>
/// The boundary this guard draws is the whole of its behaviour, so it is pinned by example
/// rather than described. Both terms of the rule are load-bearing: a percentage alone fires
/// constantly on small sources, an absolute alone is meaningless on large ones.
/// </summary>
[Trait("Category", "Unit")]
public class DeletionGuardTests
{
    [Theory]
    // Small sources clean themselves up: re-ingesting five files is cheap, and blocking
    // them would make the guard fire on ordinary tidying.
    [InlineData(5, 5, false)]
    [InlineData(10, 10, false)]
    // Losing everything from a source big enough to notice is suspicious.
    [InlineData(15, 15, true)]
    // Proportionality on large sources: 5% is plausible churn, 50% is not.
    [InlineData(5_000, 100_000, false)]
    [InlineData(50_000, 100_000, true)]
    // Exactly at each bound, so an off-by-one in either term is caught.
    [InlineData(10, 100, false)]
    [InlineData(11, 100, true)]
    // Nothing to delete is never withheld, including on an empty index.
    [InlineData(0, 0, false)]
    [InlineData(0, 100, false)]
    public void ShouldWithhold_AtTheBoundaries_MatchesTheRule(int vanished, int indexed, bool expected)
    {
        DeletionGuard.ShouldWithhold(vanished, indexed).Should().Be(expected);
    }

    [Fact]
    public void ShouldWithhold_MoreVanishedThanIndexed_DoesNotThrow()
    {
        // Not reachable through SyncViaListAndDiffAsync, which derives vanished from the
        // indexed set — but a predicate that throws on nonsense input would turn a caller
        // bug into a failed sync rather than a logged oddity.
        var act = () => DeletionGuard.ShouldWithhold(200, 100);

        act.Should().NotThrow();
    }
}
