using System.Buffers.Text;
using System.Security.Claims;
using System.Text;
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
/// The paged memory reads over random inputs: who is refused, which filters, sorts, limits and cursors are refused and
/// with which field, that a refused call never reaches memory, that a cursor works only for the request that made it,
/// and that access which can't be checked shows nothing. Every sample builds its own fakes, because CsCheck runs
/// samples on several threads.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class MemoryPagingRandomInputTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Gen<OperationContext> AnyCaller =
        Gen.Enum<OperationSurface>().Select(s => new OperationContext(new ClaimsPrincipal(new ClaimsIdentity("test")), s));

    private static readonly Gen<Container> AnyContainer = Gen.Select(Gen.Guid, Gen.String[Gen.Char["ab-1"], 2, 4],
        (id, name) => new Container(id.ToString(), $"c{name}x", null, Epoch, Epoch));

    /// <summary>How a call names the space: its id, its name in any case and spacing, a stranger's id, another name, or blank.</summary>
    private static Gen<(string Key, bool Names)> KeyFor(Container container) => Gen.OneOf(
        Gen.Const((container.Id, true)),
        Gen.Const(($" {container.Name.ToUpperInvariant()} ", true)),
        Gen.Guid.Select(g => (g.ToString(), false)),
        Gen.Const(("zz-elsewhere", false)),
        Gen.Const(("  ", false)));

    /// <summary>A limit: none, in range, or out of it either way.</summary>
    private static readonly Gen<int?> AnyLimit = Gen.OneOf(
        Gen.Const((int?)null), Gen.Int[1, 100].Select(i => (int?)i), Gen.Int[-5, 0].Select(i => (int?)i), Gen.Int[101, 400].Select(i => (int?)i));

    private static (MemoryStatus Status, string? Field) R(MemoryStatus status, string? field) => (status, field);

    private static bool InRange(int? limit) => limit is null or (>= 1 and <= 100);

    /// <summary>A cursor no request made: not base64, too long, or well formed with another request's fingerprint.</summary>
    private static readonly Gen<string?> AnyCursor = Gen.OneOf(
        Gen.Const((string?)null),
        Gen.Const((string?)"not a cursor!"),
        Gen.Const((string?)new string('A', 9000)),
        Gen.Guid.Select(g => (string?)Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{{\"f\":\"other\",\"k\":{{\"id\":\"{g}\"}}}}"))));

    /// <summary>A fact id: a real one, or text that isn't an id.</summary>
    private static readonly Gen<(string Text, bool IsId)> FactId = Gen.OneOf(
        Gen.Guid.Select(g => (g.ToString(), true)),
        Gen.String[Gen.Char["xyz-1"], 0, 6].Select(s => (s, false)));

    /// <summary>A filter with values any of which may be out of range, and recorded bounds in either order.</summary>
    private static readonly Gen<MemoryFactFilter> AnyFilter = Gen.Select(
        Gen.OneOfConst<string?>(null, "  ", "db", " Postgres "), Gen.Int[-1, 3], Gen.Int[-1, 2], Gen.Int[-3, 3], Gen.Int[-3, 3], Gen.Bool,
        (text, cites, status, from, to, author) => new MemoryFactFilter(
            text, "Halcyon", author ? Guid.NewGuid() : null, (MemoryCites)cites,
            from < 0 ? null : new DateTimeOffset(Epoch.AddDays(from)), to < 0 ? null : new DateTimeOffset(Epoch.AddDays(to)),
            (MemoryFactStatus)status));

    /// <summary>The field a filter is refused for, worked out from the rules, or null when it is accepted.</summary>
    private static string? FilterFault(MemoryFactFilter filter) =>
        (int)filter.Cites is < 0 or > 2 ? "cites"
        : (int)filter.Status is < 0 or > 1 ? "status"
        : filter.From > filter.To ? "from"
        : null;

    private static MemoryFact Stored(Guid container) =>
        new(Guid.NewGuid(), Guid.NewGuid(), container, "Halcyon", "database", "PostgreSQL 15", null, null,
            DateTimeOffset.UnixEpoch, null, null, null, new MemoryCaller(null, null, null), []);

    private sealed class World
    {
        public readonly IContainerStore Containers = ContainerStoreFakes.Create();
        public readonly IMemoryService Memory = Substitute.For<IMemoryService>();
        public readonly PolicyAuthorizer Authorizer;

        public World(bool reader, Container container, bool exists)
        {
            Authorizer = new PolicyAuthorizer(policy => policy == Policies.KnowledgeRead && reader);
            Containers.GetAsync(Guid.Parse(container.Id), Arg.Any<CancellationToken>()).Returns(exists ? container : null);
            Containers.GetByNameAsync(container.Name, Arg.Any<CancellationToken>()).Returns(exists ? container : null);
        }
    }

    /// <summary>The status and field a space-scoped read fails with before any of its own checks, or null to go on.</summary>
    private static (MemoryStatus Status, string? Field)? SpaceFault(bool reader, bool exists, bool names) =>
        !reader ? R(MemoryStatus.Forbidden, null)
        : !(exists && names) ? R(MemoryStatus.ContainerNotFound, null)
        : null;

    // ── memory.list ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(ListMemoryFacts))]
    public Task List_AnyCall_RefusesBadInputsByFieldAndPagesOnlyWithItsOwnCursor() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, AnyFilter, Gen.Int[-1, 4], AnyLimit)
            .SelectMany(t => KeyFor(t.Item3).SelectMany(key => Gen.Select(AnyCursor, Gen.Bool, Gen.Bool, (cursor, denied, more) => (t, key, cursor, denied, more))))
            .SampleAsync(async s =>
            {
                var ((caller, reader, container, exists, filter, sortValue, limit), key, cursor, denied, more) = s;
                var sort = (MemoryFactSort)sortValue;
                var world = new World(reader, container, exists);
                Guid id = Guid.Parse(container.Id);
                var facts = new List<RecalledFact> { new(Stored(id), []) };
                var next = new MemoryFactKey(Guid.NewGuid(), Recorded: DateTimeOffset.UnixEpoch.AddDays(3), Terms: "db", Position: 7);
                world.Memory.ListAsync(Arg.Any<Guid>(), Arg.Any<MemoryFactFilter>(), Arg.Any<MemoryFactSort>(), Arg.Any<MemoryFactKey?>(),
                        Arg.Any<int>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                    .Returns(denied ? MemoryFactPage.DeniedResult : new MemoryFactPage(facts, more ? next : null));
                var operation = new ListMemoryFacts(world.Containers, world.Memory, world.Authorizer);

                var listed = await operation.RunAsync(caller, key.Key, new MemoryFactListing(filter, sort, cursor, limit));

                (MemoryStatus Status, string? Field) expected =
                    SpaceFault(reader, exists, key.Names)
                    ?? (FilterFault(filter) is { } field ? R(MemoryStatus.Invalid, field)
                    : sortValue is < 0 or > 3 ? R(MemoryStatus.Invalid, "sort")
                    : sort == MemoryFactSort.Relevance && string.IsNullOrWhiteSpace(filter.Text) ? R(MemoryStatus.Invalid, "sort")
                    : !InRange(limit) ? R(MemoryStatus.Invalid, "limit")
                    : cursor is not null ? R(MemoryStatus.Invalid, "cursor")
                    : denied ? R(MemoryStatus.Unchecked, null)
                    : R(MemoryStatus.Success, null));
                listed.Status.Should().Be(expected.Status);
                listed.Field.Should().Be(expected.Field);
                bool reached = expected.Status is MemoryStatus.Success or MemoryStatus.Unchecked;
                world.Memory.ReceivedCalls().Should().HaveCount(reached ? 1 : 0);
                if (!reached)
                    return;
                await world.Memory.Received(1).ListAsync(id, filter, sort, null, limit ?? 50, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
                if (expected.Status != MemoryStatus.Success)
                    return;
                listed.Result!.Facts.Should().Equal(facts);
                if (!more)
                {
                    listed.Result.NextCursor.Should().BeNull();
                    return;
                }

                // The cursor brings back the key it was made from, for the same request only.
                world.Memory.ClearReceivedCalls();
                var second = await operation.RunAsync(caller, container.Id, new MemoryFactListing(filter, sort, listed.Result.NextCursor, limit));
                second.Status.Should().Be(MemoryStatus.Success);
                await world.Memory.Received(1).ListAsync(id, filter, sort, next, limit ?? 50, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
                var other = filter with { Status = filter.Status == MemoryFactStatus.All ? MemoryFactStatus.Current : MemoryFactStatus.All };
                var moved = await operation.RunAsync(caller, container.Id, new MemoryFactListing(other, sort, listed.Result.NextCursor, limit));
                moved.Status.Should().Be(MemoryStatus.Invalid);
                moved.Field.Should().Be("cursor");
            }, iter: 1000);

    [Theory]
    [InlineData('語')]
    [InlineData('\u0001')]
    [InlineData('"')]
    public async Task List_LongestSubjectKey_CursorBringsTheNextPageBack(char fill)
    {
        var container = new Container(Guid.NewGuid().ToString(), "notes", null, Epoch, Epoch);
        var world = new World(reader: true, container, exists: true);
        Guid id = Guid.Parse(container.Id);
        var next = new MemoryFactKey(Guid.NewGuid(), Subject: new string(fill, 200), Attribute: new string(fill, 200));
        world.Memory.ListAsync(Arg.Any<Guid>(), Arg.Any<MemoryFactFilter>(), Arg.Any<MemoryFactSort>(), Arg.Any<MemoryFactKey?>(),
                Arg.Any<int>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryFactPage([new(Stored(id), [])], next));
        var operation = new ListMemoryFacts(world.Containers, world.Memory, world.Authorizer);
        var filter = new MemoryFactFilter();
        var caller = new OperationContext(new ClaimsPrincipal(new ClaimsIdentity("test")), default);

        var first = await operation.RunAsync(caller, container.Id, new MemoryFactListing(filter, MemoryFactSort.Subject));
        var second = await operation.RunAsync(caller, container.Id, new MemoryFactListing(filter, MemoryFactSort.Subject, first.Result!.NextCursor));

        second.Status.Should().Be(MemoryStatus.Success);
        await world.Memory.Received(1).ListAsync(id, filter, MemoryFactSort.Subject, next, 50, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
    }

    // ── memory.count ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(CountMemoryFacts))]
    public Task Count_AnyCall_CountsOnlyForAReaderWithAGoodFilterAndShowsNothingWhenUnchecked() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, AnyFilter, Gen.Bool, Gen.Long[0, 50_000])
            .SelectMany(t => KeyFor(t.Item3).SelectMany(key => Gen.Bool.Select(capped => (t, key, capped))))
            .SampleAsync(async s =>
            {
                var ((caller, reader, container, exists, filter, denied, count), key, capped) = s;
                var world = new World(reader, container, exists);
                Guid id = Guid.Parse(container.Id);
                world.Memory.CountAsync(Arg.Any<Guid>(), Arg.Any<MemoryFactFilter>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                    .Returns(denied ? MemoryFactCount.DeniedResult : new MemoryFactCount(count, capped));

                var counted = await new CountMemoryFacts(world.Containers, world.Memory, world.Authorizer).RunAsync(caller, key.Key, filter);

                (MemoryStatus Status, string? Field) expected =
                    SpaceFault(reader, exists, key.Names)
                    ?? (FilterFault(filter) is { } field ? R(MemoryStatus.Invalid, field)
                    : denied ? R(MemoryStatus.Unchecked, null)
                    : R(MemoryStatus.Success, null));
                counted.Status.Should().Be(expected.Status);
                counted.Field.Should().Be(expected.Field);
                bool reached = expected.Status is MemoryStatus.Success or MemoryStatus.Unchecked;
                world.Memory.ReceivedCalls().Should().HaveCount(reached ? 1 : 0);
                if (reached)
                    await world.Memory.Received(1).CountAsync(id, filter, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
                if (expected.Status == MemoryStatus.Success)
                    counted.Result.Should().Be(new MemoryFactCount(count, capped));
            }, iter: 1000);

    // ── memory.subjects ───────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(ListMemorySubjects))]
    public Task Subjects_AnyCall_RefusesBadInputsByFieldAndPagesOnlyWithItsOwnCursor() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, Gen.OneOfConst<string?>(null, "", " hal "), Gen.Int[-1, 2], AnyLimit)
            .SelectMany(t => KeyFor(t.Item3).SelectMany(key => Gen.Select(AnyCursor, Gen.Bool, Gen.Bool, (cursor, denied, more) => (t, key, cursor, denied, more))))
            .SampleAsync(async s =>
            {
                var ((caller, reader, container, exists, text, statusValue, limit), key, cursor, denied, more) = s;
                var status = (MemoryFactStatus)statusValue;
                var world = new World(reader, container, exists);
                Guid id = Guid.Parse(container.Id);
                var subjects = new List<MemorySubject> { new("Halcyon", 3) };
                world.Memory.SubjectsAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<MemoryFactStatus>(), Arg.Any<string?>(), Arg.Any<int>(),
                        Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                    .Returns(denied ? MemorySubjectPage.DeniedResult : new MemorySubjectPage(subjects, more ? "halcyon" : null));
                var operation = new ListMemorySubjects(world.Containers, world.Memory, world.Authorizer);

                var listed = await operation.RunAsync(caller, key.Key, text, status, cursor, limit);

                (MemoryStatus Status, string? Field) expected =
                    SpaceFault(reader, exists, key.Names)
                    ?? (statusValue is < 0 or > 1 ? R(MemoryStatus.Invalid, "status")
                    : !InRange(limit) ? R(MemoryStatus.Invalid, "limit")
                    : cursor is not null ? R(MemoryStatus.Invalid, "cursor")
                    : denied ? R(MemoryStatus.Unchecked, null)
                    : R(MemoryStatus.Success, null));
                listed.Status.Should().Be(expected.Status);
                listed.Field.Should().Be(expected.Field);
                bool reached = expected.Status is MemoryStatus.Success or MemoryStatus.Unchecked;
                world.Memory.ReceivedCalls().Should().HaveCount(reached ? 1 : 0);
                if (!reached)
                    return;
                await world.Memory.Received(1).SubjectsAsync(id, text, status, null, limit ?? 50, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
                if (expected.Status != MemoryStatus.Success)
                    return;
                listed.Result!.Subjects.Should().Equal(subjects);
                if (!more)
                {
                    listed.Result.NextCursor.Should().BeNull();
                    return;
                }

                world.Memory.ClearReceivedCalls();
                (await operation.RunAsync(caller, container.Id, text, status, listed.Result.NextCursor, limit)).Status.Should().Be(MemoryStatus.Success);
                await world.Memory.Received(1).SubjectsAsync(id, text, status, "halcyon", limit ?? 50, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
                var moved = await operation.RunAsync(caller, container.Id, "another", status, listed.Result.NextCursor, limit);
                moved.Field.Should().Be("cursor");
            }, iter: 1000);

    // ── memory.history ────────────────────────────────────────────────────────────────────────────

    /// <summary>What memory answers for a fact's history: a page, a fact it can't find, or access it couldn't check.</summary>
    private enum HistoryAnswer { Page, Missing, Denied }

    private static MemoryHistoryPage Answer(HistoryAnswer answer, Guid container, bool more, MemoryVersionKey next) => answer switch
    {
        HistoryAnswer.Missing => MemoryHistoryPage.Missing,
        HistoryAnswer.Denied => MemoryHistoryPage.DeniedResult,
        _ => new MemoryHistoryPage([new RecalledFact(Stored(container), [])], 140, more ? next : null),
    };

    [Fact]
    [RandomInputTestOf(typeof(ListMemoryHistory))]
    public Task History_AnyCallInASpace_PagesEveryVersionWithItsOwnCursorAndHidesWhatItCantSee() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, FactId, AnyLimit, Gen.Enum<HistoryAnswer>())
            .SelectMany(t => KeyFor(t.Item3).SelectMany(key => Gen.Select(AnyCursor, Gen.Bool, (cursor, more) => (t, key, cursor, more))))
            .SampleAsync(async s =>
            {
                var ((caller, reader, container, exists, factId, limit, answer), key, cursor, more) = s;
                var world = new World(reader, container, exists);
                Guid id = Guid.Parse(container.Id);
                var next = new MemoryVersionKey(DateTimeOffset.UnixEpoch.AddHours(5), Guid.NewGuid());
                world.Memory.HistoryAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<MemoryVersionKey?>(), Arg.Any<int>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                    .Returns(Answer(answer, id, more, next));
                var operation = new ListMemoryHistory(world.Containers, world.Memory, world.Authorizer);

                var listed = await operation.RunAsync(caller, key.Key, factId.Text, cursor, limit);

                (MemoryStatus Status, string? Field) expected =
                    SpaceFault(reader, exists, key.Names)
                    ?? (!factId.IsId ? R(MemoryStatus.FactNotFound, null)
                    : !InRange(limit) ? R(MemoryStatus.Invalid, "limit")
                    : cursor is not null ? R(MemoryStatus.Invalid, "cursor")
                    : answer == HistoryAnswer.Denied ? R(MemoryStatus.Unchecked, null)
                    : answer == HistoryAnswer.Missing ? R(MemoryStatus.FactNotFound, null)
                    : R(MemoryStatus.Success, null));
                listed.Status.Should().Be(expected.Status);
                listed.Field.Should().Be(expected.Field);
                bool reached = expected.Status is MemoryStatus.Success or MemoryStatus.Unchecked
                    || (expected.Status == MemoryStatus.FactNotFound && factId.IsId);
                world.Memory.ReceivedCalls().Should().HaveCount(reached ? 1 : 0);
                if (!reached)
                    return;
                Guid fact = Guid.Parse(factId.Text);
                await world.Memory.Received(1).HistoryAsync(id, fact, null, limit ?? 25, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
                if (expected.Status != MemoryStatus.Success)
                    return;
                listed.Result!.Total.Should().Be(140);
                if (!more)
                {
                    listed.Result.NextCursor.Should().BeNull();
                    return;
                }

                world.Memory.ClearReceivedCalls();
                (await operation.RunAsync(caller, container.Id, factId.Text, listed.Result.NextCursor, limit)).Status.Should().Be(MemoryStatus.Success);
                await world.Memory.Received(1).HistoryAsync(id, fact, next, limit ?? 25, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
                var moved = await operation.RunAsync(caller, container.Id, Guid.NewGuid().ToString(), listed.Result.NextCursor, limit);
                moved.Field.Should().Be("cursor");
            }, iter: 1000);

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(3, 2, true)]
    [InlineData(6, 4, false)]
    [InlineData(-1, 4, false)]
    public async Task HistoryBackTo_VersionOnSomePage_ReadsPagesUntilItAndJoinsThemNewestFirst(int target, int pagesRead, bool more)
    {
        Container container = new(Guid.NewGuid().ToString(), "cspace", null, Epoch, Epoch);
        var world = new World(true, container, true);
        Guid id = Guid.Parse(container.Id);
        RecalledFact[] chain = [.. Enumerable.Range(0, 7).Select(_ => new RecalledFact(Stored(id), []))];
        // Two versions a page, each page keyed on its last version.
        world.Memory.HistoryAsync(id, Arg.Any<Guid>(), Arg.Any<MemoryVersionKey?>(), 2, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                int start = ci.ArgAt<MemoryVersionKey?>(2) is { } after ? Array.FindIndex(chain, v => v.Fact.Id == after.Id) + 1 : 0;
                int end = Math.Min(start + 2, chain.Length);
                return new MemoryHistoryPage([.. chain[start..end]], 7,
                    end < chain.Length ? new MemoryVersionKey(DateTimeOffset.UnixEpoch.AddMinutes(end), chain[end - 1].Fact.Id) : null);
            });
        Guid version = target < 0 ? Guid.NewGuid() : chain[target].Fact.Id;

        var read = await new ListMemoryHistory(world.Containers, world.Memory, world.Authorizer)
            .BackToAsync(AnyCaller.Single(), container.Id, chain[0].Fact.Id.ToString(), version, 2);

        read.Status.Should().Be(MemoryStatus.Success);
        read.Result!.Versions.Should().Equal(chain.Take(Math.Min(7, pagesRead * 2)));
        read.Result.Total.Should().Be(7);
        (read.Result.NextCursor is not null).Should().Be(more);
        world.Memory.ReceivedCalls().Should().HaveCount(pagesRead);
    }

    [Fact]
    public async Task HistoryBackTo_PageFails_FailsTheWholeRead()
    {
        Container container = new(Guid.NewGuid().ToString(), "cspace", null, Epoch, Epoch);
        var world = new World(true, container, true);
        Guid id = Guid.Parse(container.Id);
        world.Memory.HistoryAsync(id, Arg.Any<Guid>(), null, Arg.Any<int>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryHistoryPage([new RecalledFact(Stored(id), [])], 9, new MemoryVersionKey(DateTimeOffset.UnixEpoch, Guid.NewGuid())));
        world.Memory.HistoryAsync(id, Arg.Any<Guid>(), Arg.Is<MemoryVersionKey?>(k => k != null), Arg.Any<int>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(MemoryHistoryPage.DeniedResult);

        var read = await new ListMemoryHistory(world.Containers, world.Memory, world.Authorizer)
            .BackToAsync(AnyCaller.Single(), container.Id, Guid.NewGuid().ToString(), Guid.NewGuid());

        read.Status.Should().Be(MemoryStatus.Unchecked);
        read.Result.Should().BeNull();
    }

    [Fact]
    [RandomInputTestOf(typeof(ListMemoryHistory))]
    public Task HistoryByFact_AnyCall_FindsTheSpaceAndReadsAHiddenSpaceAsAMissingFact() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, FactId, Gen.Bool, AnyLimit)
            .SelectMany(t => Gen.Enum<HistoryAnswer>().Select(answer => (t, answer)))
            .SampleAsync(async s =>
            {
                var ((caller, reader, container, exists, factId, stored, limit), answer) = s;
                var world = new World(reader, container, exists);
                Guid id = Guid.Parse(container.Id);
                world.Memory.SpaceOfAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(stored ? id : null);
                world.Memory.HistoryAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<MemoryVersionKey?>(), Arg.Any<int>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                    .Returns(Answer(answer, id, false, new MemoryVersionKey(DateTimeOffset.UnixEpoch, Guid.Empty)));

                var listed = await new ListMemoryHistory(world.Containers, world.Memory, world.Authorizer)
                    .RunByFactAsync(caller, factId.Text, null, limit);

                // A space the caller can't open, like a fact that isn't there, is a missing fact; never a missing space.
                MemoryStatus expected = !reader ? MemoryStatus.Forbidden
                    : !(factId.IsId && stored && exists) ? MemoryStatus.FactNotFound
                    : !InRange(limit) ? MemoryStatus.Invalid
                    : answer == HistoryAnswer.Denied ? MemoryStatus.Unchecked
                    : answer == HistoryAnswer.Missing ? MemoryStatus.FactNotFound
                    : MemoryStatus.Success;
                listed.Status.Should().Be(expected);
                if (!reader)
                    world.Memory.ReceivedCalls().Should().BeEmpty();
                bool reached = reader && factId.IsId && stored && exists && InRange(limit);
                await world.Memory.Received(reached ? 1 : 0).HistoryAsync(
                    Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<MemoryVersionKey?>(), Arg.Any<int>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
            }, iter: 1000);
}
