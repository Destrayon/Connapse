using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Storage.Tests.Memory;

/// <summary>
/// A save's answer lists the current facts it lands beside (CON-341): those with its subject and attribute that the
/// saver can see, read the way a list is, never the saved fact itself, at most ten, and none when they can't be read.
/// </summary>
[Trait("Category", "Unit")]
public class MemoryServiceExistingTests
{
    private static readonly Guid Container = Guid.NewGuid();
    private static readonly MemoryCaller Me = new(Guid.NewGuid(), null, null);

    private readonly IMemoryStore _store = Substitute.For<IMemoryStore>();
    private readonly IDocumentAccess _access = Substitute.For<IDocumentAccess>();
    private readonly ILinkIndex _links = Substitute.For<ILinkIndex>();
    private readonly MemoryService _service;
    private MemoryFact? _saved;

    public MemoryServiceExistingTests()
    {
        _links.DocumentsOfAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(string, string)>().ToLookup(p => p.Item1, p => p.Item2));
        _access.ReadableAsync(Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlySet<string>)ci.ArgAt<IReadOnlyCollection<string>>(1).ToHashSet());
        _store.CitedSourcesAsync(Container, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns([]);
        _store.AddAsync(Container, Arg.Any<NewMemoryFact>(), Me, Arg.Any<CancellationToken>()).Returns(ci =>
        {
            NewMemoryFact f = ci.ArgAt<NewMemoryFact>(1);
            _saved = Fact(f.Subject, f.Attribute, f.Value);
            return _saved;
        });
        _store.PageAsync(default, null!, default, null, default, null!).ReturnsForAnyArgs([]);
        _service = new MemoryService(_store, _access, _links, Substitute.For<IAuditLogger>(), NullLogger<MemoryService>.Instance);
    }

    private static MemoryFact Fact(string subject, string attribute, string value)
    {
        Guid id = Guid.NewGuid();
        return new MemoryFact(id, id, Container, subject, attribute, value, null, null,
            DateTimeOffset.UnixEpoch, null, null, null, Me, [], MemoryScope.Team);
    }

    private static MemoryListedFact Listed(MemoryFact fact) => new(fact, new MemoryFactKey(fact.Id, Recorded: fact.RecordedAt));

    private void StoreLists(params MemoryFact[] facts) =>
        _store.PageAsync(Container, Arg.Any<MemoryFactFilter>(), MemoryFactSort.Recorded, null, Arg.Any<int>(), Arg.Any<MemoryViewer>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<MemoryListedFact>)[.. (_saved is null ? facts : [_saved, .. facts]).Select(Listed)]);

    private Task<MemoryWriteResult> SaveAsync(string attribute = "status") =>
        _service.RememberAsync(Container, new NewMemoryFact("CON-291", attribute, "Done", null, null, null), Me);

    [Fact]
    public async Task RememberAsync_NothingElseCurrent_ListsNothing()
    {
        MemoryWriteResult result = await SaveAsync();

        result.Fact.Should().NotBeNull();
        result.Existing.Should().BeEmpty();
    }

    [Fact]
    public async Task RememberAsync_SameSubjectAndAttributeCurrent_ListsItButNotTheSavedFact()
    {
        MemoryFact before = Fact("CON-291", "Status", "In progress");
        StoreLists(before, Fact("CON-291", "lead", "Ada"));

        MemoryWriteResult result = await SaveAsync();

        result.Existing.Should().ContainSingle().Which.Should().Be(before);
    }

    [Fact]
    public async Task RememberAsync_LooksUpCurrentFactsOnTheSavedSubject()
    {
        await SaveAsync();

        await _store.Received(1).PageAsync(Container,
            Arg.Is<MemoryFactFilter>(f => f.Subject == "CON-291" && f.Status == MemoryFactStatus.Current),
            MemoryFactSort.Recorded, null, Arg.Any<int>(), Arg.Is<MemoryViewer>(v => v.Caller == Me), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RememberAsync_MoreThanTenBeside_ListsTen()
    {
        StoreLists([.. Enumerable.Range(0, 12).Select(i => Fact("CON-291", "status", $"v{i}"))]);

        MemoryWriteResult result = await SaveAsync();

        result.Existing.Should().HaveCount(10);
    }

    [Fact]
    public async Task RememberAsync_LookupCantBeChecked_StillSavesAndListsNothing()
    {
        _store.CitedSourcesAsync(Container, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("down"));

        MemoryWriteResult result = await SaveAsync();

        result.Fact.Should().NotBeNull();
        result.Error.Should().BeNull();
        result.Existing.Should().BeEmpty();
    }

    [Fact]
    public async Task RememberAsync_Refused_ListsNothingAndLooksNothingUp()
    {
        MemoryWriteResult result = await _service.RememberAsync(Container, new NewMemoryFact("", "status", "Done", null, null, null), Me);

        result.Fact.Should().BeNull();
        result.Existing.Should().BeNull();
        await _store.DidNotReceiveWithAnyArgs().PageAsync(default, null!, default, null, default, null!);
    }
}
