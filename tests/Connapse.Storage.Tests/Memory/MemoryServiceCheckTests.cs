using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Storage.Tests.Memory;

/// <summary>
/// Confirming facts, re-saving a fact memory already holds, and a space's default check-by periods (CON-340). A confirm
/// follows a correction's rules: a fact the caller cannot see is missing, and a team fact needs write access.
/// </summary>
[Trait("Category", "Unit")]
public class MemoryServiceCheckTests
{
    private static readonly Guid Container = Guid.NewGuid();
    private static readonly MemoryCaller Me = new(Guid.NewGuid(), null, null);
    private static readonly MemoryCaller Other = new(Guid.NewGuid(), null, null);
    private static readonly MemorySource Shared = new(MemorySourceKind.Document, "shared");
    private static readonly MemorySource Restricted = new(MemorySourceKind.Document, "restricted");

    private readonly IMemoryStore _store = Substitute.For<IMemoryStore>();
    private readonly IDocumentAccess _access = Substitute.For<IDocumentAccess>();
    private readonly ILinkIndex _links = Substitute.For<ILinkIndex>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly MemoryService _service;

    public MemoryServiceCheckTests()
    {
        _links.DocumentsOfAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(string, string)>().ToLookup(p => p.Item1, p => p.Item2));
        _access.ReadableAsync(Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlySet<string>)ci.ArgAt<IReadOnlyCollection<string>>(1).Where(d => d == Shared.Ref).ToHashSet());
        _store.QueryAsync(Arg.Any<MemoryQuery>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MemoryFact>());
        _service = new MemoryService(_store, _access, _links, _audit, NullLogger<MemoryService>.Instance);
    }

    private static MemoryFact Fact(MemoryCaller author, string value = "postgres", params MemorySource[] sources)
    {
        Guid id = Guid.NewGuid();
        return new MemoryFact(id, id, Container, "Halcyon", "database", value, null, null, DateTimeOffset.UtcNow, null, null, null, author, sources,
            sources.Length > 0 ? MemoryScope.Team : MemoryScope.Personal);
    }

    private static NewMemoryFact Saved(string value = "postgres", params MemorySource[] sources) =>
        new("Halcyon", "database", value, null, null, sources);

    private void StoreConfirms(MemoryFact fact, bool runAllow = true) =>
        _store.ConfirmAsync(fact.Id, Arg.Any<MemoryCheckBy?>(), Arg.Any<Func<IReadOnlyList<MemoryFact>, Task<MemoryWriteResult?>>>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                if (runAllow && await ci.Arg<Func<IReadOnlyList<MemoryFact>, Task<MemoryWriteResult?>>>()([fact]) is { } refused)
                    return refused;
                return new MemoryWriteResult(fact with { ConfirmedAt = DateTimeOffset.UtcNow }, null, null) { Confirmed = true };
            });

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MemoryCheckBy.MaxDays + 1)]
    public async Task Confirm_PeriodOutOfRange_RefusesWithoutReading(int days)
    {
        MemoryWriteResult result = await _service.ConfirmAsync(Guid.NewGuid(), MemoryCheckBy.After(days), Me, teamWriter: true);

        result.Failure.Should().Be(MemoryWriteFailure.Invalid);
        result.Error.Should().Contain("1 to 3650 days");
        await _store.DidNotReceiveWithAnyArgs().GetAsync(default);
    }

    [Fact]
    public async Task Confirm_NoSuchFact_IsMissing()
    {
        MemoryWriteResult result = await _service.ConfirmAsync(Guid.NewGuid(), null, Me, teamWriter: true);

        result.Failure.Should().Be(MemoryWriteFailure.NotFound);
        await _store.DidNotReceiveWithAnyArgs().ConfirmAsync(default, default, default!, default);
    }

    [Fact]
    public async Task Confirm_AnotherUsersPersonalFact_IsMissing()
    {
        MemoryFact fact = Fact(Other);
        _store.GetAsync(fact.Id, Arg.Any<CancellationToken>()).Returns(fact);

        MemoryWriteResult result = await _service.ConfirmAsync(fact.Id, null, Me, teamWriter: true);

        result.Failure.Should().Be(MemoryWriteFailure.NotFound);
    }

    [Fact]
    public async Task Confirm_OwnFact_ConfirmsAndAudits()
    {
        MemoryFact fact = Fact(Me);
        _store.GetAsync(fact.Id, Arg.Any<CancellationToken>()).Returns(fact);
        StoreConfirms(fact);
        MemoryCheckBy check = MemoryCheckBy.After(14);

        MemoryWriteResult result = await _service.ConfirmAsync(fact.Id, check, Me, teamWriter: false);

        result.Confirmed.Should().BeTrue();
        result.Fact!.Id.Should().Be(fact.Id);
        await _store.Received(1).ConfirmAsync(fact.Id, check, Arg.Any<Func<IReadOnlyList<MemoryFact>, Task<MemoryWriteResult?>>>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("memory.confirmed", "memory_fact", fact.Id.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirm_TeamFactWithoutWriteAccess_IsForbiddenAndNotAudited()
    {
        MemoryFact fact = Fact(Other, "postgres", Shared);
        _store.GetAsync(fact.Id, Arg.Any<CancellationToken>()).Returns(fact);
        StoreConfirms(fact);

        MemoryWriteResult result = await _service.ConfirmAsync(fact.Id, null, Me, teamWriter: false);

        result.Failure.Should().Be(MemoryWriteFailure.Forbidden);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default, default, default, default);
    }

    [Fact]
    public async Task ConfirmError_TeamFactWithWriteAccess_Allows()
    {
        MemoryWriteResult? error = await _service.ConfirmErrorAsync(Fact(Other, "postgres", Shared), Me, teamWriter: true, CancellationToken.None);

        error.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmError_OwnPersonalFactWithoutWriteAccess_Allows()
    {
        MemoryWriteResult? error = await _service.ConfirmErrorAsync(Fact(Me), Me, teamWriter: false, CancellationToken.None);

        error.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmError_SourceNoLongerReadable_IsMissing()
    {
        MemoryWriteResult? error = await _service.ConfirmErrorAsync(Fact(Me, "postgres", Shared, Restricted), Me, teamWriter: true, CancellationToken.None);

        error!.Failure.Should().Be(MemoryWriteFailure.NotFound);
    }

    [Fact]
    public async Task ConfirmError_AccessCheckFails_RefusesAsUnchecked()
    {
        _access.ReadableAsync(Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("down"));

        MemoryWriteResult? error = await _service.ConfirmErrorAsync(Fact(Me, "postgres", Shared), Me, teamWriter: true, CancellationToken.None);

        error!.Failure.Should().Be(MemoryWriteFailure.Unchecked);
    }

    [Fact]
    public async Task Confirm_StoreRefuses_ReturnsItsErrorWithoutAudit()
    {
        MemoryFact fact = Fact(Me);
        _store.GetAsync(fact.Id, Arg.Any<CancellationToken>()).Returns(fact);
        _store.ConfirmAsync(fact.Id, Arg.Any<MemoryCheckBy?>(), Arg.Any<Func<IReadOnlyList<MemoryFact>, Task<MemoryWriteResult?>>>(), Arg.Any<CancellationToken>())
            .Returns(MemoryWriteResult.Failed("was corrected"));

        MemoryWriteResult result = await _service.ConfirmAsync(fact.Id, null, Me, teamWriter: true);

        result.Error.Should().Be("was corrected");
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default, default, default, default);
    }

    [Fact]
    public async Task Remember_SameFactAgain_ConfirmsTheOneHeldInsteadOfAdding()
    {
        MemoryFact held = Fact(Me);
        _store.QueryAsync(Arg.Any<MemoryQuery>(), Arg.Is(0), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([held]);
        StoreConfirms(held);

        MemoryWriteResult result = await _service.RememberAsync(Container, Saved("  Postgres ") with { Check = MemoryCheckBy.After(7) }, Me);

        result.Confirmed.Should().BeTrue();
        result.Fact!.Id.Should().Be(held.Id);
        await _store.Received(1).QueryAsync(
            Arg.Is<MemoryQuery>(q => q.ContainerId == Container && q.Subject == "Halcyon" && q.Attribute == "database"),
            Arg.Is(0), Arg.Is(MemoryService.MaxResaveCandidates), Arg.Any<CancellationToken>());
        await _store.Received(1).ConfirmAsync(held.Id, MemoryCheckBy.After(7), Arg.Any<Func<IReadOnlyList<MemoryFact>, Task<MemoryWriteResult?>>>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().AddAsync(default, default!, default!, default);
        await _audit.Received(1).LogAsync("memory.confirmed", "memory_fact", held.Id.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remember_HeldFactCorrectedMeanwhile_AddsTheFact()
    {
        MemoryFact held = Fact(Me);
        MemoryFact added = Fact(Me);
        _store.QueryAsync(Arg.Any<MemoryQuery>(), Arg.Is(0), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([held]);
        _store.ConfirmAsync(held.Id, Arg.Any<MemoryCheckBy?>(), Arg.Any<Func<IReadOnlyList<MemoryFact>, Task<MemoryWriteResult?>>>(), Arg.Any<CancellationToken>())
            .Returns(MemoryWriteResult.Failed("was corrected"));
        _store.AddAsync(Container, Arg.Any<NewMemoryFact>(), Me, Arg.Any<CancellationToken>()).Returns(added);

        MemoryWriteResult result = await _service.RememberAsync(Container, Saved(), Me);

        result.Confirmed.Should().BeFalse();
        result.Fact.Should().Be(added);
        await _audit.Received(1).LogAsync("memory.remembered", "memory_fact", added.Id.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remember_NewValue_Adds()
    {
        MemoryFact added = Fact(Me, "mysql");
        _store.QueryAsync(Arg.Any<MemoryQuery>(), Arg.Is(0), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Fact(Me)]);
        _store.AddAsync(Container, Arg.Any<NewMemoryFact>(), Me, Arg.Any<CancellationToken>()).Returns(added);

        MemoryWriteResult result = await _service.RememberAsync(Container, Saved("mysql"), Me);

        result.Fact.Should().Be(added);
        await _store.DidNotReceiveWithAnyArgs().ConfirmAsync(default, default, default!, default);
    }

    [Fact]
    public async Task Remember_PeriodOutOfRange_Refuses()
    {
        MemoryWriteResult result = await _service.RememberAsync(Container, Saved() with { Check = MemoryCheckBy.After(0) }, Me);

        result.Failure.Should().Be(MemoryWriteFailure.Invalid);
        await _store.DidNotReceiveWithAnyArgs().AddAsync(default, default!, default!, default);
    }

    [Fact]
    public void IsResaveOf_SameFactInAnotherCase_IsAResave() =>
        MemoryService.IsResaveOf(Fact(Me), new NewMemoryFact("HALCYON", "Database", "POSTGRES", null, null, []), Me).Should().BeTrue();

    [Fact]
    public void IsResaveOf_TeamFactByAnotherAuthor_IsAResave() =>
        MemoryService.IsResaveOf(Fact(Other, "postgres", Shared), Saved("postgres", Shared), Me).Should().BeTrue();

    [Fact]
    public void IsResaveOf_AnotherUsersPersonalFact_IsNot() =>
        MemoryService.IsResaveOf(Fact(Other), Saved(), Me).Should().BeFalse();

    [Fact]
    public void IsResaveOf_DifferentValue_IsNot() =>
        MemoryService.IsResaveOf(Fact(Me), Saved("mysql"), Me).Should().BeFalse();

    [Fact]
    public void IsResaveOf_DifferentSubject_IsNot() =>
        MemoryService.IsResaveOf(Fact(Me), Saved() with { Subject = "Orion" }, Me).Should().BeFalse();

    [Fact]
    public void IsResaveOf_DifferentAttribute_IsNot() =>
        MemoryService.IsResaveOf(Fact(Me), Saved() with { Attribute = "cache" }, Me).Should().BeFalse();

    [Fact]
    public void IsResaveOf_DifferentScope_IsNot() =>
        MemoryService.IsResaveOf(Fact(Me), Saved() with { Scope = MemoryScope.Team }, Me).Should().BeFalse();

    [Fact]
    public void IsResaveOf_DifferentSources_IsNot() =>
        MemoryService.IsResaveOf(Fact(Me, "postgres", Shared), Saved("postgres", Shared, Restricted), Me).Should().BeFalse();

    [Fact]
    public void IsResaveOf_DatesGivenAndDifferent_IsNot()
    {
        MemoryFact held = Fact(Me) with { ValidFrom = DateTimeOffset.UtcNow.AddDays(-3), ValidTo = DateTimeOffset.UtcNow.AddDays(3) };

        MemoryService.IsResaveOf(held, Saved() with { ValidFrom = DateTimeOffset.UtcNow }, Me).Should().BeFalse();
        MemoryService.IsResaveOf(held, Saved() with { ValidTo = DateTimeOffset.UtcNow }, Me).Should().BeFalse();
        MemoryService.IsResaveOf(held, Saved(), Me).Should().BeTrue();
        MemoryService.IsResaveOf(held, Saved() with { ValidFrom = held.ValidFrom, ValidTo = held.ValidTo }, Me).Should().BeTrue();
    }

    [Fact]
    public async Task CheckDefaults_ReadsTheStore()
    {
        Dictionary<string, int> defaults = new() { ["waiting_on"] = 7 };
        _store.CheckDefaultsAsync(Container, Arg.Any<CancellationToken>()).Returns(defaults);

        (await _service.CheckDefaultsAsync(Container)).Should().BeEquivalentTo(defaults);
    }

    [Fact]
    public async Task SetCheckDefaults_Valid_SavesAndAudits()
    {
        Dictionary<string, int> defaults = new() { ["waiting_on"] = 7, ["status"] = MemoryCheckBy.MaxDays };

        string? error = await _service.SetCheckDefaultsAsync(Container, defaults, Me);

        error.Should().BeNull();
        await _store.Received(1).SetCheckDefaultsAsync(Container, defaults, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("memory.check_defaults_set", "container", Container.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    public static TheoryData<string, Dictionary<string, int>> BadDefaults => new()
    {
        { "blank attribute", new() { ["  "] = 7 } },
        { "attribute too long", new() { [new string('a', 201)] = 7 } },
        { "zero days", new() { ["status"] = 0 } },
        { "too many days", new() { ["status"] = MemoryCheckBy.MaxDays + 1 } },
        { "same attribute twice", new() { ["Status"] = 7, ["status "] = 9 } },
        { "too many", Enumerable.Range(0, IMemoryService.MaxCheckDefaults + 1).ToDictionary(i => $"a{i}", _ => 7) },
    };

    [Theory]
    [MemberData(nameof(BadDefaults))]
    public async Task SetCheckDefaults_Invalid_RefusesAndChangesNothing(string name, Dictionary<string, int> defaults)
    {
        string? error = await _service.SetCheckDefaultsAsync(Container, defaults, Me);

        error.Should().StartWith("Error:", name);
        await _store.DidNotReceiveWithAnyArgs().SetCheckDefaultsAsync(default, default!, default);
    }
}
