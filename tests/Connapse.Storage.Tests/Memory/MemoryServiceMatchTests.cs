using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Storage.Tests.Memory;

/// <summary>
/// A save shows the current facts it may repeat or contradict (CON-332): only those the saver could see, and a check
/// that fails or runs long shows none without failing the save.
/// </summary>
[Trait("Category", "Unit")]
public class MemoryServiceMatchTests
{
    private static readonly Guid Container = Guid.NewGuid();
    private static readonly MemoryCaller Me = new(Guid.NewGuid(), null, null);
    private static readonly MemoryCaller Other = new(Guid.NewGuid(), null, null);
    private static readonly MemorySource Restricted = new(MemorySourceKind.Document, "restricted");
    private static readonly MemoryMatchWords StatusWords = new(["con", "291"], ["statu"], ["merg"]);

    private readonly IMemoryStore _store = Substitute.For<IMemoryStore>();
    private readonly IDocumentAccess _access = Substitute.For<IDocumentAccess>();
    private readonly ILinkIndex _links = Substitute.For<ILinkIndex>();
    private readonly MemoryService _service;
    private readonly MemoryFact _saved = Fact(Me, MemoryScope.Team);

    public MemoryServiceMatchTests()
    {
        _links.DocumentsOfAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(string, string)>().ToLookup(p => p.Item1, p => p.Item2));
        _access.ReadableAsync(Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlySet<string>)ci.ArgAt<IReadOnlyCollection<string>>(1).Where(d => d != Restricted.Ref).ToHashSet());
        _store.AddAsync(Container, Arg.Any<NewMemoryFact>(), Me, Arg.Any<CancellationToken>()).Returns(_saved);
        _service = new MemoryService(_store, _access, _links, Substitute.For<IAuditLogger>(), NullLogger<MemoryService>.Instance);
    }

    [Fact]
    public async Task Remember_AnOlderFactWithTheSameKey_ShowsIt()
    {
        MemoryFact old = Fact(Me, MemoryScope.Team);
        Found(old);

        MemoryWriteResult result = await RememberAsync();

        result.Matches.Should().ContainSingle().Which.Should().Be(new MemoryFactMatch(old, MemoryMatchKind.SameSubjectAndAttribute));
    }

    [Fact]
    public async Task Remember_MatchesTheSaverCannotSee_AreLeftOut()
    {
        Found(Fact(Me, MemoryScope.Team, Restricted), Fact(Other, MemoryScope.Personal));

        (await RememberAsync()).Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task Remember_WhenTheCheckFails_SavesAndShowsNothing()
    {
        _store.MatchCandidatesAsync(_saved, Me, Arg.Any<int>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("down"));

        MemoryWriteResult result = await RememberAsync();

        result.Fact.Should().Be(_saved);
        result.Error.Should().BeNull();
        result.Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task Remember_WhenTheCheckRunsPastItsBudget_SavesAndShowsNothing()
    {
        _store.MatchCandidatesAsync(_saved, Me, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(async ci =>
        {
            await Task.Delay(Timeout.Infinite, ci.ArgAt<CancellationToken>(3));
            return MemoryMatchSearch.None;
        });

        MemoryWriteResult result = await RememberAsync();

        result.Fact.Should().Be(_saved);
        result.Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task Remember_NeverChangesAFactBecauseOfAMatch()
    {
        Found(Fact(Me, MemoryScope.Team));

        await RememberAsync();

        await _store.DidNotReceiveWithAnyArgs().CorrectAsync(default, default, default!, default!, default!, default);
        await _store.DidNotReceiveWithAnyArgs().ForgetAsync(default, default!, default);
    }

    private Task<MemoryWriteResult> RememberAsync() =>
        _service.RememberAsync(Container, new NewMemoryFact("CON-291", "status", "Merged", null, null, [], null, MemoryScope.Team), Me);

    private void Found(params MemoryFact[] facts) =>
        _store.MatchCandidatesAsync(_saved, Me, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryMatchSearch(StatusWords, [.. facts.Select(f => new MemoryMatchCandidate(f, StatusWords, 0.5f))]));

    private static MemoryFact Fact(MemoryCaller author, MemoryScope scope, params MemorySource[] sources)
    {
        Guid id = Guid.NewGuid();
        return new MemoryFact(id, id, Container, "CON-291", "status", "In review", null, null, DateTimeOffset.UtcNow, null, null, null,
            author, sources, scope);
    }
}
