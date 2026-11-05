using System.Security.Claims;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Memory;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Application;
using Connapse.Tests.Support;
using Connapse.Web.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Connapse.Core.Tests.Mcp;

/// <summary>
/// CON-237: agents list their memory spaces, must name one to save, read one or all of them, and are told in words who
/// sees each fact.
/// </summary>
[Trait("Category", "Unit")]
public class McpToolsMemorySpacesTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IMemorySpaceStore _spaces = Substitute.For<IMemorySpaceStore>();
    private readonly IContainerStore _containers = ContainerStoreFakes.Create();
    private readonly IMemoryService _memory = Substitute.For<IMemoryService>();

    private static IHttpContextAccessor Signed => new HttpContextAccessor
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test")),
        },
    };

    private static Container Space(string name, MemorySpaceKind kind) => new(Guid.NewGuid().ToString(), name, $"{name} notes", Epoch, Epoch,
        Kind: kind == MemorySpaceKind.Container ? ContainerKind.Documents : ContainerKind.Memory,
        Audience: kind == MemorySpaceKind.Personal ? MemoryScope.Personal : MemoryScope.Team);

    private void Lists(params Container[] spaces) =>
        _spaces.ListAsync(Arg.Any<MemoryCaller>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(spaces.Select((s, i) => new MemorySpaceSummary(s, 1, new DateTimeOffset(Epoch).AddMinutes(-i), false)).ToList());

    private ListMemorySpaces List(IOperationAuthorizer? authorizer = null, IUserDirectory? people = null) =>
        new(_spaces, authorizer ?? McpCaller.Allowed, people);

    private RememberFact Remember() => new(_containers, _memory, McpCaller.Allowed);

    private RecallMemory Recall() => new(_containers, _memory, McpCaller.Allowed, _spaces);

    private static MemoryFact Fact(Guid space, MemoryScope scope, int minute = 0) =>
        new(Guid.NewGuid(), Guid.NewGuid(), space, "Halcyon", "database", "PostgreSQL 15", null, null,
            DateTimeOffset.UnixEpoch.AddMinutes(minute), null, null, null, new MemoryCaller(null, null, "agent"), [], scope);

    // ── memory_spaces ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MemorySpaces_EverySpace_SaysKindWhetherICanAddAndWhoSeesIt()
    {
        Lists(Space("my-memory", MemorySpaceKind.Personal), Space("on-call", MemorySpaceKind.Team), Space("handbook", MemorySpaceKind.Container));
        IUserDirectory people = Substitute.For<IUserDirectory>();
        people.ListAsync(0, 0, Arg.Any<CancellationToken>()).Returns(new DirectoryPage([], 14));

        JsonElement root = JsonDocument.Parse(await McpTools.MemorySpaces(List(people: people), Signed)).RootElement;

        root.GetProperty("total").GetInt32().Should().Be(3);
        root.TryGetProperty("note", out _).Should().BeFalse();
        root.TryGetProperty("canAddNote", out _).Should().BeFalse();
        var spaces = root.GetProperty("spaces").EnumerateArray().ToDictionary(s => s.GetProperty("name").GetString()!);
        spaces["my-memory"].GetProperty("kind").GetString().Should().Be("personal");
        spaces["my-memory"].GetProperty("whoSees").GetString().Should().Be("only you");
        spaces["on-call"].GetProperty("kind").GetString().Should().Be("team");
        spaces["on-call"].GetProperty("whoSees").GetString().Should().Be("everyone in this Connapse, 14 people");
        spaces["handbook"].GetProperty("kind").GetString().Should().Be("container");
        spaces["handbook"].GetProperty("description").GetString().Should().Be("handbook notes");
        spaces.Values.Should().OnlyContain(s => s.GetProperty("canAdd").GetBoolean());
    }

    [Fact]
    public async Task MemorySpaces_ReadOnlyCaller_CannotAddAndIsToldWhy()
    {
        Lists(Space("on-call", MemorySpaceKind.Team));
        var readOnly = new PolicyAuthorizer(p => p == Policies.KnowledgeRead);

        JsonElement root = JsonDocument.Parse(await McpTools.MemorySpaces(List(readOnly), Signed)).RootElement;

        root.GetProperty("spaces")[0].GetProperty("canAdd").GetBoolean().Should().BeFalse();
        root.GetProperty("spaces")[0].GetProperty("whoSees").GetString().Should().Be("everyone in this Connapse");
        root.GetProperty("canAddNote").GetString().Should().Contain("write access");
    }

    [Fact]
    public async Task MemorySpaces_MoreThanAHundred_ReturnsAHundredAndSaysHowToFindTheRest()
    {
        Lists([.. Enumerable.Range(0, 130).Select(i => Space($"team-{i:000}", MemorySpaceKind.Team))]);

        JsonElement root = JsonDocument.Parse(await McpTools.MemorySpaces(List(), Signed)).RootElement;

        root.GetProperty("spaces").GetArrayLength().Should().Be(ListMemorySpaces.MaxLimit);
        root.GetProperty("total").GetInt32().Should().Be(130);
        root.GetProperty("note").GetString().Should().Be("Showing 100 of 130. Pass `name` to find the others.");
    }

    [Fact]
    public async Task MemorySpaces_NameAndKind_FilterTheList()
    {
        Lists(Space("on-call", MemorySpaceKind.Team), Space("my-memory", MemorySpaceKind.Personal));

        JsonElement root = JsonDocument.Parse(await McpTools.MemorySpaces(List(), Signed, name: " call ", kind: "Team")).RootElement;

        root.GetProperty("spaces").EnumerateArray().Select(s => s.GetProperty("name").GetString()).Should().Equal("on-call");
        await _spaces.Received().ListAsync(Arg.Any<MemoryCaller>(), "call", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("tables")]
    [InlineData("1")]
    public async Task MemorySpaces_UnknownKind_IsRefusedAndReadsNothing(string kind)
    {
        (await McpTools.MemorySpaces(List(), Signed, kind: kind)).Should().Be("Error: kind must be \"personal\", \"team\" or \"container\".");
        _spaces.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task MemorySpaces_NoReadAccess_IsRefused()
    {
        string result = await McpTools.MemorySpaces(List(new PolicyAuthorizer(_ => false)), Signed);

        result.Should().StartWith("Error: ");
        _spaces.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(MemorySpaceKind.Personal, 5, "only you")]
    [InlineData(MemorySpaceKind.Team, 1, "everyone in this Connapse, 1 person")]
    [InlineData(MemorySpaceKind.Container, 2, "everyone in this Connapse, 2 people")]
    public void WhoSees_EachKind_IsPlainWords(MemorySpaceKind kind, int accounts, string expected) =>
        McpTools.WhoSees(kind, accounts).Should().Be(expected);

    // ── memory_remember ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MemoryRemember_NoSpace_ListsMySpacesWithTheSaveTestAndSavesNothing()
    {
        Container team = Space("on-call", MemorySpaceKind.Team);
        Lists(Space("my-memory", MemorySpaceKind.Personal), team);

        string result = await McpTools.MemoryRemember(Remember(), List(), Signed, "Halcyon", "database", "PostgreSQL 15");

        result.Should().StartWith("Error: name the `space` to save to. " + McpTools.SaveTest + " Your spaces:");
        result.Should().Contain($"\n- on-call (team: everyone in this Connapse sees it, id {team.Id})");
        result.Should().Contain("\n- my-memory (personal: only you see it, id ");
        result.Should().NotContain("more (memory_spaces").And.NotContain("no personal space");
        _memory.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task MemoryRemember_NoSpaceAndManySpaces_NamesTwentyAndCountsTheRest()
    {
        Lists([.. Enumerable.Range(0, 25).Select(i => Space($"team-{i:00}", MemorySpaceKind.Team))]);

        string result = await McpTools.MemoryRemember(Remember(), List(), Signed, "Halcyon", "database", "x", space: "  ");

        result.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal)).Should().Be(McpTools.SpacesNamedInError);
        result.Should().Contain("\n...and 5 more (memory_spaces lists them).");
        result.Should().EndWith("\nYou have no personal space yet; save to \"my-memory\" to make one.");
    }

    [Fact]
    public async Task MemoryRemember_NoSpaceAndNoSpacesAtAll_SaysToSaveToMyMemory()
    {
        Lists();

        string result = await McpTools.MemoryRemember(Remember(), List(), Signed, "Halcyon", "database", "x");

        result.Should().EndWith(" You have no spaces yet; save to \"my-memory\" to make your personal one.");
    }

    [Fact]
    public async Task MemoryRemember_NoSpaceAndSpacesCannotBeListed_PointsAtMemorySpaces()
    {
        _spaces.ListAsync(Arg.Any<MemoryCaller>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns((IReadOnlyList<MemorySpaceSummary>?)null);

        string result = await McpTools.MemoryRemember(Remember(), List(), Signed, "Halcyon", "database", "x");

        result.Should().Be("Error: name the `space` to save to. " + McpTools.SaveTest
            + " Call memory_spaces to see your spaces, or save to \"my-memory\", your personal space.");
    }

    [Fact]
    public async Task MemoryRemember_SpaceAndContainerId_SavesToTheSpace()
    {
        Container team = Space("on-call", MemorySpaceKind.Team);
        _containers.GetByNameAsync("on-call", Arg.Any<CancellationToken>()).Returns(team);
        _memory.RememberAsync(Arg.Any<Guid>(), Arg.Any<NewMemoryFact>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(c => new MemoryWriteResult(Fact(c.ArgAt<Guid>(0), MemoryScope.Team), null, null));

        string result = await McpTools.MemoryRemember(Remember(), List(), Signed, "Halcyon", "database", "x",
            space: "on-call", containerId: "elsewhere");

        JsonElement saved = JsonDocument.Parse(result).RootElement;
        saved.GetProperty("savedTo").GetProperty("note").GetString().Should().Be(
            "Saved to 'on-call', a team space: everyone in this Connapse can see it, except that a fact citing documents is shown only to people who can read all of them.");
        saved.GetProperty("remembered").GetProperty("scope").GetString().Should().Be("team");
        await _memory.Received(1).RememberAsync(Guid.Parse(team.Id), Arg.Any<NewMemoryFact>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
        await _containers.DidNotReceive().GetByNameAsync("elsewhere", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryRemember_OnlyContainerId_SavesTheOldWay()
    {
        Container docs = Space("handbook", MemorySpaceKind.Container);
        _containers.GetByNameAsync("handbook", Arg.Any<CancellationToken>()).Returns(docs);
        _memory.RememberAsync(Arg.Any<Guid>(), Arg.Any<NewMemoryFact>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(c => new MemoryWriteResult(Fact(c.ArgAt<Guid>(0), MemoryScope.Team), null, null));

        string result = await McpTools.MemoryRemember(Remember(), List(), Signed, "Halcyon", "database", "x",
            sources: [Guid.NewGuid().ToString()], containerId: "handbook");

        JsonDocument.Parse(result).RootElement.GetProperty("savedTo").GetProperty("note").GetString().Should().StartWith(
            "Saved to 'handbook', a document container: everyone in this Connapse can see it");
    }

    [Fact]
    public void SavedTo_PersonalSpace_SaysOnlyYouSeeIt() =>
        JsonDocument.Parse(JsonSerializer.Serialize(McpTools.SavedTo(Space("my-memory", MemorySpaceKind.Personal))))
            .RootElement.GetProperty("note").GetString().Should().Be("Saved to 'my-memory': only you see it. Recall it from 'my-memory'.");

    // ── memory_recall and memory_timeline ─────────────────────────────────────────────────────────

    [Fact]
    public async Task MemoryRecall_NoSpace_ReadsEverySpaceAndLabelsEachFact()
    {
        Container mine = Space("my-memory", MemorySpaceKind.Personal);
        Container team = Space("on-call", MemorySpaceKind.Team);
        Lists(mine, team);
        _memory.RecallAsync(Arg.Any<MemoryQuery>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            Guid id = c.Arg<MemoryQuery>().ContainerId;
            return new MemoryRecall([new RecalledFact(Fact(id, id == Guid.Parse(mine.Id) ? MemoryScope.Personal : MemoryScope.Team), [])]);
        });

        JsonElement root = JsonDocument.Parse(await McpTools.MemoryRecall(Recall(), Signed)).RootElement;

        var facts = root.GetProperty("facts").EnumerateArray().ToList();
        facts.Select(f => (f.GetProperty("space").GetProperty("name").GetString(), f.GetProperty("space").GetProperty("kind").GetString(), f.GetProperty("scope").GetString()))
            .Should().BeEquivalentTo([("my-memory", "personal", "personal"), ("on-call", "team", "team")]);
        root.TryGetProperty("note", out _).Should().BeFalse();
    }

    [Fact]
    public async Task MemoryTimeline_ContainerIdAlone_ReadsThatSpace()
    {
        Container docs = Space("handbook", MemorySpaceKind.Container);
        _containers.GetByNameAsync("handbook", Arg.Any<CancellationToken>()).Returns(docs);
        _memory.RecallAsync(Arg.Any<MemoryQuery>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryRecall([new RecalledFact(Fact(Guid.Parse(docs.Id), MemoryScope.Team), [])]));

        JsonElement root = JsonDocument.Parse(await McpTools.MemoryTimeline(Recall(), Signed, subject: "Halcyon", containerId: "handbook")).RootElement;

        root.GetProperty("facts")[0].GetProperty("space").GetProperty("kind").GetString().Should().Be("container");
        await _memory.Received(1).RecallAsync(Arg.Is<MemoryQuery>(q => q.ContainerId == Guid.Parse(docs.Id) && q.History),
            Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
        _spaces.ReceivedCalls().Should().BeEmpty();
    }

    // ── RecallMemory.SpacesAsync, the branches the random-input test doesn't reach ────────────────

    [Fact]
    public async Task SpacesAsync_UnknownSpace_IsNotFoundAndReadsNothing()
    {
        var recalled = await Recall().SpacesAsync(McpContext(), "nowhere", new MemoryQuery(Guid.Empty));

        recalled.Status.Should().Be(MemoryStatus.ContainerNotFound);
        recalled.Detail.Should().Be("Space 'nowhere' not found. memory_spaces lists the ones you can use.");
        _memory.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SpacesAsync_AmbiguousName_IsRefusedForTheSpaceField()
    {
        _containers.FindByNameAsync("notes", Arg.Any<MemoryCaller?>(), Arg.Any<CancellationToken>())
            .Returns([Space("notes", MemorySpaceKind.Team), Space("notes", MemorySpaceKind.Personal)]);

        var recalled = await Recall().SpacesAsync(McpContext(), "notes", new MemoryQuery(Guid.Empty));

        recalled.Status.Should().Be(MemoryStatus.Invalid);
        recalled.Field.Should().Be("space");
        _memory.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SpacesAsync_NamedSpaceUnchecked_ShowsNothing()
    {
        Container team = Space("on-call", MemorySpaceKind.Team);
        _containers.GetByNameAsync("on-call", Arg.Any<CancellationToken>()).Returns(team);
        _memory.RecallAsync(Arg.Any<MemoryQuery>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>()).Returns(MemoryRecall.DeniedResult);

        (await Recall().SpacesAsync(McpContext(), "on-call", new MemoryQuery(Guid.Empty))).Status.Should().Be(MemoryStatus.Unchecked);
    }

    [Fact]
    public async Task SpacesAsync_NoSpaceAndNoSpaceStore_AsksForASpace()
    {
        var recalled = await new RecallMemory(_containers, _memory, McpCaller.Allowed).SpacesAsync(McpContext(), null, new MemoryQuery(Guid.Empty));

        recalled.Status.Should().Be(MemoryStatus.Invalid);
        recalled.Field.Should().Be("space");
    }

    [Fact]
    public async Task SpacesAsync_MoreSpacesThanItReads_ReadsTheNewestAndCountsTheRest()
    {
        Lists([.. Enumerable.Range(0, RecallMemory.MaxSpaces + 3).Select(i => Space($"team-{i:000}", MemorySpaceKind.Team))]);
        _memory.RecallAsync(Arg.Any<MemoryQuery>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>()).Returns(new MemoryRecall([]));

        var recalled = await Recall().SpacesAsync(McpContext(), null, new MemoryQuery(Guid.Empty));

        recalled.Result!.SkippedSpaces.Should().Be(3);
        recalled.Result.Spaces.Values.Select(s => s.Name).Should().NotContain(["team-100", "team-101", "team-102"]);
        string json = await McpTools.MemoryRecall(Recall(), Signed);
        JsonDocument.Parse(json).RootElement.GetProperty("note").GetString().Should().Be(
            "Read the 100 most recently changed spaces; 3 more were not read. Name a `space` to read one of them.");
    }

    [Fact]
    public void Merge_Ranked_InterleavesEachSpacesBestFirstUpToTheLimit()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        RecalledFact a1 = new(Fact(a, MemoryScope.Team), []), a2 = new(Fact(a, MemoryScope.Team), []), b1 = new(Fact(b, MemoryScope.Team), []);

        SpacedRecall.Merge([[a1, a2], [b1]], ranked: true, limit: 2).Should().Equal(a1, b1);
        SpacedRecall.Merge([[a1, a2], [b1]], ranked: true, limit: -1).Should().BeEmpty();
        SpacedRecall.Merge([], ranked: true, limit: 5).Should().BeEmpty();
    }

    private static OperationContext McpContext() => new(Signed.HttpContext!.User, OperationSurface.Mcp);
}
