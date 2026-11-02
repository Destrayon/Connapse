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
/// Random memories over random permissions, against the real store: every answer must hold exactly the facts the
/// rule allows, worked out here from who may read which document. A fact is visible when every source is readable
/// (a node: when a document standing for it is); a sourceless fact only to its author.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemoryLeakTests(SharedWebAppFixture fixture)
{
    private sealed class FakeAccess(Dictionary<Guid, HashSet<string>> readable) : IDocumentAccess
    {
        public bool Fail { get; set; }

        public Task<IReadOnlySet<string>> ReadableAsync(Guid? userId, IReadOnlyCollection<string> documentIds, CancellationToken ct = default)
        {
            if (Fail)
                throw new InvalidOperationException("access check down");
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

    [Fact]
    public async Task Recall_PastMoreHiddenFactsThanOnePage_StillFindsTheVisibleOne()
    {
        Guid user = Guid.NewGuid();
        string open = Guid.NewGuid().ToString(), hidden = Guid.NewGuid().ToString();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid container = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Containers.Add(new ContainerEntity { Id = container, Name = $"page-{Guid.NewGuid():N}" });
            // Hidden facts match as well and are recorded later, so they all rank before the visible one.
            DateTimeOffset now = DateTimeOffset.UtcNow.AddDays(1);
            db.MemoryFacts.AddRange(Enumerable.Range(0, 1_100).Select(i =>
            {
                Guid id = Guid.NewGuid();
                return new MemoryFactEntity
                {
                    Id = id,
                    ChainId = id,
                    ContainerId = container,
                    Subject = "alpha",
                    Attribute = "database",
                    Value = "hidden",
                    RecordedAt = now,
                    Sources = [new MemoryFactSourceEntity { FactId = id, Ordinal = 0, Kind = 0, Ref = hidden }],
                };
            }));
            await db.SaveChangesAsync();
        }
        var store = new PostgresMemoryStore(factory, TimeProvider.System);
        await store.AddAsync(container, new NewMemoryFact("alpha", "region", "visible", null, null,
            [new MemorySource(MemorySourceKind.Document, open)]), new MemoryCaller(user, null, null));
        var service = new MemoryService(store, new FakeAccess(new() { [user] = [open] }), new FakeIndex([]),
            Substitute.For<IAuditLogger>(), NullLogger<MemoryService>.Instance);

        MemoryRecall recall = await service.RecallAsync(new MemoryQuery(container, Text: "alpha database region"), new MemoryCaller(user, null, null));

        recall.Facts.Select(f => f.Fact.Value).Should().Equal("visible");
    }

    [Theory]
    [InlineData(38)]
    [InlineData(39)]
    [InlineData(40)]
    public async Task EveryAnswer_HoldsExactlyTheFactsTheRuleAllows(int seed)
    {
        var random = new Random(seed);
        Guid[] users = [.. Enumerable.Range(0, 4).Select(_ => Guid.NewGuid())];
        string[] documents = [.. Enumerable.Range(0, 10).Select(_ => Guid.NewGuid().ToString())];
        Dictionary<Guid, HashSet<string>> readable = users.ToDictionary(u => u,
            _ => documents.Where(_ => random.Next(2) == 0).ToHashSet(StringComparer.Ordinal));
        Dictionary<string, string[]> nodes = new(StringComparer.Ordinal)
        {
            ["TCK-1"] = [documents[0]],
            ["TCK-2"] = [documents[1], documents[2]],
            ["fn:Orphan"] = [],
        };
        string[] nodeIds = [.. nodes.Keys];

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid container = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Containers.Add(new ContainerEntity { Id = container, Name = $"leak-{Guid.NewGuid():N}" });
            await db.SaveChangesAsync();
        }

        var store = new PostgresMemoryStore(factory, TimeProvider.System);
        var access = new FakeAccess(readable);
        var service = new MemoryService(store, access, new FakeIndex(nodes), Substitute.For<IAuditLogger>(), NullLogger<MemoryService>.Instance);

        // Written straight to the store, so facts can cite anything: the write-time check is tested elsewhere.
        string[] subjects = ["alpha", "beta"];
        string[] attributes = ["database", "lead", "region"];
        List<MemoryFact> written = [];
        for (int i = 0; i < 80; i++)
        {
            MemorySource[] sources = [.. Enumerable.Range(0, random.Next(4)).Select(_ => random.Next(4) == 0
                ? new MemorySource(MemorySourceKind.Node, nodeIds[random.Next(nodeIds.Length)])
                : new MemorySource(MemorySourceKind.Document, documents[random.Next(documents.Length)])).Distinct()];
            var fact = new NewMemoryFact(subjects[random.Next(2)], attributes[random.Next(3)], $"value {i}",
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(random.Next(150)), null, sources, $"TCK-{i % 5}");
            var author = new MemoryCaller(users[random.Next(users.Length)], null, null);
            MemoryFact? previous = written.LastOrDefault(w => w.Subject == fact.Subject && w.Attribute == fact.Attribute
                && w.ValidTo is null && w.SupersededAt is null && w.ValidFrom < fact.ValidFrom);
            if (previous is not null && random.Next(2) == 0)
            {
                // A wrong correction keeps the old dates: its start may be where an earlier version ends.
                MemoryWriteResult result = random.Next(3) == 0
                    ? await store.CorrectAsync(container, previous.Id, MemoryCorrection.Wrong, fact with { ValidFrom = null }, author)
                    : await store.CorrectAsync(container, previous.Id, MemoryCorrection.Changed, fact, author);
                result.Error.Should().BeNull();
                written.Remove(previous);
                written.Add(result.Replaced!);
                written.Add(result.Fact!);
            }
            else
            {
                written.Add(await store.AddAsync(container, fact, author));
            }
        }

        // Point-in-time recall finds exactly one version of every chain that had started by then (none here ends).
        for (DateTimeOffset at = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); at.Month < 7; at = at.AddDays(3))
        {
            IReadOnlyList<MemoryFact> then = await store.QueryAsync(new MemoryQuery(container, AsOf: at), 0, 1000);
            then.GroupBy(f => f.ChainId).Should().OnlyContain(g => g.Count() == 1, $"one version per chain on {at:d}");
            Guid[] started = [.. written.Where(f => f.SupersededAt is null).GroupBy(f => f.ChainId)
                .Where(g => g.Min(f => f.ValidFrom) <= at).Select(g => g.Key)];
            then.Select(f => f.ChainId).Should().BeEquivalentTo(started, $"every started chain shows on {at:d}");
        }

        bool Allowed(MemoryFact f, Guid user) => f.Sources.Count == 0
            ? f.Author.UserId == user
            : f.Sources.All(s => s.Kind == MemorySourceKind.Document
                ? readable[user].Contains(s.Ref)
                : nodes[s.Ref].Any(readable[user].Contains));

        MemoryQuery[] queries =
        [
            new(container, Limit: 100),
            new(container, Subject: "alpha", Limit: 100),
            new(container, AsOf: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), Limit: 100),
            new(container, History: true, Limit: 100),
            new(container, Text: "beta database TCK-3", History: true, Limit: 100),
            new(container, ChangedFrom: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
                ChangedTo: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), Limit: 100),
            new(container, History: true, Limit: 3),
        ];

        int shown = 0;
        foreach (Guid user in users)
        {
            foreach (MemoryQuery query in queries)
            {
                IReadOnlyList<MemoryFact> all = await store.QueryAsync(query, 0, 1000);
                List<Guid> expected = [.. all.Where(f => Allowed(f, user)).Take(query.Limit).Select(f => f.Id)];

                MemoryRecall recall = await service.RecallAsync(query, new MemoryCaller(user, null, null));

                recall.Denied.Should().BeFalse();
                recall.Facts.Select(f => f.Fact.Id).Should().Equal(expected, $"user {user} and query {query}");
                recall.Facts.Should().OnlyContain(f => f.Sources.Select(s => s.Ref).SequenceEqual(f.Fact.Sources.Select(s => s.Ref)));
                shown += expected.Count;
            }
        }
        shown.Should().BeGreaterThan(0, "the world must show something, or the test proves nothing");

        access.Fail = true;
        (await service.RecallAsync(queries[3], new MemoryCaller(users[0], null, null))).Should().Be(MemoryRecall.DeniedResult);
        (await service.RememberAsync(container, new NewMemoryFact("a", "b", "c", null, null, []), new MemoryCaller(users[0], null, null)))
            .Error.Should().NotBeNull("a write whose sources cannot be checked is refused");
    }
}
