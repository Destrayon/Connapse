using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Connapse.Core.Tests.Memory;

/// <summary>
/// CON-231: clearing a container's memory (before the container is deleted) deletes nothing while any fact in it is
/// hidden from the caller and someone could still see it. A hidden fact nobody can see any more doesn't block.
/// </summary>
[Trait("Category", "Unit")]
public class MemoryServiceClearTests
{
    private static readonly Guid Container = Guid.NewGuid();
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly MemoryCaller Caller = new(Me, Guid.NewGuid(), "me");

    private readonly IMemoryStore _store = Substitute.For<IMemoryStore>();
    private readonly IDocumentAccess _access = Substitute.For<IDocumentAccess>();
    private readonly ILinkIndex _links = Substitute.For<ILinkIndex>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly HashSet<Guid> _users = [Me, Other];
    private readonly List<Guid> _asked = [];

    public MemoryServiceClearTests()
    {
        _access.ReadableAsync(Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>());
        _links.DocumentsOfAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(string Node, string Document)>().ToLookup(p => p.Node, p => p.Document));
        _store.DocumentTitlesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());
    }

    private MemoryService Service() => new(_store, _access, _links, _audit, NullLogger<MemoryService>.Instance);

    private Task<bool> UserExists(Guid user, CancellationToken ct)
    {
        _asked.Add(user);
        return Task.FromResult(_users.Contains(user));
    }

    private static MemoryFact Fact(Guid? author, params MemorySource[] sources)
    {
        Guid id = Guid.NewGuid();
        return new MemoryFact(id, id, Container, "s", "a", "v", null, null, DateTimeOffset.UtcNow, null, null, null,
            new MemoryCaller(author, null, null), sources);
    }

    /// <summary>The store holds <paramref name="facts"/>: a clear asks the service about them and deletes them all when allowed.</summary>
    private void Holds(params MemoryFact[] facts) =>
        _store.ClearAsync(Container, Arg.Any<Func<IReadOnlyList<MemoryFact>, Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(async call => await call.Arg<Func<IReadOnlyList<MemoryFact>, Task<bool>>>()(facts) ? facts.Length : (int?)null);

    private void Stored(params string[] documents) =>
        _store.DocumentTitlesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<string>>().Intersect(documents).ToDictionary(d => d, d => "title"));

    private Task<MemoryClearResult> ClearAsync() => Service().ClearAsync(Container, Caller, UserExists);

    [Fact]
    public async Task Clear_OnlyFactsTheCallerSees_DeletesThemAndAudits()
    {
        string doc = Guid.NewGuid().ToString();
        Stored(doc);
        _access.ReadableAsync(Me, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(new HashSet<string> { doc });
        Holds(Fact(Me), Fact(Other, new MemorySource(MemorySourceKind.Document, doc)));

        var cleared = await ClearAsync();

        cleared.Should().Be(new MemoryClearResult(2));
        _asked.Should().BeEmpty("a fact the caller sees is never an obstacle");
        await _audit.Received(1).LogAsync("memory.cleared", "container", Container.ToString(),
            Arg.Is<object>(d => d.ToString() == new { ContainerId = Container, Deleted = 2, Caller.UserId, Caller.AgentId }.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Clear_NoFacts_DeletesNothingAndWritesNoAuditRow()
    {
        Holds();

        var cleared = await ClearAsync();

        cleared.Should().Be(new MemoryClearResult(0));
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Clear_AnotherLivingUsersPrivateFacts_BlocksWithTheirCountAndAsksOncePerAuthor()
    {
        Holds(Fact(Me), Fact(Other), Fact(Other));

        var cleared = await ClearAsync();

        cleared.Should().Be(new MemoryClearResult(0, Blocking: 2));
        _asked.Should().Equal(Other);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Clear_PrivateFactsOfDeletedOrUnknownAuthors_DoNotBlock()
    {
        Holds(Fact(Guid.NewGuid()), Fact(null));

        var cleared = await ClearAsync();

        cleared.Should().Be(new MemoryClearResult(2));
    }

    [Fact]
    public async Task Clear_ACallerWithNoUser_SeesNoPrivateFact()
    {
        Holds(Fact(Me));

        var cleared = await Service().ClearAsync(Container, new MemoryCaller(null, Guid.NewGuid(), "agent"), UserExists);

        cleared.Blocking.Should().Be(1);
    }

    [Fact]
    public async Task Clear_AFactCitingAStoredDocumentTheCallerCantRead_Blocks()
    {
        string doc = Guid.NewGuid().ToString();
        Stored(doc);
        Holds(Fact(Me, new MemorySource(MemorySourceKind.Document, doc)));

        var cleared = await ClearAsync();

        cleared.Should().Be(new MemoryClearResult(0, Blocking: 1));
        _asked.Should().BeEmpty("a sourced fact is seen through its sources, not its author");
    }

    [Theory]
    [InlineData("gone")]
    [InlineData("not-a-guid")]
    public async Task Clear_AFactCitingADocumentNoLongerStored_DoesNotBlock(string cited)
    {
        string kept = Guid.NewGuid().ToString();
        Stored(kept);
        string missing = cited == "gone" ? Guid.NewGuid().ToString() : cited;
        Holds(Fact(Other,
            new MemorySource(MemorySourceKind.Document, kept),
            new MemorySource(MemorySourceKind.Document, missing)));

        var cleared = await ClearAsync();

        cleared.Should().Be(new MemoryClearResult(1));
    }

    [Fact]
    public async Task Clear_AFactCitingANodeThatStillStandsForADocument_Blocks()
    {
        _links.DocumentsOfAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { ("node-1", "doc-1") }.ToLookup(p => p.Item1, p => p.Item2));
        Holds(Fact(Other, new MemorySource(MemorySourceKind.Node, "node-1")));

        var cleared = await ClearAsync();

        cleared.Should().Be(new MemoryClearResult(0, Blocking: 1));
    }

    [Fact]
    public async Task Clear_AFactCitingANodeWithNoDocumentsLeft_DoesNotBlock()
    {
        Holds(Fact(Other, new MemorySource(MemorySourceKind.Node, "node-1")));

        var cleared = await ClearAsync();

        cleared.Should().Be(new MemoryClearResult(1));
    }

    [Fact]
    public async Task Clear_AccessCheckFails_IsUncheckedAndDeletesNothing()
    {
        _access.ReadableAsync(Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("db down"));
        Holds(Fact(Me, new MemorySource(MemorySourceKind.Document, Guid.NewGuid().ToString())));

        var cleared = await ClearAsync();

        cleared.Should().Be(new MemoryClearResult(0, Unchecked: true));
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Clear_UserLookupFails_IsUnchecked()
    {
        Holds(Fact(Other));

        var cleared = await Service().ClearAsync(Container, Caller, (_, _) => throw new TimeoutException("directory down"));

        cleared.Unchecked.Should().BeTrue();
        cleared.Deleted.Should().Be(0);
    }

    [Fact]
    public async Task Clear_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        _access.ReadableAsync(Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlySet<string>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });
        Holds(Fact(Me, new MemorySource(MemorySourceKind.Document, Guid.NewGuid().ToString())));

        Func<Task> clear = () => Service().ClearAsync(Container, Caller, UserExists, cts.Token);

        await clear.Should().ThrowAsync<OperationCanceledException>();
    }
}
