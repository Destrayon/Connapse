using Connapse.Web.Services;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Services;

/// <summary>
/// A source's own sync interval, which the five-minute timer honours by skipping ticks.
/// </summary>
[Trait("Category", "Unit")]
public class SourceSyncIntervalTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private static Source Source(int? interval, DateTime? lastSynced) => new(
        Guid.NewGuid(), "s", null, Guid.NewGuid(), "{}", Now, Now,
        LastSyncedAt: lastSynced, SyncIntervalSeconds: interval);

    [Fact]
    public void IsDue_NoInterval_EveryTick() =>
        SourceSyncService.IsDue(Source(null, Now.AddSeconds(-1)), Now).Should().BeTrue();

    [Fact]
    public void IsDue_NeverSynced_EveryTick() =>
        SourceSyncService.IsDue(Source(900, null), Now).Should().BeTrue();

    [Fact]
    public void IsDue_WithinInterval_SitsOut() =>
        SourceSyncService.IsDue(Source(900, Now.AddMinutes(-10)), Now).Should().BeFalse();

    [Fact]
    public void IsDue_TickLandingSecondsShort_StillRuns() =>
        SourceSyncService.IsDue(Source(900, Now.AddSeconds(-895)), Now).Should().BeTrue(
            "the last cycle is stamped when it ends, so the matching tick arrives a little early");
}
