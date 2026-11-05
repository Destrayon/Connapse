using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Storage.Tests.Memory;

/// <summary>
/// Paging through memory (CON-241): one more row is asked for to tell whether a next page exists, its key is the last
/// row shown, limits are clamped, a history is hidden whole when its fact is, and access that can't be checked shows nothing.
/// </summary>
[Trait("Category", "Unit")]
public class MemoryServicePagingTests
{
    private static readonly Guid Container = Guid.NewGuid();
    private static readonly MemoryCaller Me = new(Guid.NewGuid(), null, null);
    private static readonly MemorySource Restricted = new(MemorySourceKind.Document, "restricted");
    private static readonly MemoryFactFilter Everything = new(null, null, null, MemoryCites.Any, null, null, MemoryFactStatus.Current);

    private readonly IMemoryStore _store = Substitute.For<IMemoryStore>();
    private readonly IDocumentAccess _access = Substitute.For<IDocumentAccess>();
    private readonly ILinkIndex _links = Substitute.For<ILinkIndex>();
    private readonly MemoryService _service;

    public MemoryServicePagingTests()
    {
        _links.DocumentsOfAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(string, string)>().ToLookup(p => p.Item1, p => p.Item2));
        _access.ReadableAsync(Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlySet<string>)ci.ArgAt<IReadOnlyCollection<string>>(1).Where(d => d != Restricted.Ref).ToHashSet());
        _store.CitedSourcesAsync(Container, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns([Restricted]);
        _service = new MemoryService(_store, _access, _links, Substitute.For<IAuditLogger>(), NullLogger<MemoryService>.Instance);
    }

    private static MemoryFact Fact(int minute, Guid? chain = null, params MemorySource[] sources)
    {
        Guid id = Guid.NewGuid();
        return new MemoryFact(id, chain ?? id, Container, "Halcyon", "database", $"v{minute}", null, null,
            DateTimeOffset.UnixEpoch.AddMinutes(minute), null, null, null, Me, sources, MemoryScope.Team);
    }

    private static MemoryListedFact Listed(int minute)
    {
        MemoryFact fact = Fact(minute);
        return new MemoryListedFact(fact, new MemoryFactKey(fact.Id, Recorded: fact.RecordedAt));
    }

    private void AccessCantBeChecked() =>
        _access.ReadableAsync(Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("access store down"));

    // ── facts ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_StoreHasMoreThanAPage_ShowsThePageAndKeysTheNextOnItsLastFact()
    {
        List<MemoryListedFact> rows = [.. Enumerable.Range(0, 4).Select(Listed)];
        _store.PageAsync(Container, Everything, MemoryFactSort.Recorded, null, 4, Arg.Any<MemoryViewer>(), Arg.Any<CancellationToken>()).Returns(rows);

        MemoryFactPage page = await _service.ListAsync(Container, Everything, MemoryFactSort.Recorded, null, 3, Me);

        page.Denied.Should().BeFalse();
        page.Facts.Select(f => f.Fact).Should().Equal(rows.Take(3).Select(r => r.Fact));
        page.Next.Should().Be(rows[2].Key);
    }

    [Fact]
    public async Task ListAsync_StoreHasAPageOrLess_HasNoNextPage()
    {
        List<MemoryListedFact> rows = [.. Enumerable.Range(0, 3).Select(Listed)];
        _store.PageAsync(Container, Everything, MemoryFactSort.Recorded, null, 4, Arg.Any<MemoryViewer>(), Arg.Any<CancellationToken>()).Returns(rows);

        MemoryFactPage page = await _service.ListAsync(Container, Everything, MemoryFactSort.Recorded, null, 3, Me);

        page.Facts.Should().HaveCount(3);
        page.Next.Should().BeNull();
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(-7, 2)]
    [InlineData(500, 101)]
    public async Task ListAsync_LimitOutOfRange_IsClampedBeforeTheStoreIsAsked(int limit, int take)
    {
        _store.PageAsync(default, null!, default, null, default, null!).ReturnsForAnyArgs([]);

        await _service.ListAsync(Container, Everything, MemoryFactSort.Recorded, null, limit, Me);

        await _store.Received(1).PageAsync(Container, Everything, MemoryFactSort.Recorded, null, take, Arg.Any<MemoryViewer>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAsync_ViewerReadsOnlyWhatAccessAllows()
    {
        _store.PageAsync(default, null!, default, null, default, null!).ReturnsForAnyArgs([]);
        _store.CitedSourcesAsync(Container, null, Arg.Any<CancellationToken>())
            .Returns([Restricted, new MemorySource(MemorySourceKind.Document, "open")]);

        await _service.ListAsync(Container, Everything, MemoryFactSort.Recorded, null, 10, Me);

        await _store.Received(1).PageAsync(Container, Everything, MemoryFactSort.Recorded, null, 11,
            Arg.Is<MemoryViewer>(v => v.Caller == Me && v.Readable.Count == 1 && v.Readable.Single().Ref == "open"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAsync_AccessCantBeChecked_ShowsNothing()
    {
        AccessCantBeChecked();

        MemoryFactPage page = await _service.ListAsync(Container, Everything, MemoryFactSort.Recorded, null, 10, Me);

        page.Should().Be(MemoryFactPage.DeniedResult);
        await _store.DidNotReceiveWithAnyArgs().PageAsync(default, null!, default, null, default, null!);
    }

    // ── count ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(12, false)]
    [InlineData(250_000, true)]
    public async Task CountAsync_StoreCounts_AnswersWithinTheBudget(long count, bool capped)
    {
        _store.CountAsync(Container, Everything, Arg.Any<MemoryViewer>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns((count, capped));

        MemoryFactCount counted = await _service.CountAsync(Container, Everything, Me);

        counted.Should().Be(new MemoryFactCount(count, capped));
        await _store.Received(1).CountAsync(Container, Everything, Arg.Any<MemoryViewer>(),
            Arg.Is<TimeSpan>(b => b > TimeSpan.Zero && b < TimeSpan.FromSeconds(2)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CountAsync_AccessCantBeChecked_CountsNothing()
    {
        AccessCantBeChecked();

        (await _service.CountAsync(Container, Everything, Me)).Should().Be(MemoryFactCount.DeniedResult);
    }

    // ── subjects ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubjectsAsync_StoreHasMoreThanAPage_KeysTheNextOnTheLastSubjectShown()
    {
        _store.SubjectsAsync(Container, "ha", MemoryFactStatus.All, "alpha", 3, Arg.Any<MemoryViewer>(), Arg.Any<CancellationToken>())
            .Returns([("beta", new MemorySubject("Beta", 1)), ("gamma", new MemorySubject("Gamma", 2)), ("halcyon", new MemorySubject("Halcyon", 5))]);

        MemorySubjectPage page = await _service.SubjectsAsync(Container, "ha", MemoryFactStatus.All, "alpha", 2, Me);

        page.Subjects.Should().Equal(new MemorySubject("Beta", 1), new MemorySubject("Gamma", 2));
        page.Next.Should().Be("gamma");
    }

    [Fact]
    public async Task SubjectsAsync_LastPage_HasNoNextAndClampsTheLimit()
    {
        _store.SubjectsAsync(default, null, default, null, default, null!).ReturnsForAnyArgs([("beta", new MemorySubject("Beta", 1))]);

        MemorySubjectPage page = await _service.SubjectsAsync(Container, null, MemoryFactStatus.Current, null, 1000, Me);

        page.Next.Should().BeNull();
        await _store.Received(1).SubjectsAsync(Container, null, MemoryFactStatus.Current, null, 101, Arg.Any<MemoryViewer>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubjectsAsync_AccessCantBeChecked_ShowsNothing()
    {
        AccessCantBeChecked();

        (await _service.SubjectsAsync(Container, null, MemoryFactStatus.Current, null, 10, Me)).Should().Be(MemorySubjectPage.DeniedResult);
    }

    // ── history ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HistoryAsync_LongChain_PagesFromTheChainWithTheTotalAndKeysOnTheLastVersionShown()
    {
        MemoryFact current = Fact(9);
        List<MemoryFact> versions = [current, Fact(8, current.ChainId), Fact(7, current.ChainId)];
        _store.GetAsync(current.Id, Arg.Any<CancellationToken>()).Returns(current);
        _store.HistoryAsync(Container, current.ChainId, null, 3, Arg.Any<MemoryViewer>(), Arg.Any<CancellationToken>()).Returns(versions);
        _store.HistoryCountAsync(Container, current.ChainId, Arg.Any<MemoryViewer>(), Arg.Any<CancellationToken>()).Returns(140L);

        MemoryHistoryPage page = await _service.HistoryAsync(Container, current.Id, null, 2, Me);

        page.Versions.Select(v => v.Fact).Should().Equal(versions[0], versions[1]);
        page.Total.Should().Be(140);
        page.Next.Should().Be(new MemoryVersionKey(versions[1].RecordedAt, versions[1].Id));
        await _store.Received(1).CitedSourcesAsync(Container, current.ChainId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HistoryAsync_LastPage_HasNoNext()
    {
        MemoryFact current = Fact(9);
        _store.GetAsync(current.Id, Arg.Any<CancellationToken>()).Returns(current);
        _store.HistoryAsync(default, default, null, default, null!).ReturnsForAnyArgs([current]);
        _store.HistoryCountAsync(default, default, null!).ReturnsForAnyArgs(1L);

        MemoryHistoryPage page = await _service.HistoryAsync(Container, current.Id, null, 0, Me);

        page.Next.Should().BeNull();
        page.Total.Should().Be(1);
        await _store.Received(1).HistoryAsync(Container, current.ChainId, null, 2, Arg.Any<MemoryViewer>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HistoryAsync_NoSuchFact_IsMissing()
    {
        (await _service.HistoryAsync(Container, Guid.NewGuid(), null, 10, Me)).Should().Be(MemoryHistoryPage.Missing);
        await _store.DidNotReceiveWithAnyArgs().HistoryAsync(default, default, null, default, null!);
    }

    [Fact]
    public async Task HistoryAsync_FactInAnotherSpace_IsMissing()
    {
        MemoryFact elsewhere = Fact(9) with { ContainerId = Guid.NewGuid() };
        _store.GetAsync(elsewhere.Id, Arg.Any<CancellationToken>()).Returns(elsewhere);

        (await _service.HistoryAsync(Container, elsewhere.Id, null, 10, Me)).Should().Be(MemoryHistoryPage.Missing);
        await _store.DidNotReceiveWithAnyArgs().HistoryAsync(default, default, null, default, null!);
    }

    [Fact]
    public async Task HistoryAsync_FactTheCallerCantSee_IsMissingSoItsHistoryCantBeProbed()
    {
        MemoryFact hidden = Fact(9, null, Restricted);
        _store.GetAsync(hidden.Id, Arg.Any<CancellationToken>()).Returns(hidden);

        (await _service.HistoryAsync(Container, hidden.Id, null, 10, Me)).Should().Be(MemoryHistoryPage.Missing);
        await _store.DidNotReceiveWithAnyArgs().HistoryAsync(default, default, null, default, null!);
        await _store.DidNotReceiveWithAnyArgs().HistoryCountAsync(default, default, null!);
    }

    [Fact]
    public async Task HistoryAsync_AccessCantBeChecked_ShowsNothing()
    {
        MemoryFact current = Fact(9);
        _store.GetAsync(current.Id, Arg.Any<CancellationToken>()).Returns(current);
        AccessCantBeChecked();

        (await _service.HistoryAsync(Container, current.Id, null, 10, Me)).Should().Be(MemoryHistoryPage.DeniedResult);
    }

    // ── close passages kept with a fact (CON-245) ─────────────────────────────────────────────────

    private static readonly MemoryPassage OpenPassage = new(Guid.NewGuid(), "open.md", "Halcyon runs PostgreSQL 15.");
    private static readonly MemoryPassage HiddenPassage = new(Guid.NewGuid(), "hidden.md", "Halcyon's database is PostgreSQL.");

    private static MemoryFact WithPassages(int minute) =>
        Fact(minute) with { Novelty = new MemoryNovelty("the upgrade date", [OpenPassage, HiddenPassage]) };

    /// <summary>The reader may read every document but the hidden passage's, and every cited source.</summary>
    private void HiddenPassageUnreadable() =>
        _access.ReadableAsync(Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlySet<string>)ci.ArgAt<IReadOnlyCollection<string>>(1)
                .Where(d => d != HiddenPassage.DocumentId.ToString()).ToHashSet());

    [Fact]
    public async Task RecallAsync_PassageInADocumentTheReaderCantRead_IsLeftOut()
    {
        HiddenPassageUnreadable();
        MemoryFact fact = WithPassages(1);
        _store.VisibleAsync(default!, null!, default).ReturnsForAnyArgs([fact]);

        MemoryRecall recall = await _service.RecallAsync(new MemoryQuery(Container), Me);

        RecalledFact shown = recall.Facts.Should().ContainSingle().Subject;
        shown.Fact.Novelty!.WhatIsNew.Should().Be("the upgrade date");
        shown.Fact.Novelty.ClosePassages.Should().Equal(OpenPassage);
    }

    [Fact]
    public async Task ListAsync_PassageInADocumentTheReaderCantRead_IsLeftOut()
    {
        HiddenPassageUnreadable();
        MemoryFact fact = WithPassages(1);
        _store.PageAsync(default, null!, default, null, default, null!)
            .ReturnsForAnyArgs([new MemoryListedFact(fact, new MemoryFactKey(fact.Id, Recorded: fact.RecordedAt))]);

        MemoryFactPage page = await _service.ListAsync(Container, Everything, MemoryFactSort.Recorded, null, 10, Me);

        page.Facts.Single().Fact.Novelty!.ClosePassages.Should().Equal(OpenPassage);
    }

    [Fact]
    public async Task HistoryAsync_PassageInADocumentTheReaderCantRead_IsLeftOut()
    {
        HiddenPassageUnreadable();
        MemoryFact current = WithPassages(9);
        _store.GetAsync(current.Id, Arg.Any<CancellationToken>()).Returns(current);
        _store.HistoryAsync(default, default, null, default, null!).ReturnsForAnyArgs([current]);
        _store.HistoryCountAsync(default, default, null!).ReturnsForAnyArgs(1L);

        MemoryHistoryPage page = await _service.HistoryAsync(Container, current.Id, null, 10, Me);

        page.Versions.Single().Fact.Novelty!.ClosePassages.Should().Equal(OpenPassage);
    }

    [Fact]
    public async Task RecallAsync_ReaderCanReadEveryPassage_SeesThemAll()
    {
        MemoryFact fact = WithPassages(1);
        _store.VisibleAsync(default!, null!, default).ReturnsForAnyArgs([fact]);

        MemoryRecall recall = await _service.RecallAsync(new MemoryQuery(Container), Me);

        recall.Facts.Single().Fact.Novelty!.ClosePassages.Should().Equal(OpenPassage, HiddenPassage);
    }

    [Fact]
    public async Task RecallAsync_NoPassages_ShowsTheFactUnchangedWithoutAskingAboutPassageDocuments()
    {
        MemoryFact fact = Fact(1);
        _store.VisibleAsync(default!, null!, default).ReturnsForAnyArgs([fact]);

        MemoryRecall recall = await _service.RecallAsync(new MemoryQuery(Container), Me);

        recall.Facts.Single().Fact.Should().Be(fact);
        await _access.DidNotReceive().ReadableAsync(Arg.Any<Guid?>(),
            Arg.Is<IReadOnlyCollection<string>>(d => d.Any(id => id.Length == 36)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecallAsync_PassageAccessCantBeChecked_ShowsNothing()
    {
        MemoryFact fact = WithPassages(1);
        _store.CitedSourcesAsync(Container, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns([]);
        _store.VisibleAsync(default!, null!, default).ReturnsForAnyArgs([fact]);
        AccessCantBeChecked();

        (await _service.RecallAsync(new MemoryQuery(Container), Me)).Should().Be(MemoryRecall.DeniedResult);
    }

    [Fact]
    public void Readable_FactWithoutNovelty_IsUnchanged()
    {
        MemoryFact fact = Fact(1);

        MemoryService.Readable(fact, new HashSet<string>()).Should().BeSameAs(fact);
    }

    [Fact]
    public void Readable_PassageWithoutADocumentId_IsLeftOut()
    {
        MemoryPassage unnamed = new(Guid.Empty, "not-a-guid", "Halcyon's database.");
        MemoryFact fact = Fact(1) with { Novelty = new MemoryNovelty("why", [unnamed, OpenPassage]) };

        MemoryService.Readable(fact, new HashSet<string> { OpenPassage.DocumentId.ToString() })
            .Novelty!.ClosePassages.Should().Equal(OpenPassage);
    }

    [Fact]
    public async Task SpaceOfAsync_AsksTheStore()
    {
        Guid fact = Guid.NewGuid();
        _store.SpaceOfAsync(fact, Arg.Any<CancellationToken>()).Returns(Container);

        (await _service.SpaceOfAsync(fact)).Should().Be(Container);
    }
}
