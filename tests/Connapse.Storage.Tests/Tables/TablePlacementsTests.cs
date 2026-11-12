using Connapse.Core;
using Connapse.Storage.Tables;
using FluentAssertions;
using Xunit;

namespace Connapse.Storage.Tests.Tables;

[Trait("Category", "Unit")]
public class TablePlacementsTests
{
    [Fact]
    public void Match_SamePlacements_MatchOneToOne()
    {
        TablePlacement[] tables = [new(1, 1, "aaaa"), new(2, 2, "bbbb")];

        TablePlacements.Match(tables, tables).Should().Equal(0, 1);
    }

    [Fact]
    public void Match_TableInsertedBefore_KeepsTheMovedTablesByHeader()
    {
        TablePlacement[] existing = [new(1, 1, "aaaa"), new(2, 2, "bbbb")];
        TablePlacement[] incoming = [new(1, 1, "cccc"), new(1, 2, "aaaa"), new(2, 3, "bbbb")];

        TablePlacements.Match(incoming, existing).Should().Equal(null, 0, 1);
    }

    [Fact]
    public void Match_SameHeaderTwice_ExactPlacementWinsThenOrder()
    {
        TablePlacement[] existing = [new(1, 1, "aaaa"), new(3, 2, "aaaa")];
        TablePlacement[] incoming = [new(2, 1, "aaaa"), new(3, 2, "aaaa"), new(4, 3, "aaaa")];

        TablePlacements.Match(incoming, existing).Should().Equal(0, 1, null);
    }

    [Fact]
    public void Match_HeaderChanged_IsANewTable()
    {
        TablePlacements.Match([new TablePlacement(1, 1, "bbbb")], [new TablePlacement(1, 1, "aaaa")])
            .Should().Equal([(int?)null]);
    }

    [Fact]
    public void Match_NoExisting_AllNew()
    {
        TablePlacements.Match([new TablePlacement(null, 1, "aaaa")], []).Should().Equal([(int?)null]);
    }
}
