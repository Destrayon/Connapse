using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Memory;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Tests.Support;
using CsCheck;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// The memory space operations' rules over random inputs (CON-240): who is refused, that a refused call never reaches
/// the store, and that paging through the list with its cursor visits every matching space once, in order. Each
/// expectation is worked out here from the rule. Every sample builds its own fakes, because CsCheck runs samples on
/// several threads.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class MemorySpaceOperationRandomInputTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A signed-in caller, with a user id when <c>owns</c> (so a space or star can be theirs) or without one.</summary>
    private static OperationContext Signed(bool owns, OperationSurface surface) =>
        new(new ClaimsPrincipal(new ClaimsIdentity(
            owns ? [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())] : [], "test")), surface);

    private static PolicyAuthorizer Authorizer(bool reader, bool writer) =>
        new(policy => policy switch { Policies.KnowledgeRead => reader, Policies.Writer or Policies.KnowledgeWrite => writer, _ => false });

    /// <summary>A space of any kind: a personal or team memory space, or a documents container.</summary>
    private static readonly Gen<Container> AnySpace = Gen.Select(Gen.Guid, Gen.String[Gen.Char["ab-1"], 1, 3], Gen.Int[0, 2],
        (id, name, kind) => new Container(id.ToString(), $"s{name}x", null, Epoch, Epoch,
            Kind: kind == 2 ? ContainerKind.Documents : ContainerKind.Memory,
            Audience: kind == 0 ? MemoryScope.Personal : MemoryScope.Team));

    private static MemorySpaceKind KindOf(Container c) =>
        c.Kind == ContainerKind.Documents ? MemorySpaceKind.Container : c.Audience == MemoryScope.Personal ? MemorySpaceKind.Personal : MemorySpaceKind.Team;

    /// <summary>A name as a request may give it: valid, padded, upper case, too short, blank, or missing.</summary>
    private static readonly Gen<(string? Name, bool Valid)> AnyName = Gen.OneOf(
        Gen.String[Gen.Char["abc-1"], 1, 6].Select(s => ((string?)$"n{s}z", true)),
        Gen.Const(((string?)"  padded-name  ", true)),
        Gen.Const(((string?)"Upper", false)),
        Gen.Const(((string?)"a", false)),
        Gen.Const(((string?)"-bad-", false)),
        Gen.Const(((string?)"   ", false)),
        Gen.Const(((string?)null, false)));

    // ── memorySpaces.list ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A space as the caller would see it, with the bounds the store gives before checking: up to two facts and two
    /// minutes either side. A documents container whose every fact is hidden still comes back as a candidate.
    /// </summary>
    private sealed record Truth(Container Container, int Facts, DateTimeOffset UpdatedAt, bool Starred, int Below, int Above, int Earlier, int Later)
    {
        public bool Listed => Container.Kind != ContainerKind.Documents || Facts > 0;

        public MemorySpaceCandidate Candidate => new(Container, Starred, Math.Max(0, Facts - Below),
            Container.Kind == ContainerKind.Documents ? Math.Max(1, Facts + Above) : Facts + Above,
            UpdatedAt.AddMinutes(-Earlier), UpdatedAt.AddMinutes(Later));
    }

    private static readonly Gen<Truth> AnyTruth = Gen.Select(AnySpace, Gen.Int[0, 3], Gen.Int[2, 6], Gen.Bool, Gen.Int[0, 2], Gen.Int[0, 2],
        Gen.Int[0, 2], Gen.Int[0, 2],
        (c, facts, minutes, starred, below, above, earlier, later) =>
            new Truth(c, facts, new DateTimeOffset(Epoch).AddMinutes(minutes), starred, below, above, earlier, later));

    /// <summary>A store that knows each space's exact values and answers the operation's questions from them.</summary>
    private static IMemorySpaceStore StoreOf(IReadOnlyList<Truth> truths, bool unchecked_)
    {
        IMemorySpaceStore store = Substitute.For<IMemorySpaceStore>();
        store.CandidatesAsync(Arg.Any<MemoryCaller>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(truths.Select(t => t.Candidate).ToList());
        store.TallyAsync(Arg.Any<MemoryCaller>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => unchecked_ ? null : truths
                .Where(t => call.ArgAt<IReadOnlyCollection<string>>(1).Contains(t.Container.Id))
                .ToDictionary(t => t.Container.Id, t => new MemorySpaceTally(t.Facts, t.UpdatedAt)));
        store.HoldingReadableAsync(Arg.Any<MemoryCaller>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => unchecked_ ? null : truths
                .Where(t => call.ArgAt<IReadOnlyCollection<string>>(1).Contains(t.Container.Id) && t.Facts > 0)
                .Select(t => t.Container.Id).ToHashSet());
        return store;
    }

    [Fact]
    [RandomInputTestOf(typeof(ListMemorySpaces))]
    public Task List_AnyQuery_PagesVisitEveryMatchingSpaceOnceInOrder() =>
        Gen.Select(AnyTruth.List[0, 12], Gen.Bool, Gen.Bool, Gen.Int[-1, 3], Gen.Int[0, 3], Gen.Int[-1, 6], Gen.Bool)
            .SampleAsync(async s =>
            {
                var (truths, reader, unchecked_, kindValue, sortValue, limitValue, starredOnly) = s;
                MemorySpaceKind? kind = kindValue < 0 ? null : (MemorySpaceKind)kindValue;
                var sort = (MemorySpaceSort)sortValue;
                int? limit = limitValue < 0 ? null : limitValue;
                IMemorySpaceStore store = StoreOf(truths, unchecked_);
                var list = new ListMemorySpaces(store, Authorizer(reader, false));
                MemorySpaceQuery query = new(kind, null, starredOnly, sort, limit);

                var first = await list.RunAsync(Signed(true, OperationSurface.Rest), query);

                MemorySpaceStatus expected = !reader ? MemorySpaceStatus.Forbidden
                    : kindValue > 2 ? MemorySpaceStatus.Invalid
                    : sortValue > 2 ? MemorySpaceStatus.Invalid
                    : limit is < 1 or > ListMemorySpaces.MaxLimit ? MemorySpaceStatus.InvalidPage
                    : MemorySpaceStatus.Success;
                // Access goes unchecked only when something had to be checked.
                bool asked = store.ReceivedCalls().Any(c => c.GetMethodInfo().Name is nameof(IMemorySpaceStore.TallyAsync)
                    or nameof(IMemorySpaceStore.HoldingReadableAsync));
                if (expected == MemorySpaceStatus.Success && unchecked_ && asked)
                    expected = MemorySpaceStatus.Unchecked;
                first.Status.Should().Be(expected);
                if (expected != MemorySpaceStatus.Success)
                {
                    if (expected is not MemorySpaceStatus.Unchecked)
                        store.ReceivedCalls().Should().BeEmpty();
                    return;
                }

                List<Truth> listed = [.. truths.Where(t => t.Listed)];
                // The order, written out: starred first, then the sort's key, then the id.
                List<Truth> want = [.. listed
                    .Where(x => kind is not { } k || KindOf(x.Container) == k)
                    .Where(x => !starredOnly || x.Starred)
                    .OrderByDescending(x => x.Starred)
                    .ThenByDescending(x => sort == MemorySpaceSort.Facts ? x.Facts : sort == MemorySpaceSort.Updated ? x.UpdatedAt.UtcTicks : 0)
                    .ThenBy(x => sort == MemorySpaceSort.Name ? x.Container.Name : "", StringComparer.Ordinal)
                    .ThenBy(x => x.Container.Id, StringComparer.Ordinal)];

                List<string> seen = [];
                var page = first;
                int pages = 0;
                while (true)
                {
                    page.Result!.Total.Should().Be(want.Count);
                    page.Result.Items.Count.Should().BeLessThanOrEqualTo(limit ?? ListMemorySpaces.DefaultLimit);
                    seen.AddRange(page.Result.Items.Select(i => i.Id));
                    page.Result.Items.Should().OnlyContain(i => want.Single(w => w.Container.Id == i.Id).Facts == i.Facts,
                        "every space shown carries its exact count");
                    if (page.Result.NextCursor is not { } cursor)
                        break;
                    if (unchecked_)
                        return;
                    (++pages).Should().BeLessThan(20);
                    page = await list.RunAsync(Signed(true, OperationSurface.Rest), query with { Cursor = cursor });
                    page.Status.Should().Be(MemorySpaceStatus.Success);
                }
                seen.Should().Equal(want.Select(x => x.Container.Id));
                first.Result!.Counts.Should().Be(new MemorySpaceCounts(
                    listed.Count, listed.Count(x => x.Starred),
                    listed.Count(x => KindOf(x.Container) == MemorySpaceKind.Personal),
                    listed.Count(x => KindOf(x.Container) == MemorySpaceKind.Team),
                    listed.Count(x => KindOf(x.Container) == MemorySpaceKind.Container)));

                // A cursor from one sort is refused by another.
                if (first.Result.NextCursor is { } issued)
                {
                    var other = await list.RunAsync(Signed(true, OperationSurface.Rest),
                        query with { Cursor = issued, Sort = (MemorySpaceSort)((sortValue + 1) % 3) });
                    other.Status.Should().Be(MemorySpaceStatus.InvalidPage);
                }
            }, iter: 500);

    // ── memorySpaces.create ───────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(CreateMemorySpace))]
    public Task Create_AnyRequest_MakesOnlyValidPersonalOrTeamSpacesForWriters() =>
        Gen.Select(Gen.Bool, Gen.Bool, AnyName, Gen.Int[-1, 3], Gen.Bool)
            .SampleAsync(async s =>
            {
                var (writer, owns, (name, valid), kindValue, taken) = s;
                MemorySpaceKind? kind = kindValue < 0 ? null : (MemorySpaceKind)kindValue;
                IContainerStore containers = ContainerStoreFakes.Create();
                IMemorySpaceStore spaces = Substitute.For<IMemorySpaceStore>();
                string trimmed = name?.Trim() ?? "";
                Container made = new(Guid.NewGuid().ToString(), trimmed, null, Epoch, Epoch, Kind: ContainerKind.Memory);
                containers.GetByNameAsync(trimmed, Arg.Any<CancellationToken>()).Returns(taken ? made : null);
                containers.CreateAsync(Arg.Any<CreateContainerRequest>(), Arg.Any<CancellationToken>()).Returns(made);
                spaces.CreatePersonalAsync(Arg.Any<MemoryCaller>(), trimmed, Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(taken ? null : made with { Audience = MemoryScope.Personal });

                var result = await new CreateMemorySpace(containers, spaces, NoAudit.Instance, Authorizer(true, writer))
                    .RunAsync(Signed(owns, OperationSurface.Rest), new NewMemorySpace(name, null, kind));

                MemorySpaceStatus expected = !writer ? MemorySpaceStatus.Forbidden
                    : !valid ? MemorySpaceStatus.Invalid
                    : kind is not (MemorySpaceKind.Personal or MemorySpaceKind.Team) ? MemorySpaceStatus.Invalid
                    : kind == MemorySpaceKind.Personal && !owns ? MemorySpaceStatus.Forbidden
                    : taken ? MemorySpaceStatus.Conflict
                    : MemorySpaceStatus.Success;
                result.Status.Should().Be(expected);
                bool madeTeam = expected == MemorySpaceStatus.Success && kind == MemorySpaceKind.Team;
                await containers.Received(madeTeam ? 1 : 0).CreateAsync(
                    Arg.Is<CreateContainerRequest>(r => r.Kind == ContainerKind.Memory && r.Name == trimmed), Arg.Any<CancellationToken>());
                bool askedPersonal = writer && valid && kind == MemorySpaceKind.Personal && owns;
                await spaces.Received(askedPersonal ? 1 : 0).CreatePersonalAsync(
                    Arg.Any<MemoryCaller>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
                if (expected == MemorySpaceStatus.Success)
                    result.Result!.Kind.Should().Be(kind!.Value);
            }, iter: 500);

    // ── memorySpaces.update ───────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(UpdateMemorySpace))]
    public Task Update_AnyChange_RenamesOnlyVisibleMemorySpacesForWriters() =>
        Gen.Select(Gen.Bool, AnySpace, Gen.Bool, AnyName, Gen.Bool, Gen.Bool, Gen.Bool)
            .SampleAsync(async s =>
            {
                var (writer, space, exists, (name, valid), renames, describes, taken) = s;
                Guid id = Guid.Parse(space.Id);
                IContainerStore containers = ContainerStoreFakes.Create();
                containers.GetAsync(id, Arg.Any<CancellationToken>()).Returns(exists ? space : null);
                string trimmed = name?.Trim() ?? "";
                containers.GetByNameAsync(trimmed, Arg.Any<CancellationToken>())
                    .Returns(taken ? space with { Id = Guid.NewGuid().ToString(), Name = trimmed } : null);
                containers.UpdateAsync(id, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(call => space with { Name = call.ArgAt<string?>(1) ?? space.Name });
                MemorySpaceChanges changes = new(renames ? name : null, describes ? " about " : null);

                var result = await new UpdateMemorySpace(containers, NoAudit.Instance, Authorizer(true, writer))
                    .RunAsync(Signed(true, OperationSurface.Rest), id, changes);

                bool nameGiven = renames && name is not null;
                MemorySpaceStatus expected = !writer ? MemorySpaceStatus.Forbidden
                    : !nameGiven && !describes ? MemorySpaceStatus.Invalid
                    : nameGiven && !valid ? MemorySpaceStatus.Invalid
                    : !exists ? MemorySpaceStatus.NotFound
                    : space.Kind == ContainerKind.Documents ? MemorySpaceStatus.DocumentContainer
                    : nameGiven && trimmed != space.Name && taken ? MemorySpaceStatus.Conflict
                    : MemorySpaceStatus.Success;
                result.Status.Should().Be(expected);
                await containers.Received(expected == MemorySpaceStatus.Success ? 1 : 0).UpdateAsync(
                    id, nameGiven ? trimmed : null, describes ? "about" : null, Arg.Any<CancellationToken>());
            }, iter: 500);

    // ── memorySpaces.delete ───────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(DeleteMemorySpace))]
    public Task Delete_AnyCall_DeletesOnlyVisibleMemorySpacesForWritersAndNeverOnADryRun() =>
        Gen.Select(Gen.Bool, Gen.Bool, AnySpace, Gen.Bool, Gen.Bool, Gen.Int[0, 5], Gen.Int[0, 3], Gen.Bool)
            .SampleAsync(async s =>
            {
                var (reader, writer, space, exists, dryRun, facts, authors, checkedSources) = s;
                Guid id = Guid.Parse(space.Id);
                IContainerStore containers = ContainerStoreFakes.Create();
                IMemorySpaceStore spaces = Substitute.For<IMemorySpaceStore>();
                containers.GetAsync(id, Arg.Any<CancellationToken>()).Returns(exists ? space : null);
                containers.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(true);
                spaces.ImpactAsync(id, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                    .Returns(checkedSources ? new MemorySpaceImpact(facts, facts * 2, authors) : null);

                var result = await new DeleteMemorySpace(containers, spaces, NoAudit.Instance, Authorizer(reader, writer))
                    .RunAsync(Signed(true, OperationSurface.Rest), id, dryRun);

                MemorySpaceStatus expected = !writer || !reader ? MemorySpaceStatus.Forbidden
                    : !exists ? MemorySpaceStatus.NotFound
                    : space.Kind == ContainerKind.Documents ? MemorySpaceStatus.DocumentContainer
                    : !checkedSources ? MemorySpaceStatus.Unchecked
                    : MemorySpaceStatus.Success;
                result.Status.Should().Be(expected);
                await containers.Received(expected == MemorySpaceStatus.Success && !dryRun ? 1 : 0).DeleteAsync(id, Arg.Any<CancellationToken>());
                if (expected == MemorySpaceStatus.Success)
                    result.Result.Should().Be(new MemorySpaceDeletion(space.Id, space.Name, KindOf(space), facts, facts * 2, authors, !dryRun));
            }, iter: 500);

    // ── memorySpaces.star / memorySpaces.unstar ───────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(StarMemorySpace))]
    [RandomInputTestOf(typeof(UnstarMemorySpace))]
    public Task Star_AnyCall_ChangesOnlyTheCallersStarOnASpaceTheyCanSee() =>
        Gen.Select(Gen.Bool, Gen.Bool, AnySpace, Gen.Bool, Gen.Bool)
            .SampleAsync(async s =>
            {
                var (reader, owns, space, exists, starring) = s;
                Guid id = Guid.Parse(space.Id);
                IContainerStore containers = ContainerStoreFakes.Create();
                IMemorySpaceStore spaces = Substitute.For<IMemorySpaceStore>();
                containers.GetAsync(id, Arg.Any<CancellationToken>()).Returns(exists ? space : null);
                OperationContext caller = Signed(owns, OperationSurface.Rest);
                PolicyAuthorizer authorizer = Authorizer(reader, false);

                var result = starring
                    ? await new StarMemorySpace(containers, spaces, authorizer).RunAsync(caller, id)
                    : await new UnstarMemorySpace(containers, spaces, authorizer).RunAsync(caller, id);

                MemorySpaceStatus expected = !reader || !owns ? MemorySpaceStatus.Forbidden
                    : !exists ? MemorySpaceStatus.NotFound
                    : MemorySpaceStatus.Success;
                result.Status.Should().Be(expected);
                bool applied = expected == MemorySpaceStatus.Success;
                await spaces.Received(applied && starring ? 1 : 0).StarAsync(id, Arg.Is<MemoryCaller>(c => c.UserId != null), Arg.Any<CancellationToken>());
                await spaces.Received(applied && !starring ? 1 : 0).UnstarAsync(id, Arg.Is<MemoryCaller>(c => c.UserId != null), Arg.Any<CancellationToken>());
            }, iter: 500);
}
