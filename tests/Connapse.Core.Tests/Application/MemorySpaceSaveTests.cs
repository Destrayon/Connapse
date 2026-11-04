using System.Security.Claims;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Memory;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Mcp;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// CON-235: what a save tells the caller. Personal memory refused on a team space or document container names the
/// caller's own spaces, or <c>my-memory</c> when they have none; a name that is both a team container and the caller's
/// own space is refused rather than guessed; an old-style save without sources says which personal space it went to.
/// </summary>
[Trait("Category", "Unit")]
public class MemorySpaceSaveTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly OperationContext Alice = new(new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "test")), OperationSurface.Mcp);

    private readonly IContainerStore _containers = ContainerStoreFakes.Create();
    private readonly IMemoryService _memory = Substitute.For<IMemoryService>();
    private readonly RememberFact _remember;

    public MemorySpaceSaveTests()
    {
        _remember = new RememberFact(_containers, _memory, new PolicyAuthorizer(_ => true));
        _memory.RememberAsync(Arg.Any<Guid>(), Arg.Any<NewMemoryFact>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(call => new MemoryWriteResult(new MemoryFact(Guid.NewGuid(), Guid.NewGuid(), call.ArgAt<Guid>(0), "Halcyon", "database",
                "PostgreSQL 15", null, null, DateTimeOffset.UnixEpoch, null, null, null, call.ArgAt<MemoryCaller>(2), [],
                call.ArgAt<NewMemoryFact>(1).Scope ?? MemoryScope.Personal), null, null));
        _containers.EnsurePersonalSpaceAsync(Arg.Any<MemoryCaller>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Space(call.ArgAt<string>(1), MemoryScope.Personal));
    }

    private static Container Space(string name, MemoryScope audience, ContainerKind kind = ContainerKind.Memory) =>
        new(Guid.NewGuid().ToString(), name, null, Epoch, Epoch, Kind: kind, Audience: audience);

    private static NewMemoryFact Fact(MemoryScope? scope = null, bool cites = false) =>
        new("Halcyon", "database", "PostgreSQL 15", null, null,
            cites ? [new MemorySource(MemorySourceKind.Document, Guid.NewGuid().ToString())] : [], null, scope);

    private void Have(params Container[] spaces)
    {
        foreach (Container space in spaces)
        {
            _containers.GetAsync(Guid.Parse(space.Id), Arg.Any<MemoryCaller?>(), Arg.Any<CancellationToken>()).Returns(space);
            _containers.FindByNameAsync(space.Name, Arg.Any<MemoryCaller?>(), Arg.Any<CancellationToken>())
                .Returns(spaces.Where(s => s.Name == space.Name).OrderBy(s => s.Audience == MemoryScope.Personal).ToList());
        }
        _containers.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<MemoryCaller?>(), Arg.Any<CancellationToken>()).Returns(spaces);
    }

    [Fact]
    public async Task Remember_PersonalOnDocumentContainer_IsRefusedNamingTheCallersSpaces()
    {
        Container docs = Space("handbook", MemoryScope.Team, ContainerKind.Documents);
        Have(docs, Space("my-memory", MemoryScope.Personal), Space("drafts", MemoryScope.Personal), Space("team-notes", MemoryScope.Team));

        var written = await _remember.RunAsync(Alice, MemoryTarget.Space("handbook"), Fact(MemoryScope.Personal));

        written.Status.Should().Be(MemoryStatus.Invalid);
        written.Field.Should().Be("scope");
        written.Detail.Should().Contain("document container").And.Contain("my-memory, drafts").And.NotContain("team-notes");
        _memory.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Remember_PersonalOnTeamSpaceWithNoSpacesOfYourOwn_SuggestsMyMemory()
    {
        Have(Space("team-notes", MemoryScope.Team));

        var written = await _remember.RunAsync(Alice, MemoryTarget.Space("team-notes"), Fact(MemoryScope.Personal));

        written.Status.Should().Be(MemoryStatus.Invalid);
        written.Detail.Should().Contain("a team space").And.Contain("\"my-memory\"");
    }

    [Fact]
    public async Task Remember_TeamIntoPersonalSpace_IsRefused()
    {
        Container mine = Space("drafts", MemoryScope.Personal);
        Have(mine);

        var bySpace = await _remember.RunAsync(Alice, MemoryTarget.Space(mine.Id), Fact(MemoryScope.Team, cites: true));
        var byContainer = await _remember.RunAsync(Alice, MemoryTarget.Container(mine.Id), Fact(MemoryScope.Team, cites: true));

        bySpace.Detail.Should().Contain("'drafts' is a personal space");
        byContainer.Detail.Should().Contain("'drafts' is a personal space");
        _memory.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Remember_NameOfTeamContainerAndYourOwnSpace_IsRefusedWithBothIds()
    {
        Container team = Space("notes", MemoryScope.Team);
        Container mine = Space("notes", MemoryScope.Personal);
        Have(team, mine);

        var written = await _remember.RunAsync(Alice, MemoryTarget.Space("notes"), Fact());

        written.Status.Should().Be(MemoryStatus.Invalid);
        written.Field.Should().Be("space");
        written.Detail.Should().Contain(team.Id).And.Contain(mine.Id);
        (await _remember.RunAsync(Alice, MemoryTarget.Space(mine.Id), Fact())).Succeeded.Should().BeTrue("the id says which one");
    }

    [Fact]
    public async Task Remember_OldStyleWithoutSources_GoesToThePersonalSpaceForThatContainer()
    {
        Container docs = Space("handbook", MemoryScope.Team, ContainerKind.Documents);
        Have(docs);

        var written = await _remember.RunAsync(Alice, docs.Id, Fact());

        written.Succeeded.Should().BeTrue();
        written.Result!.SavedTo!.Name.Should().Be("handbook-personal");
        written.Result.SavedTo.Audience.Should().Be(MemoryScope.Personal);
        await _containers.Received(1).EnsurePersonalSpaceAsync(
            Arg.Is<MemoryCaller>(c => c.UserId == UserId), "handbook-personal", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _memory.Received(1).RememberAsync(Guid.Parse(written.Result.SavedTo.Id),
            Arg.Is<NewMemoryFact>(f => f.Scope == MemoryScope.Personal), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remember_OldStyleOnALongName_CutsItToAValidPersonalName()
    {
        string longName = new string('a', 118) + "-bcdefghi";
        Have(Space(longName, MemoryScope.Team));

        var written = await _remember.RunAsync(Alice, longName, Fact());

        string expected = new string('a', 118) + "-personal";
        written.Result!.SavedTo!.Name.Should().Be(expected);
        expected.Length.Should().BeLessThanOrEqualTo(128);
    }

    [Fact]
    public async Task Remember_OldStyleTeamWithoutSources_NeedsASpace()
    {
        Have(Space("team-notes", MemoryScope.Team));

        var written = await _remember.RunAsync(Alice, "team-notes", Fact(MemoryScope.Team));

        written.Status.Should().Be(MemoryStatus.Invalid);
        written.Detail.Should().Contain("`space`");
    }

    [Fact]
    public async Task Remember_MyMemoryForACallerWithNoId_IsForbiddenAndMakesNothing()
    {
        var anonymous = new OperationContext(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Mcp);

        var written = await _remember.RunAsync(anonymous, MemoryTarget.Space("my-memory"), Fact());

        written.Status.Should().Be(MemoryStatus.Forbidden);
        await _containers.DidNotReceiveWithAnyArgs().EnsurePersonalSpaceAsync(default!, default!, default);
    }

    [Fact]
    public async Task Recall_NameOfTeamContainerAndYourOwnSpace_IsRefused()
    {
        Have(Space("notes", MemoryScope.Team), Space("notes", MemoryScope.Personal));
        var recall = new RecallMemory(_containers, _memory, new PolicyAuthorizer(_ => true));

        var recalled = await recall.RunAsync(Alice, "notes", new MemoryQuery(Guid.Empty));

        recalled.Status.Should().Be(MemoryStatus.Invalid);
        _memory.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void SavedTo_PersonalSpace_SaysOnlyYouSeeIt()
    {
        static string Note(Container space) =>
            JsonSerializer.SerializeToElement(McpTools.SavedTo(space)).GetProperty("note").GetString()!;

        Note(Space("handbook-personal", MemoryScope.Personal)).Should().Contain("only you can see it").And.Contain("handbook-personal");
        Note(Space("team-notes", MemoryScope.Team)).Should().Be("Saved to 'team-notes'.");
        McpTools.SavedTo(null).Should().BeNull();
    }
}
