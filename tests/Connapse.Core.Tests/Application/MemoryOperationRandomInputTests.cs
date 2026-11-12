using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Memory;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using CsCheck;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// The memory operations' rules over random inputs: who is refused, that a refused or unresolved call never reaches
/// memory, and how memory's typed failures become statuses. Each expectation is worked out here from the rule. Every
/// sample builds its own fakes, because CsCheck runs samples on several threads.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class MemoryOperationRandomInputTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Gen<OperationContext> AnyCaller =
        Gen.Enum<OperationSurface>().Select(s => new OperationContext(new ClaimsPrincipal(new ClaimsIdentity("test")), s));

    private static readonly Gen<Container> AnyContainer = Gen.Select(Gen.Guid, Gen.String[Gen.Char["ab-1"], 2, 4],
        (id, name) => new Container(id.ToString(), $"c{name}x", null, Epoch, Epoch, Kind: ContainerKind.Memory));

    /// <summary>How a call names the container: its id, its name in any case and spacing, a stranger's id, another name, or blank.</summary>
    private static Gen<(string Key, bool Names)> KeyFor(Container container) => Gen.OneOf(
        Gen.Const((container.Id, true)),
        Gen.Const(($" {container.Name.ToUpperInvariant()} ", true)),
        Gen.Const((container.Name, true)),
        Gen.Guid.Select(g => (g.ToString(), false)),
        Gen.Const(("zz-elsewhere", false)),
        Gen.Const(("  ", false)));

    /// <summary>A fact id: a real one, or text that isn't an id.</summary>
    private static readonly Gen<(string Text, bool IsId)> FactId = Gen.OneOf(
        Gen.Guid.Select(g => (g.ToString(), true)),
        Gen.String[Gen.Char["xyz-1"], 0, 6].Select(s => (s, false)));

    private static readonly NewMemoryFact AFact = new("Halcyon", "database", "PostgreSQL 15", null, null, []);

    private static MemoryFact Stored(Guid container) =>
        new(Guid.NewGuid(), Guid.NewGuid(), container, "Halcyon", "database", "PostgreSQL 15", null, null,
            DateTimeOffset.UnixEpoch, null, null, null, new MemoryCaller(null, null, null), []);

    /// <summary>The status a write failure must become, written out.</summary>
    private static MemoryStatus StatusOf(MemoryWriteFailure failure) => failure switch
    {
        MemoryWriteFailure.None => MemoryStatus.Success,
        MemoryWriteFailure.NotFound => MemoryStatus.FactNotFound,
        MemoryWriteFailure.Unchecked => MemoryStatus.Unchecked,
        MemoryWriteFailure.Forbidden => MemoryStatus.Forbidden,
        _ => MemoryStatus.Invalid,
    };

    /// <summary>A scope a call can ask for: none, personal, team, or a value that is neither.</summary>
    private static readonly Gen<MemoryScope?> AnyScope = Gen.Int[-1, 2].Select(v => v < 0 ? (MemoryScope?)null : (MemoryScope)v);

    private sealed class World
    {
        public readonly IContainerStore Containers = ContainerStoreFakes.Create();
        public readonly IMemoryService Memory = Substitute.For<IMemoryService>();
        public readonly PolicyAuthorizer Authorizer;

        public World(bool reader, bool writer, Container container, bool exists)
        {
            Authorizer = new PolicyAuthorizer(policy => policy switch { Policies.KnowledgeRead => reader, Policies.Writer or Policies.KnowledgeWrite => writer, _ => false });
            Containers.GetAsync(Guid.Parse(container.Id), Arg.Any<CancellationToken>()).Returns(exists ? container : null);
            Containers.GetByNameAsync(container.Name, Arg.Any<CancellationToken>()).Returns(exists ? container : null);
        }

        public MemoryWriteResult Answers(MemoryWriteFailure failure, Guid container) =>
            failure == MemoryWriteFailure.None
                ? new MemoryWriteResult(Stored(container), null, null) { Matches = [new MemoryFactMatch(Stored(container), MemoryMatchKind.SameSubject)] }
                : MemoryWriteResult.Failed("no", failure);
    }

    // ── memory.remember ───────────────────────────────────────────────────────────────────────────

    /// <summary>A signed-in caller, with a user id when <c>owns</c> (so a personal space can be theirs) or without one.</summary>
    private static OperationContext Signed(bool owns, OperationSurface surface) =>
        new(new ClaimsPrincipal(new ClaimsIdentity(
            owns ? [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())] : [], "test")), surface);

    /// <summary>A memory space or container of any kind and audience; only memory spaces are ever personal.</summary>
    private static readonly Gen<Container> AnySpace = Gen.Select(AnyContainer, Gen.Bool, Gen.Bool,
        (c, memory, personal) => c with
        {
            Kind = memory ? ContainerKind.Memory : ContainerKind.Documents,
            Audience = memory && personal ? MemoryScope.Personal : MemoryScope.Team,
        });

    [Fact]
    [RandomInputTestOf(typeof(RememberFact))]
    public Task Remember_AnyCall_SavesWhereTheSpaceOrTheOldRulesSayAndOnlyForWriters() =>
        Gen.Select(Gen.Enum<OperationSurface>(), Gen.Bool, Gen.Bool, AnySpace, Gen.Bool, Gen.Enum<MemoryWriteFailure>(), AnyScope)
            .SelectMany(t => KeyFor(t.Item4).SelectMany(key => Gen.Select(Gen.Bool, Gen.Bool, Gen.Bool, (cites, asSpace, myMemory) => (t, key, cites, asSpace, myMemory))))
            .SampleAsync(async s =>
            {
                var ((surface, owns, writer, container, exists, failure, scope), key, cites, asSpace, myMemory) = s;
                OperationContext caller = Signed(owns, surface);
                string named = myMemory ? " My-Memory " : key.Key;
                bool resolves = !myMemory && exists && key.Names;
                NewMemoryFact fact = AFact with { Scope = scope, Sources = cites ? [new MemorySource(MemorySourceKind.Document, Guid.Empty.ToString())] : [] };
                var world = new World(true, writer, container, exists);
                Guid ensuredId = Guid.NewGuid();
                world.Containers.EnsurePersonalSpaceAsync(Arg.Any<MemoryCaller>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(call => new Container(ensuredId.ToString(), call.ArgAt<string>(1), null, Epoch, Epoch, Kind: ContainerKind.Memory, Audience: MemoryScope.Personal));
                world.Memory.RememberAsync(Arg.Any<Guid>(), Arg.Any<NewMemoryFact>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                    .Returns(call => world.Answers(failure, call.ArgAt<Guid>(0)));

                var target = asSpace ? MemoryTarget.Space(named) : MemoryTarget.Container(named);
                var written = await new RememberFact(world.Containers, world.Memory, world.Authorizer).RunAsync(caller, target, fact);

                // The rules, written out. A space decides the scope; an old-style container keeps sourceless facts personal.
                (MemoryStatus Status, Guid? SavedTo, MemoryScope Scope, string? Made) Expected()
                {
                    if (!writer)
                        return (MemoryStatus.Forbidden, null, default, null);
                    if (scope is { } asked && (int)asked > 1)
                        return (MemoryStatus.Invalid, null, default, null);
                    Container? space = resolves ? container : null;
                    if (space?.Kind == ContainerKind.Documents)
                        return (MemoryStatus.Invalid, null, default, null);
                    string? made = null;
                    // CON-291: "my-memory" is a name like any other, so one nobody made is not found.
                    if (space is null)
                        return (MemoryStatus.ContainerNotFound, null, default, null);
                    bool personalSpace = space.Audience == MemoryScope.Personal;
                    MemoryScope effective = asSpace ? space.Audience
                        : scope ?? (personalSpace || !cites ? MemoryScope.Personal : MemoryScope.Team);
                    if (scope is { } requested && requested != effective)
                        return (MemoryStatus.Invalid, null, default, made);
                    if (effective == MemoryScope.Team && (personalSpace || (!cites && !asSpace)))
                        return (MemoryStatus.Invalid, null, default, made);
                    if (effective == MemoryScope.Personal && !personalSpace)
                    {
                        if (!owns)
                            return (MemoryStatus.Forbidden, null, default, made);
                        return (StatusOf(failure), ensuredId, effective, $"{space.Name}-personal");
                    }
                    return (StatusOf(failure), Guid.Parse(space.Id), effective, made);
                }

                var expected = Expected();
                written.Status.Should().Be(expected.Status);
                if (expected.SavedTo is { } savedTo)
                {
                    await world.Memory.Received(1).RememberAsync(savedTo, fact with { Scope = expected.Scope }, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
                    if (written.Succeeded)
                    {
                        written.Result!.SavedTo!.Id.Should().Be(savedTo.ToString());
                        written.Result.Matches.Should().ContainSingle("the facts a save may repeat pass through unchanged (CON-332)");
                    }
                }
                else
                    world.Memory.ReceivedCalls().Should().BeEmpty();
                if (expected.Made is { } made)
                    await world.Containers.Received(1).EnsurePersonalSpaceAsync(Arg.Any<MemoryCaller>(), made, Arg.Any<string?>(), Arg.Any<CancellationToken>());
                else
                    await world.Containers.DidNotReceive().EnsurePersonalSpaceAsync(Arg.Any<MemoryCaller>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            }, iter: 2000);

    // ── memory.correct ────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(CorrectFact))]
    public Task Correct_AnyCall_ReachesMemoryOnlyWithAWriterWhoCanReadARealFactIdAndAKnownKind() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, Gen.Enum<MemoryWriteFailure>(), FactId, Gen.Int[0, 3])
            .SelectMany(t => KeyFor(t.Item3).SelectMany(key => Gen.Bool.Select(reader => (t, key, reader))))
            .SampleAsync(async s =>
            {
                var ((caller, writer, container, exists, failure, factId, kindValue), key, reader) = s;
                var kind = (MemoryCorrection)kindValue;
                var world = new World(reader, writer, container, exists);
                Guid id = Guid.Parse(container.Id);
                world.Memory.CorrectAsync(Arg.Any<Guid>(), kind, AFact, Arg.Any<MemoryCaller>(), true, Arg.Any<CancellationToken>())
                    .Returns(world.Answers(failure, id));

                // The container key is ignored: a fact is found by id wherever it lives, even after it moved into a space.
                var written = await new CorrectFact(world.Containers, world.Memory, world.Authorizer).RunAsync(caller, key.Key, factId.Text, kind, AFact);

                MemoryStatus expected = !(writer && reader) ? MemoryStatus.Forbidden
                    : !factId.IsId ? MemoryStatus.FactNotFound
                    : kindValue > 1 ? MemoryStatus.Invalid
                    : StatusOf(failure);
                written.Status.Should().Be(expected);
                bool reached = writer && reader && factId.IsId && kindValue <= 1;
                world.Memory.ReceivedCalls().Should().HaveCount(reached ? 2 : 0, "where the fact is, then the correction");
                world.Containers.ReceivedCalls().Should().BeEmpty();
            }, iter: 1000);

    [Theory]
    [InlineData(MemoryScope.Team, false, false, false)]
    [InlineData(MemoryScope.Team, true, false, false)]
    [InlineData(MemoryScope.Team, false, true, false)]
    [InlineData(MemoryScope.Team, true, true, true)]
    [InlineData(MemoryScope.Personal, false, false, false)]
    [InlineData(MemoryScope.Personal, true, false, false)]
    [InlineData(MemoryScope.Personal, false, true, false)]
    [InlineData(MemoryScope.Personal, true, true, true)]
    public async Task Remember_AnyScope_NeedsTheWriterRoleAndTheWriteScope(MemoryScope scope, bool writerRole, bool writeScope, bool expected)
    {
        // An Editor's knowledge:read token passes the writer role but not the scope; it must not save memory at all.
        var authorizer = new PolicyAuthorizer(policy => policy switch
        {
            Policies.Writer => writerRole,
            Policies.KnowledgeWrite => writeScope,
            _ => false,
        });

        Container container = AnyContainer.Single() with { Kind = ContainerKind.Memory, Audience = scope };
        var world = new World(true, false, container, true);
        world.Memory.RememberAsync(Guid.Parse(container.Id), Arg.Any<NewMemoryFact>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(world.Answers(MemoryWriteFailure.None, Guid.Parse(container.Id)));

        var written = await new RememberFact(world.Containers, world.Memory, authorizer).RunAsync(
            Signed(true, OperationSurface.Mcp), MemoryTarget.Space(container.Id),
            new NewMemoryFact("Halcyon", "database", "PostgreSQL", null, null, [], null, scope));

        written.Status.Should().Be(expected ? MemoryStatus.Success : MemoryStatus.Forbidden);
    }

    // ── memory.forget ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(ForgetFact))]
    public Task Forget_AnyCall_DeletesOnlyForAWriterAndHidesWhyAFactWasNotFound() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, AnyContainer, Gen.Bool, FactId, Gen.Int[0, 4])
            .SelectMany(t => KeyFor(t.Item4).SelectMany(key => Gen.OneOfConst(MemoryWriteFailure.None, MemoryWriteFailure.NotFound, MemoryWriteFailure.Forbidden, MemoryWriteFailure.Unchecked)
                .Select(failure => (t, key, failure))))
            .SampleAsync(async s =>
            {
                var ((caller, reader, writer, container, exists, factId, deleted), key, failure) = s;
                var world = new World(reader, writer, container, exists);
                Guid id = Guid.Parse(container.Id);
                world.Memory.ForgetAsync(Arg.Any<Guid>(), Arg.Any<MemoryCaller>(), true, Arg.Any<CancellationToken>())
                    .Returns((failure == MemoryWriteFailure.None ? deleted : 0, failure));

                // The container key is ignored: a fact is found by id wherever it lives, even after it moved into a space.
                var forgotten = await new ForgetFact(world.Memory, world.Authorizer).RunAsync(caller, key.Key, factId.Text);

                bool reached = writer && factId.IsId;
                MemoryStatus expected = !writer ? MemoryStatus.Forbidden
                    : !factId.IsId ? MemoryStatus.FactNotFound
                    : failure == MemoryWriteFailure.Forbidden ? MemoryStatus.Forbidden
                    : failure == MemoryWriteFailure.NotFound ? MemoryStatus.FactNotFound
                    : failure == MemoryWriteFailure.Unchecked ? MemoryStatus.Unchecked
                    : MemoryStatus.Success;
                forgotten.Status.Should().Be(expected);
                world.Memory.ReceivedCalls().Should().HaveCount(reached ? 1 : 0);
                world.Containers.ReceivedCalls().Should().BeEmpty();
                if (expected == MemoryStatus.Success)
                    forgotten.Result.Should().Be(deleted);
            }, iter: 1000);

    // ── memory.recall ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(RecallMemory))]
    public Task Recall_AnyCall_ReadsOnlyTheNamedContainerAndShowsNothingWhenAccessIsUnchecked() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, Gen.Bool, Gen.Guid, Gen.Int[0, 3])
            .SelectMany(t => KeyFor(t.Item3).Select(key => (t, key)))
            .SampleAsync(async s =>
            {
                var ((caller, reader, container, exists, denied, claimed, count), key) = s;
                var world = new World(reader, writer: false, container, exists);
                Guid id = Guid.Parse(container.Id);
                var facts = Enumerable.Range(0, count).Select(_ => new RecalledFact(Stored(id), [])).ToList();
                world.Memory.RecallAsync(Arg.Any<MemoryQuery>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                    .Returns(denied ? MemoryRecall.DeniedResult : new MemoryRecall(facts));

                // The query names other containers; the one the call names must win.
                var recalled = await new RecallMemory(world.Containers, world.Memory, world.Authorizer)
                    .RunAsync(caller, key.Key, new MemoryQuery(claimed, Text: "db", ContainerIds: [claimed]));

                MemoryStatus expected = !reader ? MemoryStatus.Forbidden
                    : !(exists && key.Names) ? MemoryStatus.ContainerNotFound
                    : denied ? MemoryStatus.Unchecked
                    : MemoryStatus.Success;
                recalled.Status.Should().Be(expected);
                if (expected is MemoryStatus.Forbidden or MemoryStatus.ContainerNotFound)
                {
                    world.Memory.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                await world.Memory.Received(1).RecallAsync(
                    Arg.Is<MemoryQuery>(q => q.ContainerId == id && q.ContainerIds == null && q.Text == "db"),
                    Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
                if (expected == MemoryStatus.Success)
                    recalled.Result.Should().Equal(facts);
            }, iter: 1000);

    /// <summary>A space the caller may see, with how many current facts it holds and when it last changed.</summary>
    private static readonly Gen<MemorySpaceSummary> AnyListedSpace = Gen.Select(AnySpace, Gen.Int[0, 2], Gen.Int[0, 3],
        (c, facts, minutes) => new MemorySpaceSummary(c, facts, new DateTimeOffset(Epoch).AddMinutes(minutes), false));

    [Fact]
    [RandomInputTestOf(typeof(RecallMemory))]
    public Task PageAsync_AnyListing_RefusesWhatItMustAndOtherwiseReadsOnePageWithACursorOnlyWhenMoreRemain() =>
        Gen.Select(Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, Gen.Int[-1, 102]),
                Gen.Int[0, 4], Gen.Int[0, 2], Gen.Bool, Gen.Bool, Gen.Bool, (t, sort, status, text, denied, more) => (t, sort, status, text, denied, more))
            .SampleAsync(async sample =>
            {
                var ((caller, reader, space, exists, limit), sortValue, statusValue, text, denied, more) = sample;
                var world = new World(reader, writer: false, space, exists);
                Guid id = Guid.Parse(space.Id);
                MemoryFactSort sort = (MemoryFactSort)sortValue;
                MemoryFactFilter filter = new(Text: text ? "db" : null, Status: (MemoryFactStatus)statusValue);
                world.Memory.ListAsync(id, Arg.Any<MemoryFactFilter>(), Arg.Any<MemoryFactSort>(), Arg.Any<MemoryFactKey?>(), Arg.Any<int>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                    .Returns(new MemoryFactPage([new RecalledFact(Stored(id), [])], more ? new MemoryFactKey(Guid.NewGuid(), Recorded: DateTimeOffset.UnixEpoch) : null, denied));

                var paged = await new RecallMemory(world.Containers, world.Memory, world.Authorizer)
                    .PageAsync(caller, space.Name, new MemoryFactListing(filter, sort, Limit: limit));

                (MemoryStatus Status, string? Field) expected =
                    !reader ? (MemoryStatus.Forbidden, (string?)null)
                    : !exists ? (MemoryStatus.ContainerNotFound, (string?)null)
                    : statusValue > 1 ? (MemoryStatus.Invalid, (string?)"status")
                    : sortValue > 3 ? (MemoryStatus.Invalid, (string?)"sort")
                    : limit is < 1 or > 100 ? (MemoryStatus.Invalid, (string?)"limit")
                    : denied ? (MemoryStatus.Unchecked, (string?)null)
                    : (MemoryStatus.Success, (string?)null);
                paged.Status.Should().Be(expected.Status);
                if (expected.Field is not null)
                    paged.Field.Should().Be(expected.Field);
                if (expected.Status is not (MemoryStatus.Success or MemoryStatus.Unchecked))
                {
                    world.Memory.ReceivedCalls().Should().BeEmpty();
                    return;
                }

                MemoryFactSort read = sort == MemoryFactSort.Relevance && !text ? MemoryFactSort.Recorded : sort;
                await world.Memory.Received(1).ListAsync(id, filter, read, null, limit, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
                if (denied)
                    return;
                paged.Result!.Facts.Should().ContainSingle();
                paged.Result.Spaces.Keys.Should().Equal(id);
                (paged.Result.NextCursor is not null).Should().Be(more);
            });

    [Fact]
    [RandomInputTestOf(typeof(RecallMemory))]
    public Task SpacesAsync_NoSpaceNamed_ReadsEverySpaceWithFactsInOneRecall() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyListedSpace.List[0, 6], Gen.Bool, Gen.Bool, Gen.Int[0, 3])
            .SampleAsync(async sample =>
            {
                var (caller, reader, listed, unchecked_, history, count) = sample;
                var world = new World(reader, writer: false, new Container(Guid.NewGuid().ToString(), "unused", null, Epoch, Epoch), exists: false);
                IMemorySpaceStore spaces = Substitute.For<IMemorySpaceStore>();
                spaces.ListAsync(Arg.Any<MemoryCaller>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(unchecked_ ? null : listed);
                var facts = Enumerable.Range(0, count).Select(_ => new RecalledFact(Stored(Guid.NewGuid()), [])).ToList();
                world.Memory.RecallAsync(Arg.Any<MemoryQuery>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                    .Returns(new MemoryRecall(facts));

                MemoryQuery query = new(Guid.Empty, Text: "db", History: history);
                var recalled = await new RecallMemory(world.Containers, world.Memory, world.Authorizer, spaces)
                    .SpacesAsync(caller, " ", query);

                // Spaces read: those with facts, or every one for history, most recently changed first.
                List<Guid> read = [.. listed.Where(x => history || x.Facts > 0)
                    .OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Container.Id, StringComparer.Ordinal)
                    .Select(x => Guid.Parse(x.Container.Id))];
                MemoryStatus expected = !reader ? MemoryStatus.Forbidden
                    : unchecked_ ? MemoryStatus.Unchecked
                    : MemoryStatus.Success;
                recalled.Status.Should().Be(expected);
                if (!reader)
                    spaces.ReceivedCalls().Should().BeEmpty();
                if (expected != MemoryStatus.Success || read.Count == 0)
                {
                    world.Memory.ReceivedCalls().Should().BeEmpty();
                    if (expected == MemoryStatus.Success)
                        recalled.Result!.Facts.Should().BeEmpty();
                    return;
                }

                // Every space is ranked in the one read, never one read per space.
                await world.Memory.Received(1).RecallAsync(
                    Arg.Is<MemoryQuery>(q => q.Text == "db" && q.History == history && read.Contains(q.ContainerId)
                        && (read.Count == 1 ? q.ContainerIds == null : q.ContainerIds != null && q.ContainerIds.Count == read.Count && read.ToHashSet().SetEquals(q.ContainerIds))),
                    Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
                recalled.Result!.Facts.Should().Equal(facts);
                recalled.Result.Spaces.Keys.Should().BeEquivalentTo(read);
                recalled.Result.SkippedSpaces.Should().Be(0);
            }, iter: 1000);

    /// <summary>A recall request's optional parts: each present or not, with a limit in and out of range.</summary>
    private static readonly Gen<(bool Space, bool AsOf, bool Attribute, bool Text, bool History, bool Cursor, int Limit)> AnyRecallShape =
        Gen.Select(Gen.Select(Gen.Bool, Gen.Bool, Gen.Bool, Gen.Bool), Gen.Bool, Gen.Bool, Gen.Int[-3, 140],
            (a, history, cursor, limit) => (a.Item1, a.Item2, a.Item3, a.Item4, history, cursor, limit));

    [Fact]
    [RandomInputTestOf(typeof(RecallMemory))]
    public Task RecallAsync_AnyRequest_PagesOneNamedSpaceWithoutAPointInTimeOrAttribute_AndReadsEveryOtherOneWhole() =>
        Gen.Select(AnyContainer, AnyRecallShape).SampleAsync(async sample =>
        {
            var (container, shape) = sample;
            Container space = container with { Kind = ContainerKind.Memory };
            var world = new World(reader: true, writer: false, space, exists: true);
            Guid id = Guid.Parse(space.Id);
            IMemorySpaceStore spaces = Substitute.For<IMemorySpaceStore>();
            spaces.ListAsync(Arg.Any<MemoryCaller>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns([new MemorySpaceSummary(space, 1, new DateTimeOffset(Epoch), false)]);
            world.Memory.ListAsync(id, Arg.Any<MemoryFactFilter>(), Arg.Any<MemoryFactSort>(), Arg.Any<MemoryFactKey?>(), Arg.Any<int>(),
                    Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                .Returns(new MemoryFactPage([new RecalledFact(Stored(id), [])], null));
            world.Memory.RecallAsync(Arg.Any<MemoryQuery>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                .Returns(new MemoryRecall([new RecalledFact(Stored(id), [])]));
            DateTimeOffset? asOf = shape.AsOf ? new DateTimeOffset(Epoch) : null;
            MemoryRecallRequest request = new(shape.Space ? space.Name : " ", shape.Text ? "db" : null, "Halcyon",
                shape.Attribute ? "database" : null, asOf, shape.History, shape.Limit, shape.Cursor ? "not-a-cursor" : null);

            var recalled = await new RecallMemory(world.Containers, world.Memory, world.Authorizer, spaces).RecallAsync(AnyCallerOf(), request);

            bool pages = shape.Space && !shape.AsOf && !shape.Attribute;
            if (shape.Cursor)
            {
                // Paged, the cursor is checked and this one is no cursor; unpaged, no read takes one at all.
                recalled.Status.Should().Be(MemoryStatus.Invalid);
                recalled.Field.Should().Be("cursor");
                world.Memory.ReceivedCalls().Should().BeEmpty();
                return;
            }
            recalled.Status.Should().Be(MemoryStatus.Success);
            // Every read is one recall, one space or all of them, and its answer comes back as memory gave it.
            recalled.Result!.Facts.Should().HaveCount(1);
            if (pages)
            {
                MemoryFactFilter filter = new(Text: shape.Text ? "db" : null, Subject: "Halcyon",
                    Status: shape.History ? MemoryFactStatus.All : MemoryFactStatus.Current);
                await world.Memory.Received(1).ListAsync(id, filter, shape.Text ? MemoryFactSort.Relevance : MemoryFactSort.Recorded,
                    null, Math.Clamp(shape.Limit, 1, 100), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
                await world.Memory.DidNotReceiveWithAnyArgs().RecallAsync(default!, default!, default);
                return;
            }
            await world.Memory.Received(1).RecallAsync(new MemoryQuery(id, shape.Text ? "db" : null, "Halcyon",
                shape.Attribute ? "database" : null, asOf, shape.History, Limit: shape.Limit), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
            await world.Memory.DidNotReceiveWithAnyArgs().ListAsync(default, default!, default, default, default, default!, default);
        }, iter: 500);

    private static OperationContext AnyCallerOf() => new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest);

    [Fact]
    [RandomInputTestOf(typeof(RecallMemory))]
    public Task AcrossAsync_SpacesNamed_ReadsExactlyThoseInOneRecallAndShowsNothingWhenAccessIsUnchecked() =>
        Gen.Select(AnyCaller, Gen.Bool, AnySpace.Select(Gen.Bool).List[0, 4], Gen.Bool, Gen.Int[0, 3], Gen.Bool)
            .SelectMany(t => Gen.Int[0, 5].List[1, 5].Select(picks => (t, picks)))
            .SampleAsync(async s =>
            {
                var ((caller, reader, generated, denied, count, claimed), picks) = s;
                // Names are unique, as the store makes them.
                List<(Container, bool)> stored = [.. generated.DistinctBy(c => c.Item1.Name)];
                IContainerStore containers = ContainerStoreFakes.Create();
                var memory = Substitute.For<IMemoryService>();
                var authorizer = new PolicyAuthorizer(policy => policy == Policies.KnowledgeRead && reader);
                foreach ((Container container, bool exists) in stored)
                {
                    containers.GetAsync(Guid.Parse(container.Id), Arg.Any<CancellationToken>()).Returns(exists ? container : null);
                    containers.GetByNameAsync(container.Name, Arg.Any<CancellationToken>()).Returns(exists ? container : null);
                }
                var facts = Enumerable.Range(0, count).Select(_ => new RecalledFact(Stored(Guid.NewGuid()), [])).ToList();
                memory.RecallAsync(Arg.Any<MemoryQuery>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                    .Returns(denied ? MemoryRecall.DeniedResult : new MemoryRecall(facts));

                // Each pick names a stored space by id or by name, or (past the stored ones) a space that isn't there.
                List<(string Key, Container? Names)> keys = [.. picks.Select(p => p < stored.Count
                    ? (p % 2 == 0 ? stored[p].Item1.Id : stored[p].Item1.Name.ToUpperInvariant(), stored[p].Item2 ? stored[p].Item1 : null)
                    : (p == 4 ? " " : Guid.NewGuid().ToString(), (Container?)null))];
                Guid other = Guid.NewGuid();

                // The query names another container; the spaces the call names must win.
                var recalled = await new RecallMemory(containers, memory, authorizer).AcrossAsync(caller,
                    [.. keys.Select(k => k.Key)], new MemoryQuery(other, Text: "db", ContainerIds: claimed ? [other] : null));

                HashSet<Guid> ids = [.. keys.Select(k => k.Names).OfType<Container>().Select(c => Guid.Parse(c.Id))];
                // The first key that names no space decides the refusal: a document container never holds memory.
                MemoryStatus? refused = keys
                    .Select(k => k.Names is null ? MemoryStatus.ContainerNotFound
                        : k.Names.Kind == ContainerKind.Documents ? MemoryStatus.Invalid
                        : (MemoryStatus?)null)
                    .FirstOrDefault(status => status is not null);
                MemoryStatus expected = !reader ? MemoryStatus.Forbidden
                    : refused ?? (denied ? MemoryStatus.Unchecked : MemoryStatus.Success);
                recalled.Status.Should().Be(expected);
                if (expected is MemoryStatus.Forbidden or MemoryStatus.ContainerNotFound or MemoryStatus.Invalid)
                {
                    memory.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                await memory.Received(1).RecallAsync(
                    Arg.Is<MemoryQuery>(q => q.Text == "db" && ids.Contains(q.ContainerId)
                        && (ids.Count == 1 ? q.ContainerIds == null : q.ContainerIds != null && q.ContainerIds.Count == ids.Count && ids.SetEquals(q.ContainerIds))),
                    Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
                if (expected == MemoryStatus.Success)
                {
                    recalled.Result!.Facts.Should().Equal(facts);
                    recalled.Result.Spaces.Keys.Should().BeEquivalentTo(ids);
                }
            }, iter: 1000);

    [Fact]
    public async Task AcrossAsync_MoreThanTheCapNamed_IsInvalidAndReadsNothing()
    {
        IContainerStore containers = ContainerStoreFakes.Create();
        var memory = Substitute.For<IMemoryService>();
        string[] keys = [.. Enumerable.Range(0, RecallMemory.MaxSpaces + 1).Select(_ => Guid.NewGuid().ToString())];

        var recalled = await new RecallMemory(containers, memory, new PolicyAuthorizer(_ => true))
            .AcrossAsync(new OperationContext(new ClaimsPrincipal(), OperationSurface.Mcp), keys, new MemoryQuery(Guid.Empty));

        recalled.Status.Should().Be(MemoryStatus.Invalid);
        recalled.Field.Should().Be("spaces");
        containers.ReceivedCalls().Should().BeEmpty();
        memory.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    [RandomInputTestOf(typeof(ListMemorySpaces))]
    public Task AudienceAsync_AnyCaller_CanAddOnlyWithWriteAccessAndCountsEveryAccount() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, Gen.Int[0, 500], Gen.Bool)
            .SampleAsync(async s =>
            {
                var (caller, writerRole, writeScope, accounts, hasDirectory) = s;
                var authorizer = new PolicyAuthorizer(p => p switch { Policies.Writer => writerRole, Policies.KnowledgeWrite => writeScope, _ => false });
                IUserDirectory people = Substitute.For<IUserDirectory>();
                people.ListAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(new DirectoryPage([], accounts));

                MemorySpaceAudience audience = await new ListMemorySpaces(Substitute.For<IMemorySpaceStore>(), authorizer, hasDirectory ? people : null)
                    .AudienceAsync(caller);

                audience.Should().Be(new MemorySpaceAudience(writerRole && writeScope, hasDirectory ? accounts : null));
            }, iter: 200);
}
