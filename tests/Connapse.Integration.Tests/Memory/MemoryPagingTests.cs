using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// Paging a space against the real store, over random facts and permissions: every page sequence must show each fact
/// the rule allows exactly once and nothing else, in every sort, and counts must count only those facts. The rule is
/// worked out here from who may read which document. Many facts share a recorded time, so keyset ties are exercised.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class MemoryPagingTests(SharedWebAppFixture fixture) : IDisposable
{
    // Held for the test, since the worlds it seeds outlive the method that seeds them.
    private readonly AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

    public void Dispose() => scope.Dispose();

    private sealed class FakeAccess(Dictionary<Guid, HashSet<string>> readable) : IDocumentAccess
    {
        public Task<IReadOnlySet<string>> ReadableAsync(Guid? userId, IReadOnlyCollection<string> documentIds, CancellationToken ct = default)
        {
            IReadOnlySet<string> open = userId is { } u && readable.TryGetValue(u, out HashSet<string>? r)
                ? documentIds.Where(r.Contains).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            return Task.FromResult(open);
        }
    }

    private sealed class FakeIndex(Dictionary<string, string[]> nodes) : ILinkIndex
    {
        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(nodes.Count > 0);

        public Task<ILookup<string, string>> NodesOfAsync(IReadOnlyCollection<string> documentIds, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ILookup<string, string>> DocumentsOfAsync(IReadOnlyCollection<string> nodeIds, CancellationToken ct = default) =>
            Task.FromResult(nodeIds.SelectMany(n => nodes.GetValueOrDefault(n, []).Select(d => (n, d))).ToLookup(p => p.n, p => p.d));
    }

    /// <summary>A clock tests move by hand, so many facts can share one recorded time.</summary>
    private sealed class StepClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset Jan = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Task<MemoryWriteResult?> AnyChain(IReadOnlyList<MemoryFact> chain) => Task.FromResult<MemoryWriteResult?>(null);

    private sealed record World(
        Guid Container, PostgresMemoryStore Store, MemoryService Service, StepClock Clock, MemoryCaller[] Viewers,
        Func<MemoryFact, MemoryCaller, bool> Allowed, IReadOnlyList<MemoryFact> Facts);

    private async Task<World> SeedAsync(int seed, int facts = 120)
    {
        var random = new Random(seed);
        Guid[] users = [.. Enumerable.Range(0, 3).Select(_ => Guid.NewGuid())];
        Guid agent = Guid.NewGuid();
        string[] documents = [.. Enumerable.Range(0, 6).Select(_ => Guid.NewGuid().ToString())];
        Dictionary<Guid, HashSet<string>> readable = users.ToDictionary(u => u,
            _ => documents.Where(_ => random.Next(3) > 0).ToHashSet(StringComparer.Ordinal));
        Dictionary<string, string[]> nodes = new(StringComparer.Ordinal) { ["TCK-1"] = [documents[0], documents[1]], ["fn:Orphan"] = [] };

        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid container = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Containers.Add(new ContainerEntity { Id = container, Name = $"paging-{Guid.NewGuid():N}" });
            await db.SaveChangesAsync();
        }

        var clock = new StepClock(Jan);
        var store = new PostgresMemoryStore(factory, clock) { CountBatch = 7 };
        var service = new MemoryService(store, new FakeAccess(readable), new FakeIndex(nodes), Substitute.For<IAuditLogger>(), NullLogger<MemoryService>.Instance);
        MemoryCaller[] authors = [.. users.Select(u => new MemoryCaller(u, null, null)), new MemoryCaller(null, agent, "bot")];

        string[] subjects = ["alpha", "Alpha", "beta", "gamma"];
        string[] attributes = ["database", "lead", "region"];
        List<MemoryFact> written = [];
        for (int i = 0; i < facts; i++)
        {
            // Every few facts share one recorded time, so pages break inside a tie.
            if (i % 4 == 0)
                clock.Now = clock.Now.AddMinutes(1);
            MemorySource[] sources = [.. Enumerable.Range(0, random.Next(3)).Select(_ => random.Next(5) == 0
                ? new MemorySource(MemorySourceKind.Node, random.Next(2) == 0 ? "TCK-1" : "fn:Orphan")
                : new MemorySource(MemorySourceKind.Document, documents[random.Next(documents.Length)])).Distinct()];
            DateTimeOffset? validFrom = random.Next(4) == 0 ? null : Jan.AddDays(random.Next(150));
            MemoryScope scope2 = sources.Length == 0 || random.Next(4) == 0 ? MemoryScope.Personal : MemoryScope.Team;
            var fact = new NewMemoryFact(subjects[random.Next(subjects.Length)], attributes[random.Next(3)], $"value {i}",
                validFrom, null, sources, null, scope2);
            MemoryCaller author = authors[random.Next(authors.Length)];
            MemoryFact? previous = written.LastOrDefault(w => w.Subject == fact.Subject && w.Attribute == fact.Attribute
                && w.ValidTo is null && w.SupersededAt is null && w.ValidFrom is { } from && validFrom > from);
            if (previous is not null && random.Next(2) == 0)
            {
                MemoryWriteResult result = await store.CorrectAsync(previous.Id,
                    random.Next(2) == 0 ? MemoryCorrection.Wrong : MemoryCorrection.Changed, fact, author, AnyChain);
                result.Error.Should().BeNull();
            }
            else
            {
                await store.AddAsync(container, fact, author);
            }
        }
        // Now sits inside the facts' true-from range, so some are not current yet.
        clock.Now = Jan.AddDays(75);

        bool Allowed(MemoryFact f, MemoryCaller viewer) => (f.Scope == MemoryScope.Team || viewer.IsAuthorOf(f.Author))
            && f.Sources.All(s => viewer.UserId is { } user && (s.Kind == MemorySourceKind.Document
                ? readable[user].Contains(s.Ref)
                : nodes[s.Ref].Length > 0 && nodes[s.Ref].All(readable[user].Contains)));

        IReadOnlyList<MemoryFact> all = await store.QueryAsync(new MemoryQuery(container, History: true), 0, 10_000);
        return new World(container, store, service, clock, authors, Allowed, all);
    }

    private static bool Matches(MemoryFact f, MemoryFactFilter filter, DateTimeOffset now) =>
        (filter.Subject is null || string.Equals(f.Subject, filter.Subject, StringComparison.OrdinalIgnoreCase))
        && (filter.Author is not { } author || f.Author.UserId == author || f.Author.AgentId == author)
        && (filter.Cites != MemoryCites.Some || f.Sources.Count > 0)
        && (filter.Cites != MemoryCites.None || f.Sources.Count == 0)
        && (filter.From is not { } from || f.RecordedAt >= from)
        && (filter.To is not { } to || f.RecordedAt <= to)
        && (filter.Status == MemoryFactStatus.All
            || (f.SupersededAt is null && (f.ValidFrom is null || f.ValidFrom <= now) && (f.ValidTo is null || f.ValidTo > now)));

    private static async Task<List<RecalledFact>> PageAllAsync(
        MemoryService service, Guid container, MemoryFactFilter filter, MemoryFactSort sort, MemoryCaller viewer, int limit)
    {
        List<RecalledFact> shown = [];
        MemoryFactKey? after = null;
        for (int pages = 0; pages < 1_000; pages++)
        {
            MemoryFactPage page = await service.ListAsync(container, filter, sort, after, limit, viewer);
            page.Denied.Should().BeFalse();
            page.Facts.Count.Should().BeLessThanOrEqualTo(limit);
            shown.AddRange(page.Facts);
            if (page.Next is null)
                return shown;
            page.Facts.Should().HaveCount(limit, "only a full page has a next one");
            after = page.Next;
        }
        throw new InvalidOperationException("paging did not end");
    }

    [Theory]
    [InlineData(241)]
    [InlineData(242)]
    public async Task Paging_EverySortAndFilter_ShowsEveryVisibleFactExactlyOnce(int seed)
    {
        World world = await SeedAsync(seed);
        MemoryFactFilter[] filters =
        [
            new(),
            new(Subject: "ALPHA"),
            new(Cites: MemoryCites.None),
            new(Cites: MemoryCites.Some),
            new(Author: world.Viewers[0].UserId),
            new(Author: world.Viewers[3].AgentId),
            new(From: Jan.AddMinutes(5), To: Jan.AddMinutes(20)),
        ];

        int shown = 0;
        foreach (MemoryCaller viewer in world.Viewers)
            foreach (MemoryFactFilter baseFilter in filters)
                foreach (MemoryFactStatus status in Enum.GetValues<MemoryFactStatus>())
                {
                    MemoryFactFilter filter = baseFilter with { Status = status };
                    Guid[] expected = [.. world.Facts.Where(f => world.Allowed(f, viewer) && Matches(f, filter, world.Clock.Now)).Select(f => f.Id)];
                    foreach (MemoryFactSort sort in new[] { MemoryFactSort.Recorded, MemoryFactSort.Subject, MemoryFactSort.ValidFrom })
                    {
                        List<RecalledFact> paged = await PageAllAsync(world.Service, world.Container, filter, sort, viewer, 7);
                        paged.Select(f => f.Fact.Id).Should().OnlyHaveUniqueItems($"{sort} {filter} for {viewer}")
                            .And.BeEquivalentTo(expected, $"{sort} {filter} for {viewer}");
                    }

                    MemoryFactCount count = await world.Service.CountAsync(world.Container, filter, viewer);
                    count.Should().Be(new MemoryFactCount(expected.Length, false), $"count {filter} for {viewer}");
                    shown += expected.Length;
                }
        shown.Should().BeGreaterThan(0, "the world must show something, or the test proves nothing");
    }

    [Fact]
    public async Task Paging_ByRelevance_ShowsTheSameVisibleMatchesAsNewestFirstEachOnce()
    {
        World world = await SeedAsync(243);
        int shown = 0;
        foreach (MemoryCaller viewer in world.Viewers)
            foreach (MemoryFactStatus status in Enum.GetValues<MemoryFactStatus>())
            {
                var filter = new MemoryFactFilter(Text: "alpha region", Status: status);
                List<RecalledFact> ranked = await PageAllAsync(world.Service, world.Container, filter, MemoryFactSort.Relevance, viewer, 5);
                List<RecalledFact> newest = await PageAllAsync(world.Service, world.Container, filter, MemoryFactSort.Recorded, viewer, 5);

                ranked.Select(f => f.Fact.Id).Should().OnlyHaveUniqueItems().And.BeEquivalentTo(newest.Select(f => f.Fact.Id));
                ranked.Should().OnlyContain(f => world.Allowed(f.Fact, viewer) && Matches(f.Fact, filter, world.Clock.Now));
                (await world.Service.CountAsync(world.Container, filter, viewer)).Count.Should().Be(ranked.Count);
                shown += ranked.Count;
            }
        shown.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Count_PastItsBudget_AnswersWithALowerBoundMarkedCapped()
    {
        World world = await SeedAsync(244, facts: 40);
        MemoryCaller viewer = world.Viewers[0];
        MemoryViewer everything = new(viewer, world.Facts.SelectMany(f => f.Sources).ToHashSet());
        var filter = new MemoryFactFilter(Status: MemoryFactStatus.All);
        long visible = world.Facts.Count(f => f.Scope == MemoryScope.Team || viewer.IsAuthorOf(f.Author));

        (await world.Store.CountAsync(world.Container, filter, everything, TimeSpan.FromMinutes(1)))
            .Should().Be((visible, false), "batches of seven add up to the exact count");
        (long count, bool capped) = await world.Store.CountAsync(world.Container, filter, everything, TimeSpan.Zero);
        capped.Should().BeTrue();
        count.Should().BeLessThan(visible);
    }

    [Fact]
    public async Task Subjects_PageAToZ_WithVisibleCountsAndCaseFolded()
    {
        World world = await SeedAsync(245);
        foreach (MemoryCaller viewer in world.Viewers)
        {
            List<MemorySubject> subjects = [];
            string? after = null;
            do
            {
                MemorySubjectPage page = await world.Service.SubjectsAsync(world.Container, null, MemoryFactStatus.All, after, 1, viewer);
                page.Denied.Should().BeFalse();
                subjects.AddRange(page.Subjects);
                after = page.Next;
            }
            while (after is not null);

            var expected = world.Facts.Where(f => world.Allowed(f, viewer))
                .GroupBy(f => f.Subject.ToLowerInvariant()).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (g.Key, (long)g.Count())).ToList();
            subjects.Select(s => (s.Subject.ToLowerInvariant(), s.Facts)).Should().Equal(expected, $"subjects for {viewer}");
        }

        MemorySubjectPage beta = await world.Service.SubjectsAsync(world.Container, "ET", MemoryFactStatus.All, null, 10, world.Viewers[0]);
        beta.Subjects.Should().OnlyContain(s => s.Subject == "beta");
    }

    [Fact]
    public async Task History_PagesEveryVersionPastAHundredWithAnExactTotal()
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid container = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Containers.Add(new ContainerEntity { Id = container, Name = $"history-{Guid.NewGuid():N}" });
            await db.SaveChangesAsync();
        }
        var clock = new StepClock(Jan);
        var store = new PostgresMemoryStore(factory, clock);
        var service = new MemoryService(store, new FakeAccess([]), new FakeIndex([]), Substitute.For<IAuditLogger>(), NullLogger<MemoryService>.Instance);
        var me = new MemoryCaller(Guid.NewGuid(), null, null);

        MemoryFact latest = await store.AddAsync(container, new NewMemoryFact("Halcyon", "database", "v0", null, null, []), me);
        List<Guid> versions = [latest.Id];
        for (int i = 1; i < 105; i++)
        {
            // Pairs share a recorded time, so pages break inside a tie.
            if (i % 2 == 0)
                clock.Now = clock.Now.AddSeconds(1);
            latest = (await store.CorrectAsync(latest.Id, MemoryCorrection.Wrong,
                new NewMemoryFact("", "", $"v{i}", null, null, []), me, AnyChain)).Fact!;
            versions.Add(latest.Id);
        }

        List<Guid> paged = [];
        MemoryVersionKey? after = null;
        do
        {
            MemoryHistoryPage page = await service.HistoryAsync(container, versions[0], after, 25, me);
            page.Total.Should().Be(105);
            paged.AddRange(page.Versions.Select(v => v.Fact.Id));
            after = page.Next;
        }
        while (after is not null);

        paged.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(versions);
        paged[0].Should().Be(latest.Id, "newest first");
        (await service.HistoryAsync(container, latest.Id, null, 25, new MemoryCaller(Guid.NewGuid(), null, null)))
            .Should().Be(MemoryHistoryPage.Missing, "a personal fact's history is its author's alone");
        (await service.HistoryAsync(container, Guid.NewGuid(), null, 25, me)).Should().Be(MemoryHistoryPage.Missing);
    }
}
