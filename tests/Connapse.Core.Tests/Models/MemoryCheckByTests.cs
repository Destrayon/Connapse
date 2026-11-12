using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.Models;

/// <summary>How a requested check-by turns into a date and the period a confirm later resets it by (CON-340).</summary>
[Trait("Category", "Unit")]
public class MemoryCheckByTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void From_Never_GivesNoDate()
    {
        MemoryCheckBy.Never.IsNever.Should().BeTrue();
        MemoryCheckBy.Never.From(Now).Should().Be(((DateTimeOffset?)null, (int?)null));
    }

    [Fact]
    public void From_Period_DatesFromNow()
    {
        MemoryCheckBy check = MemoryCheckBy.After(30);

        check.IsNever.Should().BeFalse();
        check.From(Now).Should().Be(((DateTimeOffset?)Now.AddDays(30), (int?)30));
    }

    [Fact]
    public void From_DateAhead_KeepsTheDateAndRoundsThePeriodUp()
    {
        DateTimeOffset at = Now.AddDays(10).AddHours(1);

        new MemoryCheckBy(at, null).From(Now).Should().Be(((DateTimeOffset?)at, (int?)11));
    }

    [Fact]
    public void From_PastDate_KeepsTheDateWithTheShortestPeriod()
    {
        DateTimeOffset at = Now.AddDays(-5);

        new MemoryCheckBy(at, null).From(Now).Should().Be(((DateTimeOffset?)at, (int?)1));
    }

    [Fact]
    public void From_DateBeyondTenYears_CapsThePeriod()
    {
        DateTimeOffset at = Now.AddYears(20);

        new MemoryCheckBy(at, null).From(Now).Should().Be(((DateTimeOffset?)at, (int?)MemoryCheckBy.MaxDays));
    }

    [Fact]
    public void From_DateAndPeriod_TheDateWins()
    {
        DateTimeOffset at = Now.AddDays(3);

        new MemoryCheckBy(at, 90).From(Now).Should().Be(((DateTimeOffset?)at, (int?)3));
    }
}
