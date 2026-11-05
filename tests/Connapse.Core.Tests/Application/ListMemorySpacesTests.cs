using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Memory;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Tests.Support;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// CON-243: the spaces list checks access only for the spaces that could land on the page, and one fact per documents
/// container whose presence is in doubt. Each test names the spaces the store is asked to check.
/// </summary>
[Trait("Category", "Unit")]
public class ListMemorySpacesTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly OperationContext Reader = new(new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test")), OperationSurface.Rest);

    /// <summary>A space, its exact values, and the bounds the store gives before checking.</summary>
    private sealed record Space(string Name, int Facts, int AtLeast, int AtMost, ContainerKind Kind = ContainerKind.Memory, int Minutes = 0)
    {
        public string Id { get; } = Guid.NewGuid().ToString();

        public MemorySpaceCandidate Candidate => new(
            new Container(Id, Name, null, Epoch.UtcDateTime, Epoch.UtcDateTime, Kind: Kind),
            false, AtLeast, AtMost, Epoch.AddMinutes(Minutes), Epoch.AddMinutes(Minutes));
    }

    private sealed class Store(params Space[] spaces)
    {
        public IMemorySpaceStore Fake { get; } = Substitute.For<IMemorySpaceStore>();

        public List<IReadOnlyCollection<string>> Tallied { get; } = [];

        public List<IReadOnlyCollection<string>> Probed { get; } = [];

        public bool TallyFails { get; init; }

        public bool ProbeFails { get; init; }

        public IReadOnlySet<string> Gone { get; init; } = new HashSet<string>();

        public Store Ready()
        {
            Fake.CandidatesAsync(Arg.Any<MemoryCaller>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(spaces.Select(s => s.Candidate).ToList());
            Fake.TallyAsync(Arg.Any<MemoryCaller>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    IReadOnlyCollection<string> ids = call.ArgAt<IReadOnlyCollection<string>>(1);
                    Tallied.Add([.. ids]);
                    return TallyFails ? null : spaces.Where(s => ids.Contains(s.Id) && !Gone.Contains(s.Id))
                        .ToDictionary(s => s.Id, s => new MemorySpaceTally(s.Facts, Epoch.AddMinutes(s.Minutes)));
                });
            Fake.HoldingReadableAsync(Arg.Any<MemoryCaller>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    IReadOnlyCollection<string> ids = call.ArgAt<IReadOnlyCollection<string>>(1);
                    Probed.Add([.. ids]);
                    return ProbeFails ? null : spaces.Where(s => ids.Contains(s.Id) && s.Facts > 0).Select(s => s.Id).ToHashSet();
                });
            return this;
        }

        public IEnumerable<string> AllTallied => Tallied.SelectMany(t => t);
    }

    private static ListMemorySpaces List(Store store) =>
        new(store.Fake, new PolicyAuthorizer(policy => policy == Policies.KnowledgeRead));

    [Fact]
    public async Task RunAsync_NameSort_TalliesOnlyThePage()
    {
        Space[] spaces = [.. Enumerable.Range(0, 5).Select(i => new Space($"s{i}", i, 0, 9))];
        Store store = new Store(spaces).Ready();

        var page = await List(store).RunAsync(Reader, new MemorySpaceQuery(Sort: MemorySpaceSort.Name, Limit: 2));

        page.Result!.Items.Select(i => i.Name).Should().Equal("s0", "s1");
        page.Result.Items.Select(i => i.Facts).Should().Equal(0, 1);
        store.Tallied.Should().ContainSingle().Which.Should().BeEquivalentTo([spaces[0].Id, spaces[1].Id]);
        page.Result.NextCursor.Should().NotBeNull();
        page.Result.Total.Should().Be(5);
    }

    [Fact]
    public async Task RunAsync_FactsSortWithSeparatedBounds_StopsAfterOneBatch()
    {
        Space big = new("big", 50, 40, 60), bigger = new("bigger", 70, 65, 80);
        Space[] small = [.. Enumerable.Range(0, 4).Select(i => new Space($"small{i}", i, 0, 10))];
        Store store = new Store([big, bigger, .. small]).Ready();

        var page = await List(store).RunAsync(Reader, new MemorySpaceQuery(Sort: MemorySpaceSort.Facts, Limit: 2));

        page.Result!.Items.Select(i => i.Name).Should().Equal("bigger", "big");
        store.AllTallied.Should().BeEquivalentTo([big.Id, bigger.Id], "no small space could outrank the page's last");
        page.Result.NextCursor.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_FactsSortWithOverlappingBounds_ChecksUntilTheOrderIsSure()
    {
        // The upper bounds put "loose" first, but its exact count puts it last.
        Space loose = new("loose", 1, 0, 100), firm = new("firm", 30, 30, 31), next = new("next", 20, 20, 25);
        Space middle = new("middle", 15, 15, 16), last = new("last", 5, 5, 6);
        Store store = new Store(loose, firm, next, middle, last).Ready();

        var page = await List(store).RunAsync(Reader, new MemorySpaceQuery(Sort: MemorySpaceSort.Facts, Limit: 2));

        page.Result!.Items.Select(i => i.Name).Should().Equal("firm", "next");
        store.Tallied.Should().HaveCount(2, "after loose and firm, next could still outrank loose");
        store.AllTallied.Should().NotContain(last.Id);
    }

    [Fact]
    public async Task RunAsync_SettledSpaces_AreNeverTallied()
    {
        Space settled = new("settled", 3, 3, 3), open = new("open", 2, 0, 4);
        Store store = new Store(settled, open).Ready();

        var page = await List(store).RunAsync(Reader, new MemorySpaceQuery(Sort: MemorySpaceSort.Facts));

        page.Result!.Items.Select(i => (i.Name, i.Facts)).Should().Equal(("settled", 3), ("open", 2));
        store.AllTallied.Should().Equal(open.Id);
        page.Result.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_SecondPage_SkipsSpacesThatMustBeOnTheFirstUnchecked()
    {
        Space[] spaces = [new("a", 90, 90, 95), new("b", 80, 80, 85), new("c", 10, 0, 50), new("d", 5, 0, 40)];
        Store first = new Store(spaces).Ready();
        string cursor = (await List(first).RunAsync(Reader, new MemorySpaceQuery(Sort: MemorySpaceSort.Facts, Limit: 2))).Result!.NextCursor!;
        Store second = new Store(spaces).Ready();

        var page = await List(second).RunAsync(Reader, new MemorySpaceQuery(Sort: MemorySpaceSort.Facts, Limit: 2, Cursor: cursor));

        page.Result!.Items.Select(i => i.Name).Should().Equal("c", "d");
        second.AllTallied.Should().BeEquivalentTo([spaces[2].Id, spaces[3].Id], "a and b rank at or before the cursor whatever their exact counts");
        page.Result.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_UpdatedSort_OrdersByExactNewestChange()
    {
        Space older = new("older", 1, 0, 1, Minutes: 1), newer = new("newer", 1, 0, 1, Minutes: 5);
        Store store = new Store(older, newer).Ready();

        var page = await List(store).RunAsync(Reader, new MemorySpaceQuery(Sort: MemorySpaceSort.Updated));

        page.Result!.Items.Select(i => i.Name).Should().Equal("newer", "older");
        page.Result.Items[0].UpdatedAt.Should().Be(Epoch.AddMinutes(5));
    }

    [Fact]
    public async Task RunAsync_DocumentsContainers_ProbesOnlyThoseInDoubtAndDropsHiddenOnes()
    {
        Space certain = new("certain", 4, 1, 4, ContainerKind.Documents);
        Space readable = new("readable", 2, 0, 3, ContainerKind.Documents);
        Space hidden = new("hidden", 0, 0, 3, ContainerKind.Documents);
        Space team = new("team", 0, 0, 0);
        Store store = new Store(certain, readable, hidden, team).Ready();

        var page = await List(store).RunAsync(Reader, new MemorySpaceQuery(Kind: MemorySpaceKind.Team));

        store.Probed.Should().ContainSingle().Which.Should().BeEquivalentTo([readable.Id, hidden.Id]);
        page.Result!.Counts.Should().Be(new MemorySpaceCounts(3, 0, 0, 1, 2), "a container whose every fact is hidden isn't listed");
        page.Result.Items.Select(i => i.Name).Should().Equal("team");
        page.Result.Total.Should().Be(1);
        store.AllTallied.Should().BeEmpty("the only space shown is settled");
    }

    [Fact]
    public async Task RunAsync_NoDocumentsInDoubt_ProbesNothing()
    {
        Store store = new Store(new Space("team", 1, 1, 1)).Ready();

        (await List(store).RunAsync(Reader, new MemorySpaceQuery())).Succeeded.Should().BeTrue();

        store.Probed.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ProbeUnchecked_FailsWithNoCounts()
    {
        Store store = new Store(new Space("docs", 1, 0, 1, ContainerKind.Documents)) { ProbeFails = true }.Ready();

        var page = await List(store).RunAsync(Reader, new MemorySpaceQuery());

        page.Status.Should().Be(MemorySpaceStatus.Unchecked);
        store.Tallied.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_TallyUnchecked_FailsWithNoCounts()
    {
        Store store = new Store(new Space("team", 1, 0, 1)) { TallyFails = true }.Ready();

        var page = await List(store).RunAsync(Reader, new MemorySpaceQuery());

        page.Status.Should().Be(MemorySpaceStatus.Unchecked);
    }

    [Fact]
    public async Task RunAsync_SpaceGoneBeforeItsTally_IsLeftOffThePage()
    {
        Space gone = new("gone", 1, 0, 2), kept = new("kept", 1, 0, 2);
        Store store = new Store(gone, kept) { Gone = new HashSet<string> { gone.Id } }.Ready();

        var page = await List(store).RunAsync(Reader, new MemorySpaceQuery(Sort: MemorySpaceSort.Name));

        page.Result!.Items.Select(i => i.Name).Should().Equal("kept");
        page.Result.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_DocumentsEmptiedAfterTheProbe_IsLeftOffThePage()
    {
        // The probe sees a readable fact, then the page tally finds none: it expired or its source was hidden in between.
        Space emptied = new("emptied", 0, 0, 2, ContainerKind.Documents), team = new("team", 0, 0, 1);
        Store store = new Store(emptied, team).Ready();
        store.Fake.HoldingReadableAsync(Arg.Any<MemoryCaller>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { emptied.Id });

        var page = await List(store).RunAsync(Reader, new MemorySpaceQuery(Sort: MemorySpaceSort.Name));

        page.Result!.Items.Select(i => i.Name).Should().Equal("team");
    }

    [Fact]
    public async Task RunAsync_NothingListed_ChecksNothing()
    {
        Store store = new Store().Ready();

        var page = await List(store).RunAsync(Reader, new MemorySpaceQuery());

        page.Result!.Items.Should().BeEmpty();
        page.Result.NextCursor.Should().BeNull();
        store.Tallied.Should().BeEmpty();
        store.Probed.Should().BeEmpty();
    }
}
