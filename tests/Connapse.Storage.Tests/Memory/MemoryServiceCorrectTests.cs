using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Storage.Tests.Memory;

/// <summary>
/// A correction needs the corrector to see every version of the chain, so no chain ends up with versions no one user
/// can see all of, which no one could then forget (CON-232).
/// </summary>
[Trait("Category", "Unit")]
public class MemoryServiceCorrectTests
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

    public MemoryServiceCorrectTests()
    {
        _links.DocumentsOfAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(string, string)>().ToLookup(p => p.Item1, p => p.Item2));
        _access.ReadableAsync(Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlySet<string>)ci.ArgAt<IReadOnlyCollection<string>>(1).Where(d => d == Shared.Ref).ToHashSet());
        _service = new MemoryService(_store, _access, _links, _audit, NullLogger<MemoryService>.Instance);
    }

    private static MemoryFact Fact(MemoryCaller author, params MemorySource[] sources)
    {
        Guid id = Guid.NewGuid();
        return new MemoryFact(id, id, Container, "Halcyon", "database", "x", null, null, DateTimeOffset.UtcNow, null, null, null, author, sources);
    }

    [Fact]
    public async Task WholeChainVisible_OwnPrivateAndReadableVersions_Allows()
    {
        MemoryWriteResult? result = await _service.WholeChainVisibleAsync(Guid.NewGuid(), [Fact(Me), Fact(Other, Shared)], Me, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task WholeChainVisible_AnotherUsersPrivateVersion_Refuses()
    {
        Guid factId = Guid.NewGuid();

        MemoryWriteResult? result = await _service.WholeChainVisibleAsync(factId, [Fact(Other), Fact(Other, Shared)], Me, CancellationToken.None);

        result!.Failure.Should().Be(MemoryWriteFailure.Invalid);
        result.Error.Should().Contain(factId.ToString()).And.Contain("Remember a new fact");
    }

    [Fact]
    public async Task WholeChainVisible_VersionCitingAnUnreadableDocument_Refuses()
    {
        MemoryWriteResult? result = await _service.WholeChainVisibleAsync(Guid.NewGuid(), [Fact(Me, Shared), Fact(Other, Shared, Restricted)], Me, CancellationToken.None);

        result!.Failure.Should().Be(MemoryWriteFailure.Invalid);
    }

    [Fact]
    public async Task WholeChainVisible_CallerWithNoUser_RefusesAPrivateVersion()
    {
        MemoryWriteResult? result = await _service.WholeChainVisibleAsync(Guid.NewGuid(), [Fact(new MemoryCaller(null, null, null))],
            new MemoryCaller(null, Guid.NewGuid(), null), CancellationToken.None);

        result!.Failure.Should().Be(MemoryWriteFailure.Invalid);
    }

    [Fact]
    public async Task WholeChainVisible_AccessCheckFails_RefusesAsUnchecked()
    {
        _access.ReadableAsync(Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("down"));

        MemoryWriteResult? result = await _service.WholeChainVisibleAsync(Guid.NewGuid(), [Fact(Me)], Me, CancellationToken.None);

        result!.Failure.Should().Be(MemoryWriteFailure.Unchecked);
        result.Error.Should().Contain("nothing was written");
    }

    [Fact]
    public async Task WholeChainVisible_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _access.ReadableAsync(Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        Func<Task> act = () => _service.WholeChainVisibleAsync(Guid.NewGuid(), [Fact(Me)], Me, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Correct_ChainWithAVersionTheCallerCannotSee_ReturnsTheRefusalAndAuditsNothing()
    {
        MemoryFact current = Fact(Other, Shared);
        IReadOnlyList<MemoryFact> chain = [Fact(Other), current];
        _store.GetAsync(Container, current.Id, Arg.Any<CancellationToken>()).Returns(current);
        _store.CorrectAsync(Container, current.Id, MemoryCorrection.Wrong, Arg.Any<NewMemoryFact>(), Me,
                Arg.Any<Func<IReadOnlyList<MemoryFact>, Task<MemoryWriteResult?>>>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await ci.ArgAt<Func<IReadOnlyList<MemoryFact>, Task<MemoryWriteResult?>>>(5)(chain)
                ?? new MemoryWriteResult(Fact(Me), current, null));

        MemoryWriteResult result = await _service.CorrectAsync(Container, current.Id, MemoryCorrection.Wrong,
            new NewMemoryFact("", "", "y", null, null, []), Me);

        result.Fact.Should().BeNull();
        result.Error.Should().Contain("versions you cannot see");
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task Correct_ChainTheCallerSeesWhole_IsWrittenAndAudited()
    {
        MemoryFact current = Fact(Other, Shared);
        MemoryFact written = Fact(Me);
        IReadOnlyList<MemoryFact> chain = [Fact(Me), current];
        _store.GetAsync(Container, current.Id, Arg.Any<CancellationToken>()).Returns(current);
        _store.CorrectAsync(Container, current.Id, MemoryCorrection.Wrong, Arg.Any<NewMemoryFact>(), Me,
                Arg.Any<Func<IReadOnlyList<MemoryFact>, Task<MemoryWriteResult?>>>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await ci.ArgAt<Func<IReadOnlyList<MemoryFact>, Task<MemoryWriteResult?>>>(5)(chain)
                ?? new MemoryWriteResult(written, current, null));

        MemoryWriteResult result = await _service.CorrectAsync(Container, current.Id, MemoryCorrection.Wrong,
            new NewMemoryFact("", "", "y", null, null, []), Me);

        result.Fact.Should().Be(written);
        await _audit.Received(1).LogAsync("memory.corrected", "memory_fact", written.Id.ToString(), Arg.Any<object>(), Arg.Any<CancellationToken>());
    }
}
