using Connapse.Application.Memory;
using Connapse.Web.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Connapse.Core.Tests.Web;

/// <summary>
/// CON-282: the memory routes read a remember or correct body into the fact the memory tools would build from the same
/// values, and name the first value they can't read.
/// </summary>
[Trait("Category", "Unit")]
public class MemoryFactRouteInputTests
{
    private static readonly Guid Document = Guid.NewGuid();

    [Fact]
    public void NewFact_ReadableBody_IsTheFactTheToolBuilds()
    {
        var (fact, problem) = MemoryFactsEndpoints.NewFact("Halcyon", "database", "PostgreSQL 15", "2026-03-01", null,
            [Document.ToString(), "node:HAL-17"], "HAL-17", keepSourcesWhenNone: false);

        problem.Should().BeNull();
        fact.Should().BeEquivalentTo(new NewMemoryFact("Halcyon", "database", "PostgreSQL 15",
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), null,
            [new MemorySource(MemorySourceKind.Document, Document.ToString()), new MemorySource(MemorySourceKind.Node, "HAL-17")], "HAL-17"));
    }

    [Fact]
    public void NewFact_MissingText_IsEmpty_SoTheOperationRefusesIt()
    {
        var (fact, _) = MemoryFactsEndpoints.NewFact(null, null, null, null, null, null, null, keepSourcesWhenNone: false);
        (fact!.Subject, fact.Attribute, fact.Value).Should().Be(("", "", ""));
    }

    [Fact]
    public void NewFact_NoSources_AreNoneForANewFact_AndTheOldOnesForACorrection()
    {
        MemoryFactsEndpoints.NewFact("s", "a", "v", null, null, null, null, keepSourcesWhenNone: false).Fact!.Sources.Should().BeEmpty();
        MemoryFactsEndpoints.NewFact("s", "a", "v", null, null, null, null, keepSourcesWhenNone: true).Fact!.Sources.Should().BeNull();
    }

    [Fact]
    public void NewFact_EmptySourceList_IsNoneEvenForACorrection() =>
        MemoryFactsEndpoints.NewFact("s", "a", "v", null, null, [], null, keepSourcesWhenNone: true).Fact!.Sources.Should().BeEmpty();

    [Theory]
    [InlineData("soon", null, null, "validFrom")]
    [InlineData(null, "later", null, "validTo")]
    [InlineData("soon", "later", null, "validFrom")]
    [InlineData(null, null, "HAL-17", "sources")]
    public void NewFact_UnreadableValue_IsA400NamingIt(string? from, string? to, string? source, string field)
    {
        var (fact, problem) = MemoryFactsEndpoints.NewFact("s", "a", "v", from, to, source is null ? null : [source], null, keepSourcesWhenNone: false);

        fact.Should().BeNull();
        ProblemHttpResult result = problem.Should().BeOfType<ProblemHttpResult>().Subject;
        result.StatusCode.Should().Be(400);
        result.ProblemDetails.Extensions["field"].Should().Be(field);
    }

    [Fact]
    public void NewFact_CheckBy_IsReadLikeTheToolReadsIt()
    {
        MemoryFactsEndpoints.NewFact("s", "a", "v", null, null, null, null, keepSourcesWhenNone: false, checkBy: "30d")
            .Fact!.Check.Should().Be(MemoryCheckBy.After(30));
        MemoryFactsEndpoints.NewFact("s", "a", "v", null, null, null, null, keepSourcesWhenNone: false)
            .Fact!.Check.Should().BeNull("no checkBy means the space's default");
    }

    [Fact]
    public void NewFact_UnreadableCheckBy_IsA400NamingIt()
    {
        var (fact, problem) = MemoryFactsEndpoints.NewFact("s", "a", "v", null, null, null, null, keepSourcesWhenNone: false, checkBy: "0d");

        fact.Should().BeNull();
        ProblemHttpResult result = problem.Should().BeOfType<ProblemHttpResult>().Subject;
        result.StatusCode.Should().Be(400);
        result.ProblemDetails.Extensions["field"].Should().Be("checkBy");
    }

    [Fact]
    public void RecallResponse_ListsEachSpaceOnce_ByName_WithItsAudience()
    {
        DateTime at = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Guid team = Guid.NewGuid(), personal = Guid.NewGuid();
        Dictionary<Guid, Container> spaces = new()
        {
            [team] = new Container(team.ToString(), "zeta-team", null, at, at, Kind: ContainerKind.Memory),
            [personal] = new Container(personal.ToString(), "alpha-mine", null, at, at, Kind: ContainerKind.Memory, Audience: MemoryScope.Personal),
        };
        RecalledFact fact = new(new MemoryFact(Guid.NewGuid(), Guid.NewGuid(), team, "s", "a", "v", null, null, at, null, null, null,
            new MemoryCaller(null, null, null), []), []);

        MemoryRecallResponse response = MemoryFactsEndpoints.RecallResponse(new SpacedRecall([fact], spaces, 3, "next"));

        response.Facts.Should().Equal(fact);
        response.Spaces.Should().Equal(
            new MemorySpaceRef(personal.ToString(), "alpha-mine", MemoryScope.Personal),
            new MemorySpaceRef(team.ToString(), "zeta-team", MemoryScope.Team));
        (response.SkippedSpaces, response.NextCursor).Should().Be((3, "next"));
    }
}
