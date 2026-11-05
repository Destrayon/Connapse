using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Storage.Tests.Memory;

/// <summary>
/// Personal and team facts (CON-234): a personal fact is seen only by its author whatever it cites, a team fact by
/// everyone who can read its sources; and a correction never widens who sees a fact.
/// </summary>
[Trait("Category", "Unit")]
public class MemoryServiceScopeTests
{
    private static readonly Guid Container = Guid.NewGuid();
    private static readonly MemoryCaller Me = new(Guid.NewGuid(), null, null);
    private static readonly MemoryCaller Other = new(Guid.NewGuid(), null, null);
    private static readonly MemorySource Shared = new(MemorySourceKind.Document, "shared");
    private static readonly MemorySource AlsoShared = new(MemorySourceKind.Document, "also-shared");
    private static readonly MemorySource Restricted = new(MemorySourceKind.Document, "restricted");

    private readonly IMemoryStore _store = Substitute.For<IMemoryStore>();
    private readonly IDocumentAccess _access = Substitute.For<IDocumentAccess>();
    private readonly ILinkIndex _links = Substitute.For<ILinkIndex>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly MemoryService _service;

    public MemoryServiceScopeTests()
    {
        _links.DocumentsOfAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(string, string)>().ToLookup(p => p.Item1, p => p.Item2));
        _access.ReadableAsync(Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlySet<string>)ci.ArgAt<IReadOnlyCollection<string>>(1).Where(d => d != Restricted.Ref).ToHashSet());
        _service = new MemoryService(_store, _access, _links, _audit, NullLogger<MemoryService>.Instance);
    }

    private static MemoryFact Fact(MemoryCaller author, MemoryScope scope, params MemorySource[] sources)
    {
        Guid id = Guid.NewGuid();
        return new MemoryFact(id, id, Container, "Halcyon", "database", "x", null, null, DateTimeOffset.UtcNow, null, null, null, author, sources, scope);
    }

    private static NewMemoryFact Replacement(IReadOnlyList<MemorySource>? sources, MemoryScope? scope = null) =>
        new("", "", "y", null, null, sources, null, scope);

    /// <summary>Makes the store run the check it is given against <paramref name="chain"/>, then answer <paramref name="written"/>.</summary>
    private void CorrectionRunsCheck(MemoryFact current, IReadOnlyList<MemoryFact> chain, MemoryFact written)
    {
        _store.GetAsync(current.Id, Arg.Any<CancellationToken>()).Returns(current);
        _store.CorrectAsync(current.Id, MemoryCorrection.Wrong, Arg.Any<NewMemoryFact>(), Arg.Any<MemoryCaller>(),
                Arg.Any<Func<IReadOnlyList<MemoryFact>, Task<MemoryWriteResult?>>>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await ci.ArgAt<Func<IReadOnlyList<MemoryFact>, Task<MemoryWriteResult?>>>(4)(chain)
                ?? new MemoryWriteResult(written, current, null));
    }

    // ── who is the author ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("u1", null, "u1", null, true)]
    [InlineData("u1", "a1", "u1", "a2", true)]
    [InlineData("u1", null, "u2", null, false)]
    [InlineData(null, "a1", null, "a1", true)]
    [InlineData(null, "a1", "u1", "a1", false)]
    [InlineData(null, "a1", null, "a2", false)]
    [InlineData(null, null, null, null, false)]
    [InlineData("u1", null, null, null, false)]
    public void IsAuthorOf_MatchesTheUserOrElseAUserlessAgent(string? authorUser, string? authorAgent, string? callerUser, string? callerAgent, bool expected)
    {
        static Guid? Id(string? name) => name is null ? null : new Guid(name.PadLeft(32, '0').Replace('u', '1').Replace('a', '2'));

        new MemoryCaller(Id(callerUser), Id(callerAgent), null).IsAuthorOf(new MemoryCaller(Id(authorUser), Id(authorAgent), null))
            .Should().Be(expected);
    }

    // ── the scope a new fact gets ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ScopeOrDefault_WithoutAScope_IsTeamWhenItCitesSourcesAndPersonalWhenNot()
    {
        new NewMemoryFact("s", "a", "v", null, null, [Shared]).ScopeOrDefault.Should().Be(MemoryScope.Team);
        new NewMemoryFact("s", "a", "v", null, null, []).ScopeOrDefault.Should().Be(MemoryScope.Personal);
        new NewMemoryFact("s", "a", "v", null, null, null).ScopeOrDefault.Should().Be(MemoryScope.Personal);
    }

    [Fact]
    public void ScopeOrDefault_WithAScope_IsThatScope()
    {
        new NewMemoryFact("s", "a", "v", null, null, [Shared], null, MemoryScope.Personal).ScopeOrDefault.Should().Be(MemoryScope.Personal);
        new NewMemoryFact("s", "a", "v", null, null, [], null, MemoryScope.Team).ScopeOrDefault.Should().Be(MemoryScope.Team);
    }

    [Fact]
    public async Task Remember_WithoutAScope_StoresTheDefaultScope()
    {
        _store.AddAsync(Container, Arg.Any<NewMemoryFact>(), Me, Arg.Any<CancellationToken>()).Returns(Fact(Me, MemoryScope.Team, Shared));

        await _service.RememberAsync(Container, new NewMemoryFact("s", "a", "v", null, null, [Shared]), Me);

        await _store.Received(1).AddAsync(Container, Arg.Is<NewMemoryFact>(f => f.Scope == MemoryScope.Team), Me, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remember_WithAnUndefinedScope_IsRefusedBeforeTheStore()
    {
        MemoryWriteResult result = await _service.RememberAsync(Container, new NewMemoryFact("s", "a", "v", null, null, [], null, (MemoryScope)7), Me);

        result.Error.Should().Contain("scope must be");
        await _store.DidNotReceiveWithAnyArgs().AddAsync(default, default!, default!, default);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Remember_NoveltyWithoutWhatIsNew_IsRefusedBeforeTheStore(string? whatIsNew)
    {
        NewMemoryFact fact = new("s", "a", "v", null, null, [], null, null, new MemoryNovelty(whatIsNew!, []));

        MemoryWriteResult result = await _service.RememberAsync(Container, fact, Me);

        result.Error.Should().Be($"Error: 'whatIsNew' must say what is new, in at most {MemoryService.MaxWhatIsNew} characters.");
        await _store.DidNotReceiveWithAnyArgs().AddAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task Remember_WhatIsNewOverTheLimit_IsRefusedBeforeTheStore()
    {
        NewMemoryFact fact = new("s", "a", "v", null, null, [], null, null,
            new MemoryNovelty(new string('n', MemoryService.MaxWhatIsNew + 1), []));

        MemoryWriteResult result = await _service.RememberAsync(Container, fact, Me);

        result.Error.Should().Contain("'whatIsNew'");
        await _store.DidNotReceiveWithAnyArgs().AddAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task Remember_WithNovelty_StoresItWithWhatIsNewTrimmed()
    {
        MemoryPassage passage = new(Guid.NewGuid(), "ops.md", "Halcyon runs PostgreSQL 15.");
        _store.AddAsync(Container, Arg.Any<NewMemoryFact>(), Me, Arg.Any<CancellationToken>()).Returns(Fact(Me, MemoryScope.Team));

        await _service.RememberAsync(Container,
            new NewMemoryFact("s", "a", "v", null, null, [], null, null, new MemoryNovelty("  why it is new \n", [passage])), Me);

        await _store.Received(1).AddAsync(Container,
            Arg.Is<NewMemoryFact>(f => f.Novelty!.WhatIsNew == "why it is new" && f.Novelty.ClosePassages.Single() == passage),
            Me, Arg.Any<CancellationToken>());
    }

    // ── who sees a fact ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Visible_PersonalFactCitingAReadableDocument_IsSeenOnlyByItsAuthor()
    {
        MemoryFact fact = Fact(Other, MemoryScope.Personal, Shared);
        HashSet<MemorySource> readable = [Shared];

        MemoryService.Visible(fact, readable, Me).Should().BeFalse();
        MemoryService.Visible(fact, readable, Other).Should().BeTrue();
    }

    [Fact]
    public void Visible_TeamFact_IsSeenByEveryoneWhoCanReadEverySource()
    {
        MemoryFact fact = Fact(Other, MemoryScope.Team, Shared, Restricted);

        MemoryService.Visible(fact, new HashSet<MemorySource> { Shared, Restricted }, Me).Should().BeTrue();
        MemoryService.Visible(fact, new HashSet<MemorySource> { Shared }, Me).Should().BeFalse();
        MemoryService.Visible(fact, new HashSet<MemorySource> { Shared }, Other).Should().BeFalse("its author needs the sources too");
    }

    // ── corrections ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TeamCorrectionError_OfAPersonalFact_AllowsAnything()
    {
        MemoryService.TeamCorrectionError(Fact(Me, MemoryScope.Personal, Shared), [], teamWriter: false).Should().BeNull();
    }

    [Fact]
    public void TeamCorrectionError_OfATeamFactWithoutWriteAccess_IsForbidden()
    {
        MemoryWriteResult? result = MemoryService.TeamCorrectionError(Fact(Me, MemoryScope.Team, Shared), [Shared], teamWriter: false);

        result!.Failure.Should().Be(MemoryWriteFailure.Forbidden);
        result.Error.Should().Contain("knowledge:write");
    }

    [Fact]
    public void TeamCorrectionError_KeepingEverySourceAndAddingOne_IsAllowed()
    {
        MemoryService.TeamCorrectionError(Fact(Me, MemoryScope.Team, Shared), [Shared, Restricted], teamWriter: true).Should().BeNull();
    }

    [Fact]
    public void TeamCorrectionError_DroppingASource_IsRefusedNamingIt()
    {
        MemoryWriteResult? result = MemoryService.TeamCorrectionError(Fact(Me, MemoryScope.Team, Shared, Restricted), [Shared], teamWriter: true);

        result!.Failure.Should().Be(MemoryWriteFailure.Invalid);
        result.Error.Should().Contain(Restricted.Ref).And.NotContain($"{Shared.Ref},");
    }

    [Fact]
    public async Task Correct_AskingForAnotherScope_IsRefusedBeforeTheStore()
    {
        MemoryFact current = Fact(Me, MemoryScope.Personal);
        _store.GetAsync(current.Id, Arg.Any<CancellationToken>()).Returns(current);

        MemoryWriteResult result = await _service.CorrectAsync(current.Id, MemoryCorrection.Wrong,
            Replacement([], MemoryScope.Team), Me, teamWriter: true);

        result.Error.Should().Contain("keeps the fact's scope");
        await _store.DidNotReceiveWithAnyArgs().CorrectAsync(default, default, default!, default!, default!, default);
    }

    [Fact]
    public async Task Correct_AskingForTheSameScope_IsWritten()
    {
        MemoryFact current = Fact(Me, MemoryScope.Personal);
        MemoryFact written = Fact(Me, MemoryScope.Personal);
        CorrectionRunsCheck(current, [current], written);

        MemoryWriteResult result = await _service.CorrectAsync(current.Id, MemoryCorrection.Wrong,
            Replacement([], MemoryScope.Personal), Me, teamWriter: false);

        result.Fact.Should().Be(written);
    }

    [Fact]
    public async Task Correct_NamingNoSources_KeepsTheOldFactsSources()
    {
        MemoryFact current = Fact(Other, MemoryScope.Team, Shared, AlsoShared);
        CorrectionRunsCheck(current, [current], Fact(Me, MemoryScope.Team, Shared, AlsoShared));

        MemoryWriteResult result = await _service.CorrectAsync(current.Id, MemoryCorrection.Wrong, Replacement(null), Me, teamWriter: true);

        result.Error.Should().BeNull();
        await _store.Received(1).CorrectAsync(current.Id, MemoryCorrection.Wrong,
            Arg.Is<NewMemoryFact>(f => f.Sources!.SequenceEqual(new[] { Shared, AlsoShared })), Me,
            Arg.Any<Func<IReadOnlyList<MemoryFact>, Task<MemoryWriteResult?>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Correct_OfATeamFactWithoutWriteAccess_IsForbiddenAndAuditsNothing()
    {
        MemoryFact current = Fact(Other, MemoryScope.Team, Shared);
        CorrectionRunsCheck(current, [current], Fact(Me, MemoryScope.Team, Shared));

        MemoryWriteResult result = await _service.CorrectAsync(current.Id, MemoryCorrection.Wrong, Replacement(null), Me, teamWriter: false);

        result.Failure.Should().Be(MemoryWriteFailure.Forbidden);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task Correct_OfATeamFactDroppingASource_IsRefused()
    {
        // Each source keeps the fact from readers who cannot read it; a correction leaving one out would show them the new value.
        MemoryFact current = Fact(Me, MemoryScope.Team, Shared, AlsoShared);
        CorrectionRunsCheck(current, [current], Fact(Me, MemoryScope.Team, Shared));

        MemoryWriteResult result = await _service.CorrectAsync(current.Id, MemoryCorrection.Wrong, Replacement([Shared]), Me, teamWriter: true);

        result.Failure.Should().Be(MemoryWriteFailure.Invalid);
        result.Error.Should().Contain(AlsoShared.Ref);
    }

    // ── forgetting ────────────────────────────────────────────────────────────────────────────────

    private void ForgetRunsAllow(IReadOnlyList<MemoryFact> chain) =>
        _store.ForgetAsync(chain[0].Id, Arg.Any<Func<IReadOnlyList<MemoryFact>, Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await ci.ArgAt<Func<IReadOnlyList<MemoryFact>, Task<bool>>>(1)(chain) ? chain.Count : (int?)null);

    [Fact]
    public async Task Forget_OwnPersonalChainWithoutWriteAccess_Deletes()
    {
        MemoryFact first = Fact(Me, MemoryScope.Personal);
        ForgetRunsAllow([first, Fact(Me, MemoryScope.Personal)]);

        (await _service.ForgetAsync(first.Id, Me, teamWriter: false)).Should().Be((2, MemoryWriteFailure.None));
    }

    [Fact]
    public async Task Forget_ChainWithATeamVersionWithoutWriteAccess_IsForbidden()
    {
        MemoryFact first = Fact(Me, MemoryScope.Team, Shared);
        ForgetRunsAllow([first]);

        (await _service.ForgetAsync(first.Id, Me, teamWriter: false)).Should().Be((0, MemoryWriteFailure.Forbidden));
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task Forget_ChainWithATeamVersionWithWriteAccess_Deletes()
    {
        MemoryFact first = Fact(Other, MemoryScope.Team, Shared);
        ForgetRunsAllow([first]);

        (await _service.ForgetAsync(first.Id, Me, teamWriter: true)).Should().Be((1, MemoryWriteFailure.None));
    }

    [Fact]
    public async Task Forget_ChainWithAHiddenTeamVersion_ReadsAsMissingNotForbidden()
    {
        // Saying "forbidden" would reveal that the hidden version exists.
        MemoryFact first = Fact(Me, MemoryScope.Team, Shared);
        ForgetRunsAllow([first, Fact(Other, MemoryScope.Team, Restricted)]);

        (await _service.ForgetAsync(first.Id, Me, teamWriter: false)).Should().Be((0, MemoryWriteFailure.NotFound));
    }

    [Fact]
    public async Task Forget_AccessCheckFails_ReportsUncheckedAndDeletesNothing()
    {
        MemoryFact first = Fact(Me, MemoryScope.Team, Shared);
        ForgetRunsAllow([first]);
        _access.ReadableAsync(Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("down"));

        (await _service.ForgetAsync(first.Id, Me, teamWriter: true)).Should().Be((0, MemoryWriteFailure.Unchecked));
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default!, default!, default);
    }

}
