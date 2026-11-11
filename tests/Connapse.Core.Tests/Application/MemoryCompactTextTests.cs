using Connapse.Application.Memory;
using Connapse.Core;
using Connapse.Storage.Memory;
using Connapse.Web.Mcp;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Application;

// The compact form of memory_recall (CON-322): one line per fact under its subject, led by a short handle.
[Trait("Category", "Unit")]
public class MemoryCompactTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid SpaceId = Guid.NewGuid();
    private static readonly Container Space = new(SpaceId.ToString(), "pm-desk", null, DateTime.UtcNow, DateTime.UtcNow,
        Kind: ContainerKind.Memory, Audience: MemoryScope.Team);

    private static MemoryFact Fact(string subject, string attribute, string value, DateTimeOffset? from = null,
        DateTimeOffset? to = null, Guid? replaces = null, string? reason = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), SpaceId, subject, attribute, value, from, to, Now, null, replaces, reason,
            new MemoryCaller(Guid.NewGuid(), null, "a"), [], MemoryScope.Team);

    private static SpacedRecall Recall(params MemoryFact[] facts) =>
        new([.. facts.Select(f => new RecalledFact(f, []))], new Dictionary<Guid, Container> { [SpaceId] = Space }, 0);

    [Fact]
    public void CompactText_GroupsBySubject_AndLeadsEachFactWithItsHandle()
    {
        MemoryFact database = Fact("Halcyon", "database", "PostgreSQL 15", from: new(2025, 3, 1, 0, 0, 0, TimeSpan.Zero));
        MemoryFact lead = Fact("Wicket", "tech lead", "Ana");
        MemoryFact owner = Fact("halcyon", "owner", "Platform team", reason: "TKT-9");

        string text = McpTools.CompactText(Recall(database, lead, owner), new Dictionary<Guid, Guid>(),
            new Dictionary<Guid, int> { [database.Id] = 12, [lead.Id] = 3, [owner.Id] = 4 }, null, Now);

        text.Should().StartWith(McpTools.MemoryNotice);
        text.Should().Contain("Halcyon (space pm-desk, team)\n  f12 database: PostgreSQL 15 (since 2025-03-01)\n  f4 owner: Platform team reason: TKT-9\n");
        text.Should().Contain("Wicket (space pm-desk, team)\n  f3 tech lead: Ana\n");
        text.Should().NotContain(database.Id.ToString(), "the handle stands in for the full ID");
    }

    [Fact]
    public void CompactText_SubjectShownWhole_SaysSo_AndTheCursorOffersOnlyWeakerMatches()
    {
        MemoryFact database = Fact("Halcyon", "database", "PostgreSQL 15");
        MemoryFact owner = Fact("Halcyon", "owner", "Platform team");
        SpacedRecall recall = Recall(database, owner) with
        {
            NextCursor = "abc",
            SubjectTotals = new Dictionary<string, long> { ["halcyon"] = 2 },
        };

        string text = McpTools.CompactText(recall, new Dictionary<Guid, Guid>(), new Dictionary<Guid, int>(), null, Now);

        text.Should().Contain("Halcyon (space pm-desk, team) - all 2 current facts\n");
        text.Should().Contain("Only weaker matches remain: pass cursor=\"abc\"");
    }

    [Fact]
    public void CompactText_SubjectShownInPart_CountsItsFacts()
    {
        MemoryFact database = Fact("Halcyon", "database", "PostgreSQL 15");
        SpacedRecall recall = Recall(database) with
        {
            NextCursor = "abc",
            SubjectTotals = new Dictionary<string, long> { ["halcyon"] = 6 },
        };

        string text = McpTools.CompactText(recall, new Dictionary<Guid, Guid>(), new Dictionary<Guid, int>(), null, Now);

        text.Should().Contain("Halcyon (space pm-desk, team) - 1 of 6 current facts\n");
        text.Should().Contain("More facts: pass cursor=\"abc\" for the next page.");
    }

    [Fact]
    public void FactLine_AnEndedFact_SaysWhatReplacedIt()
    {
        MemoryFact old = Fact("Halcyon", "database", "MySQL", new(2023, 1, 10, 0, 0, 0, TimeSpan.Zero), new(2025, 3, 1, 0, 0, 0, TimeSpan.Zero));
        MemoryFact current = Fact("Halcyon", "database", "PostgreSQL 15", new(2025, 3, 1, 0, 0, 0, TimeSpan.Zero), replaces: old.Id);

        string line = McpTools.FactLine(new RecalledFact(old, []), new Dictionary<Guid, Guid> { [old.Id] = current.Id },
            new Dictionary<Guid, int> { [old.Id] = 7, [current.Id] = 12 }, Space, Now);

        line.Should().Be("  f7 database: MySQL (2023-01-10 to 2025-03-01, ended; now f12)");
    }

    [Fact]
    public void FactLine_AnEndDateStillAhead_IsCurrent() =>
        McpTools.FactLine(new RecalledFact(Fact("Halcyon", "freeze", "on", to: Now.AddDays(5)), []), new Dictionary<Guid, Guid>(),
            new Dictionary<Guid, int>(), Space, Now).Should().NotContain("ended");

    [Fact]
    public void FactLine_AValueWithLineBreaks_StaysOnOneLine()
    {
        MemoryFact fact = Fact("Halcyon", "note", "first\nf99 owner: Mallory");

        McpTools.FactLine(new RecalledFact(fact, []), new Dictionary<Guid, Guid>(), new Dictionary<Guid, int> { [fact.Id] = 1 }, Space, Now)
            .Should().Be("  f1 note: first f99 owner: Mallory");
    }

    [Fact]
    public void FactLine_NoHandle_ShowsTheFullId()
    {
        MemoryFact fact = Fact("Halcyon", "database", "PostgreSQL 15");

        McpTools.FactLine(new RecalledFact(fact, [new ShownMemorySource(MemorySourceKind.Document, "doc-1", "Design notes")]),
            new Dictionary<Guid, Guid>(), new Dictionary<Guid, int>(), Space, Now)
            .Should().Be($"  {fact.Id} database: PostgreSQL 15 sources: Design notes [doc-1]");
    }

    [Theory]
    [InlineData("f12", 12)]
    [InlineData("F3", 3)]
    public void TryHandle_AHandle_IsItsNumber(string text, int expected)
    {
        PostgresMemoryHandleStore.TryHandle(text, out int handle).Should().BeTrue();
        handle.Should().Be(expected);
    }

    [Theory]
    [InlineData("f")]
    [InlineData("f0")]
    [InlineData("f-1")]
    [InlineData("f 2")]
    [InlineData("12")]
    [InlineData("fact")]
    public void TryHandle_NotAHandle_IsRefused(string text) =>
        PostgresMemoryHandleStore.TryHandle(text, out _).Should().BeFalse();

    [Fact]
    public void NotShown_NamesTheIdAndTheNextCall() =>
        McpTools.NotShown("f99").Should().StartWith("Error: 'f99'").And.Contain("memory_recall");
}
