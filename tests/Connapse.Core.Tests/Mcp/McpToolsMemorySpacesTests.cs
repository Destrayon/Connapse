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
        Kind: ContainerKind.Memory,
        Audience: kind == MemorySpaceKind.Personal ? MemoryScope.Personal : MemoryScope.Team);

    private void Lists(params Container[] spaces)
    {
        _spaces.ListAsync(Arg.Any<MemoryCaller>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(spaces.Select((s, i) => new MemorySpaceSummary(s, 1, new DateTimeOffset(Epoch).AddMinutes(-i), false)).ToList());
        // The spaces list reads candidates whose bounds already meet, so it needs no source check.
        _spaces.CandidatesAsync(Arg.Any<MemoryCaller>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(spaces.Select((s, i) => new MemorySpaceCandidate(s, false, 1, 1,
                new DateTimeOffset(Epoch).AddMinutes(-i), new DateTimeOffset(Epoch).AddMinutes(-i))).ToList());
    }

    private ListMemorySpaces List(IOperationAuthorizer? authorizer = null, IUserDirectory? people = null) =>
        new(_spaces, authorizer ?? McpCaller.Allowed, people);

    private RememberFact Remember() => new(_containers, _memory, McpCaller.Allowed);

    private RecallMemory Recall() => new(_containers, _memory, McpCaller.Allowed, _spaces);

    private static MemoryFact Fact(Guid space, MemoryScope scope, int minute = 0) =>
        new(Guid.NewGuid(), Guid.NewGuid(), space, "Halcyon", "database", "PostgreSQL 15", null, null,
            DateTimeOffset.UnixEpoch.AddMinutes(minute), null, null, null, new MemoryCaller(null, null, "agent"), [], scope);

    // ── memory_space_create ───────────────────────────────────────────────────────────────────────

    private CreateMemorySpace Create(IOperationAuthorizer? authorizer = null) =>
        new(_containers, _spaces, Substitute.For<IAuditLogger>(), authorizer ?? McpCaller.Allowed);

    [Fact]
    public async Task MemorySpaceCreate_Personal_MakesItForTheCallerAndSaysOnlyTheySeeIt()
    {
        Container made = Space("halcyon-notes", MemorySpaceKind.Personal);
        _spaces.CreatePersonalAsync(Arg.Any<MemoryCaller>(), "halcyon-notes", "Halcyon facts", Arg.Any<CancellationToken>()).Returns(made);

        JsonElement created = JsonDocument.Parse(
            await McpTools.MemorySpaceCreate(Create(), List(), Signed, "halcyon-notes", "Personal", "Halcyon facts")).RootElement.GetProperty("created");

        created.GetProperty("id").GetString().Should().Be(made.Id);
        created.GetProperty("kind").GetString().Should().Be("personal");
        created.GetProperty("whoSees").GetString().Should().Be("only you");
        await _containers.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task MemorySpaceCreate_Team_MakesAMemoryContainerEveryoneSees()
    {
        Container made = Space("on-call", MemorySpaceKind.Team);
        _containers.CreateAsync(Arg.Is<CreateContainerRequest>(r => r.Name == "on-call" && r.Kind == ContainerKind.Memory), Arg.Any<CancellationToken>())
            .Returns(made);

        JsonElement created = JsonDocument.Parse(
            await McpTools.MemorySpaceCreate(Create(), List(), Signed, "on-call", "team")).RootElement.GetProperty("created");

        created.GetProperty("kind").GetString().Should().Be("team");
        created.GetProperty("whoSees").GetString().Should().StartWith("everyone in this Connapse");
    }

    [Fact]
    public async Task MemorySpaceCreate_WithoutWriteAccess_IsRefusedAndMakesNothing()
    {
        IOperationAuthorizer readOnly = Substitute.For<IOperationAuthorizer>();
        readOnly.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.KnowledgeRead).Returns(true);

        string result = await McpTools.MemorySpaceCreate(Create(readOnly), List(readOnly), Signed, "on-call", "team");

        result.Should().StartWith("Error:").And.Contain("write access");
        await _containers.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
        await _spaces.DidNotReceiveWithAnyArgs().CreatePersonalAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("")]
    [InlineData("shared")]
    [InlineData("1")]
    public async Task MemorySpaceCreate_UnknownKind_SaysTheTwoKinds(string kind)
    {
        string result = await McpTools.MemorySpaceCreate(Create(), List(), Signed, "on-call", kind);

        result.Should().Be("Error: kind must be \"personal\" or \"team\".");
        await _containers.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    // ── memory_spaces ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MemorySpaces_EverySpace_SaysKindWhetherICanAddAndWhoSeesIt()
    {
        Lists(Space("my-memory", MemorySpaceKind.Personal), Space("on-call", MemorySpaceKind.Team), Space("handbook", MemorySpaceKind.Team));
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
        spaces["handbook"].GetProperty("kind").GetString().Should().Be("team");
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
        await _spaces.Received().CandidatesAsync(Arg.Any<MemoryCaller>(), "call", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("tables")]
    [InlineData("1")]
    public async Task MemorySpaces_UnknownKind_IsRefusedAndReadsNothing(string kind)
    {
        (await McpTools.MemorySpaces(List(), Signed, kind: kind)).Should().Be("Error: kind must be \"personal\" or \"team\".");
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
    [InlineData(MemorySpaceKind.Team, 2, "everyone in this Connapse, 2 people")]
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
        result.Should().EndWith("\nYou have no personal space yet; make one with memory_space_create (kind \"personal\").");
    }

    [Fact]
    public async Task MemoryRemember_NoSpaceAndNoSpacesAtAll_SaysToCreateOne()
    {
        Lists();

        string result = await McpTools.MemoryRemember(Remember(), List(), Signed, "Halcyon", "database", "x");

        result.Should().EndWith(" You have no spaces yet: make one with memory_space_create (kind \"personal\" or \"team\"), then save to it.");
    }

    [Fact]
    public async Task MemoryRemember_NoSpaceAndSpacesCannotBeListed_PointsAtMemorySpaces()
    {
        _spaces.CandidatesAsync(Arg.Any<MemoryCaller>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([new MemorySpaceCandidate(Space("on-call", MemorySpaceKind.Team), false, 0, 1, Epoch, Epoch)]);
        _spaces.TallyAsync(Arg.Any<MemoryCaller>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, MemorySpaceTally>?)null);

        string result = await McpTools.MemoryRemember(Remember(), List(), Signed, "Halcyon", "database", "x");

        result.Should().Be("Error: name the `space` to save to. " + McpTools.SaveTest
            + " Call memory_spaces to see your spaces, or make one with memory_space_create.");
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
        Container docs = Space("handbook", MemorySpaceKind.Team);
        _containers.GetByNameAsync("handbook", Arg.Any<CancellationToken>()).Returns(docs);
        _memory.RememberAsync(Arg.Any<Guid>(), Arg.Any<NewMemoryFact>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(c => new MemoryWriteResult(Fact(c.ArgAt<Guid>(0), MemoryScope.Team), null, null));

        string result = await McpTools.MemoryRemember(Remember(), List(), Signed, "Halcyon", "database", "x",
            sources: [Guid.NewGuid().ToString()], containerId: "handbook");

        JsonDocument.Parse(result).RootElement.GetProperty("savedTo").GetProperty("note").GetString().Should().StartWith(
            "Saved to 'handbook', a team space: everyone in this Connapse can see it");
    }

    [Fact]
    public async Task MemoryRemember_DocumentContainer_IsRefusedAndSavesNothing()
    {
        Container docs = new(Guid.NewGuid().ToString(), "handbook", null, Epoch, Epoch);
        _containers.GetByNameAsync("handbook", Arg.Any<CancellationToken>()).Returns(docs);

        string result = await McpTools.MemoryRemember(Remember(), List(), Signed, "Halcyon", "database", "x", containerId: "handbook");

        result.Should().StartWith("Error: ");
        await _memory.DidNotReceive().RememberAsync(Arg.Any<Guid>(), Arg.Any<NewMemoryFact>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryRecall_DocumentContainer_IsRefusedAndReadsNothing()
    {
        Container docs = new(Guid.NewGuid().ToString(), "handbook", null, Epoch, Epoch);
        _containers.GetByNameAsync("handbook", Arg.Any<CancellationToken>()).Returns(docs);

        string result = await McpTools.MemoryRecall(Recall(), Signed, space: "handbook", detailed: true);

        result.Should().StartWith("Error: ");
        await _memory.DidNotReceive().RecallAsync(Arg.Any<MemoryQuery>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
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
            new MemoryRecall([.. c.Arg<MemoryQuery>().Containers.Select(id =>
                new RecalledFact(Fact(id, id == Guid.Parse(mine.Id) ? MemoryScope.Personal : MemoryScope.Team), []))]));

        JsonElement root = JsonDocument.Parse(await McpTools.MemoryRecall(Recall(), Signed, detailed: true)).RootElement;

        var facts = root.GetProperty("facts").EnumerateArray().ToList();
        facts.Select(f => (f.GetProperty("space").GetProperty("name").GetString(), f.GetProperty("space").GetProperty("kind").GetString(), f.GetProperty("scope").GetString()))
            .Should().BeEquivalentTo([("my-memory", "personal", "personal"), ("on-call", "team", "team")]);
        root.TryGetProperty("note", out _).Should().BeFalse();
    }

    [Fact]
    public async Task MemoryTimeline_ContainerIdAlone_PagesThatSpaceNewestFirstAndShowsEachPageInDateOrder()
    {
        Container docs = Space("handbook", MemorySpaceKind.Team);
        _containers.GetByNameAsync("handbook", Arg.Any<CancellationToken>()).Returns(docs);
        Guid id = Guid.Parse(docs.Id);
        _memory.ListAsync(id, Arg.Any<MemoryFactFilter>(), Arg.Any<MemoryFactSort>(), Arg.Any<MemoryFactKey?>(), Arg.Any<int>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryFactPage([new RecalledFact(Fact(id, MemoryScope.Team, minute: 9), []), new RecalledFact(Fact(id, MemoryScope.Team, minute: 1), [])], null));

        JsonElement root = JsonDocument.Parse(await McpTools.MemoryTimeline(Recall(), Signed, subject: "Halcyon", containerId: "handbook", detailed: true)).RootElement;

        root.GetProperty("facts")[0].GetProperty("space").GetProperty("kind").GetString().Should().Be("team");
        root.GetProperty("facts").EnumerateArray().Select(f => f.GetProperty("recordedAt").GetDateTimeOffset().Minute).Should().Equal(1, 9);
        root.TryGetProperty("nextCursor", out _).Should().BeFalse();
        await _memory.Received(1).ListAsync(id, Arg.Is<MemoryFactFilter>(f => f.Subject == "Halcyon" && f.Status == MemoryFactStatus.All),
            MemoryFactSort.ValidFrom, null, 50, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
        _spaces.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task MemoryTimeline_Window_ReadsTheWindowUnpaged()
    {
        Container team = Space("on-call", MemorySpaceKind.Team);
        _containers.GetByNameAsync("on-call", Arg.Any<CancellationToken>()).Returns(team);
        _memory.RecallAsync(Arg.Any<MemoryQuery>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>()).Returns(new MemoryRecall([]));

        await McpTools.MemoryTimeline(Recall(), Signed, space: "on-call", from: "2026-01-01", detailed: true);

        await _memory.Received(1).RecallAsync(Arg.Is<MemoryQuery>(q => q.ChangedFrom != null && !q.History), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, "2026-01-01", null)]
    [InlineData("on-call", "2026-01-01", null)]
    [InlineData("on-call", null, "database")]
    public async Task MemoryTimeline_CursorWhenItCannotPage_IsRefusedAndReadsNothing(string? space, string? from, string? attribute)
    {
        string result = await McpTools.MemoryTimeline(Recall(), Signed, space: space, from: from, attribute: attribute, cursor: "abc", detailed: true);

        result.Should().Be("Error: cursor works only when you name a `space` and leave out `from`, `to` and `attribute`.");
        _memory.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("on-call", "2026-01-01", null)]
    [InlineData("on-call", null, "database")]
    public async Task MemoryRecall_CursorWhenItCannotPage_IsRefusedAndReadsNothing(string? space, string? asOf, string? attribute)
    {
        string result = await McpTools.MemoryRecall(Recall(), Signed, space: space, asOf: asOf, attribute: attribute, cursor: "abc", detailed: true);

        result.Should().Be("Error: cursor works only when you name a `space` and leave out `asOf` and `attribute`.");
        _memory.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, false, MemoryFactSort.Recorded, MemoryFactStatus.Current)]
    [InlineData("postgres", true, MemoryFactSort.Relevance, MemoryFactStatus.All)]
    public async Task MemoryRecall_NamedSpace_PagesItAndHandsBackTheCursor(string? query, bool history, MemoryFactSort sort, MemoryFactStatus status)
    {
        Guid id = Named("on-call");
        _memory.ListAsync(id, Arg.Any<MemoryFactFilter>(), Arg.Any<MemoryFactSort>(), Arg.Any<MemoryFactKey?>(), Arg.Any<int>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryFactPage([new RecalledFact(Fact(id, MemoryScope.Team), [])], new MemoryFactKey(Guid.NewGuid(), Recorded: DateTimeOffset.UnixEpoch)));

        JsonElement first = JsonDocument.Parse(await McpTools.MemoryRecall(Recall(), Signed, space: "on-call", query: query, includeHistory: history, limit: 500, detailed: true)).RootElement;
        string cursor = first.GetProperty("nextCursor").GetString()!;
        JsonElement second = JsonDocument.Parse(await McpTools.MemoryRecall(Recall(), Signed, space: "on-call", query: query, includeHistory: history, limit: 500, cursor: cursor, detailed: true)).RootElement;

        second.GetProperty("facts").GetArrayLength().Should().Be(1);
        await _memory.Received(1).ListAsync(id, Arg.Is<MemoryFactFilter>(f => f.Text == query && f.Status == status), sort, null, 100, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
        await _memory.Received(1).ListAsync(id, Arg.Any<MemoryFactFilter>(), sort, Arg.Is<MemoryFactKey?>(k => k != null), 100, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
    }

    // ── RecallMemory.PageAsync ────────────────────────────────────────────────────────────────────

    private Task<Attempt<SpacedRecall, MemoryStatus>> Page(string space, MemoryFactListing listing, RecallMemory? recall = null) =>
        (recall ?? Recall()).PageAsync(McpContext(), space, listing);

    private Guid Named(string name)
    {
        Container team = Space(name, MemorySpaceKind.Team);
        _containers.GetByNameAsync(name, Arg.Any<CancellationToken>()).Returns(team);
        return Guid.Parse(team.Id);
    }

    [Fact]
    public async Task PageAsync_NoReadAccess_IsForbiddenAndReadsNothing()
    {
        var paged = await Page("on-call", new MemoryFactListing(new MemoryFactFilter()), new RecallMemory(_containers, _memory, new PolicyAuthorizer(_ => false)));

        paged.Status.Should().Be(MemoryStatus.Forbidden);
        _memory.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task PageAsync_UnknownSpace_IsNotFound()
    {
        var paged = await Page("nowhere", new MemoryFactListing(new MemoryFactFilter()));

        paged.Status.Should().Be(MemoryStatus.ContainerNotFound);
        paged.Detail.Should().Be("Space 'nowhere' not found. memory_spaces lists the ones you can use.");
    }

    [Fact]
    public async Task PageAsync_AmbiguousName_IsRefusedForTheSpaceField()
    {
        _containers.FindByNameAsync("notes", Arg.Any<MemoryCaller?>(), Arg.Any<CancellationToken>())
            .Returns([Space("notes", MemorySpaceKind.Team), Space("notes", MemorySpaceKind.Personal)]);

        var paged = await Page("notes", new MemoryFactListing(new MemoryFactFilter()));

        paged.Status.Should().Be(MemoryStatus.Invalid);
        paged.Field.Should().Be("space");
    }

    [Theory]
    [InlineData(99, MemoryFactSort.Recorded, 10, "status")]
    [InlineData(0, (MemoryFactSort)42, 10, "sort")]
    [InlineData(0, MemoryFactSort.Recorded, 101, "limit")]
    [InlineData(0, MemoryFactSort.Recorded, 0, "limit")]
    public async Task PageAsync_BadListing_IsRefusedForItsFieldAndReadsNothing(int status, MemoryFactSort sort, int limit, string field)
    {
        Named("on-call");

        var paged = await Page("on-call", new MemoryFactListing(new MemoryFactFilter(Status: (MemoryFactStatus)status), sort, Limit: limit));

        paged.Status.Should().Be(MemoryStatus.Invalid);
        paged.Field.Should().Be(field);
        _memory.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task PageAsync_CursorFromAnotherFilter_IsRefused()
    {
        Guid id = Named("on-call");
        _memory.ListAsync(id, Arg.Any<MemoryFactFilter>(), Arg.Any<MemoryFactSort>(), Arg.Any<MemoryFactKey?>(), Arg.Any<int>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryFactPage([], new MemoryFactKey(Guid.NewGuid(), Recorded: DateTimeOffset.UnixEpoch)));
        string cursor = (await Page("on-call", new MemoryFactListing(new MemoryFactFilter(Subject: "Halcyon")))).Result!.NextCursor!;
        _memory.ClearReceivedCalls();

        var paged = await Page("on-call", new MemoryFactListing(new MemoryFactFilter(Subject: "Orion"), Cursor: cursor));

        paged.Status.Should().Be(MemoryStatus.Invalid);
        paged.Field.Should().Be("cursor");
        _memory.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task PageAsync_RelevanceWithoutText_ListsByRecordedAndEndsWithNoCursor()
    {
        Guid id = Named("on-call");
        _memory.ListAsync(id, Arg.Any<MemoryFactFilter>(), Arg.Any<MemoryFactSort>(), Arg.Any<MemoryFactKey?>(), Arg.Any<int>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryFactPage([], null));

        var paged = await Page("on-call", new MemoryFactListing(new MemoryFactFilter(Text: "  "), MemoryFactSort.Relevance));

        paged.Result!.NextCursor.Should().BeNull();
        paged.Result.Spaces.Keys.Should().Equal(id);
        await _memory.Received(1).ListAsync(id, Arg.Any<MemoryFactFilter>(), MemoryFactSort.Recorded, null, 50, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PageAsync_Denied_ShowsNothing()
    {
        Guid id = Named("on-call");
        _memory.ListAsync(id, Arg.Any<MemoryFactFilter>(), Arg.Any<MemoryFactSort>(), Arg.Any<MemoryFactKey?>(), Arg.Any<int>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryFactPage([], null, Denied: true));

        (await Page("on-call", new MemoryFactListing(new MemoryFactFilter()))).Status.Should().Be(MemoryStatus.Unchecked);
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
        string json = await McpTools.MemoryRecall(Recall(), Signed, detailed: true);
        JsonDocument.Parse(json).RootElement.GetProperty("note").GetString().Should().Be(
            "Read the 100 most recently changed spaces; 3 more were not read. Name a `space` to read one of them.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpacesAsync_Unnamed_ReadsASpaceWithNoCurrentFactsOnlyWhenTheReadReachesPastNow(bool history)
    {
        Container ended = Space("team-a", MemorySpaceKind.Team);
        _spaces.ListAsync(Arg.Any<MemoryCaller>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([new MemorySpaceSummary(ended, 0, new DateTimeOffset(Epoch), false)]);
        _memory.RecallAsync(Arg.Any<MemoryQuery>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>()).Returns(new MemoryRecall([]));

        await Recall().SpacesAsync(McpContext(), null, new MemoryQuery(Guid.Empty, History: history));
        await Recall().SpacesAsync(McpContext(), null, new MemoryQuery(Guid.Empty, AsOf: history ? DateTimeOffset.UnixEpoch : null));

        await _memory.Received(history ? 2 : 0).RecallAsync(Arg.Any<MemoryQuery>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
    }

    private static OperationContext McpContext() => new(Signed.HttpContext!.User, OperationSurface.Mcp);
}
