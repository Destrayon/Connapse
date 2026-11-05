using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using Connapse.Application;
using Connapse.Application.Memory;
using IOperationAuthorizer = Connapse.Application.Ports.IOperationAuthorizer;
using Policies = Connapse.Application.Ports.Policies;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Containers;
using Connapse.Storage.Data;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace Connapse.Integration.Tests.Memory;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MemoryLoadCollection
{
    public const string Name = "Memory load";
}

/// <summary>
/// CON-242: the speed targets in docs/plans/con-238-memory-spaces.md ("Targets and the load test"), at 50,000 current
/// memories in one team space (about 65,500 versions), 200 spaces in all, one fact with 500 versions, and a read-only
/// viewer who can read 1% of the cited documents. Every operation runs 20 times per reader against PostgreSQL capped
/// like every test container (2 CPUs, 4 GB); the 95th percentile must meet its target. The time measured is the
/// application operation the REST and MCP routes call, against the real stores. Who may read which document is faked:
/// the real check needs cloud identity, and its cost is one call per request either way.
/// Category=Load runs in no CI step; run it with <c>dotnet test --filter "Category=Load"</c>.
/// </summary>
[Trait("Category", "Load")]
[Collection(MemoryLoadCollection.Name)]
public sealed class MemorySpacesLoadTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int Runs = 20;
    private const int Facts = 50_000;
    private const int Documents = 5_000;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_memory_load")
        .WithUsername("load_test")
        .WithPassword("load_test")
        .WithCreateParameterModifier(TestContainerLimits.Apply)
        .Build();

    private readonly Guid _reader = Guid.NewGuid(), _viewer = Guid.NewGuid();
    private readonly Guid[] _authors = [.. Enumerable.Range(0, 20).Select(_ => Guid.NewGuid())];
    private readonly string[] _documents = [.. Enumerable.Range(0, Documents).Select(_ => Guid.NewGuid().ToString())];
    private readonly DateTimeOffset _start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly List<Timing> _timings = [];

    private sealed record Timing(string Operation, string Reader, double P50, double P95, double Target, string Note);

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private DbContextOptions<KnowledgeDbContext> Options() => new DbContextOptionsBuilder<KnowledgeDbContext>()
        .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseVector())
        .Options;

    /// <summary>The full reader reads every document; the viewer reads every hundredth one.</summary>
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

    private sealed class NoLinks : ILinkIndex
    {
        private static readonly ILookup<string, string> Empty = Array.Empty<(string, string)>().ToLookup(p => p.Item1, p => p.Item2);

        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task<ILookup<string, string>> NodesOfAsync(IReadOnlyCollection<string> documentIds, CancellationToken ct = default) =>
            Task.FromResult(Empty);

        public Task<ILookup<string, string>> DocumentsOfAsync(IReadOnlyCollection<string> nodeIds, CancellationToken ct = default) =>
            Task.FromResult(Empty);
    }

    /// <summary>The viewer holds knowledge:read and nothing else; everyone else may do anything.</summary>
    private sealed class ReadOnlyViewer(Guid viewer) : IOperationAuthorizer
    {
        public Task<bool> AllowsAsync(ClaimsPrincipal user, string policy) => Task.FromResult(
            user.FindFirstValue(ClaimTypes.NameIdentifier) != viewer.ToString() || policy == Policies.KnowledgeRead);
    }

    private async Task MigrateAsync()
    {
        await using var context = new KnowledgeDbContext(Options());
        await context.GetService<IMigrator>().MigrateAsync();
    }

    /// <summary>
    /// Seeds the data and returns the big team space and the current version of the 500-version fact. The big space
    /// holds 50,000 current facts by 20 authors, 15,000 of them corrected once, 70% citing one of 5,000 documents and
    /// 1 in 20 mentioning a refund; the other 199 spaces (personal, team and documents containers) hold 50 facts each.
    /// </summary>
    private async Task<(Guid Big, Guid LongFact)> SeedAsync()
    {
        Guid big = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var seed = new NpgsqlCommand(
            """
            INSERT INTO containers (id, name, description, kind, audience, created_at, updated_at)
            VALUES (@big, 'load-big', 'the big team space', 'memory', 'team', @t0, @t0);

            CREATE TEMP TABLE seed AS
            SELECT g, gen_random_uuid() AS chain, gen_random_uuid() AS cur FROM generate_series(1, @facts) g;

            INSERT INTO memory_facts (id, chain_id, container_id, subject, attribute, value, recorded_at, superseded_at,
                                      author_user_id, scope)
            SELECT s.chain, s.chain, @big, 'subject ' || s.g % 2000, 'attribute ' || s.g % 7, 'earlier note ' || s.g,
                   @t0 + make_interval(secs => s.g * 60), @t0 + make_interval(secs => s.g * 60 + 30),
                   (@authors)[1 + s.g % 20], 1
            FROM seed s WHERE s.g % 10 < 3;

            INSERT INTO memory_facts (id, chain_id, container_id, subject, attribute, value, valid_from, recorded_at,
                                      replaces_id, author_user_id, scope)
            SELECT CASE WHEN s.g % 10 < 3 THEN s.cur ELSE s.chain END, s.chain, @big,
                   'subject ' || s.g % 2000, 'attribute ' || s.g % 7,
                   CASE WHEN s.g % 20 = 0 THEN 'refund policy note ' ELSE 'plain note ' END || s.g,
                   CASE WHEN s.g % 4 = 0 THEN @t0 - make_interval(days => s.g % 365) END,
                   @t0 + make_interval(secs => s.g * 60 + 45),
                   CASE WHEN s.g % 10 < 3 THEN s.chain END, (@authors)[1 + s.g % 20], 1
            FROM seed s;

            INSERT INTO memory_fact_sources (fact_id, ordinal, kind, ref)
            SELECT CASE WHEN s.g % 10 < 3 THEN s.cur ELSE s.chain END, 0, 0, (@docs)[1 + (s.g / 10) % @documents]
            FROM seed s WHERE s.g % 10 < 7;

            CREATE TEMP TABLE long AS SELECT v, gen_random_uuid() AS id FROM generate_series(1, 500) v;
            INSERT INTO memory_facts (id, chain_id, container_id, subject, attribute, value, recorded_at, superseded_at,
                                      replaces_id, author_user_id, scope)
            SELECT l.id, (SELECT id FROM long WHERE v = 1), @big, 'long history', 'status', 'version ' || l.v,
                   @t0 + make_interval(secs => l.v), CASE WHEN l.v < 500 THEN @t0 + make_interval(secs => l.v + 1) END,
                   (SELECT p.id FROM long p WHERE p.v = l.v - 1), (@authors)[1], 1
            FROM long l;

            CREATE TEMP TABLE spaces AS
            SELECT n, gen_random_uuid() AS id,
                   CASE WHEN n <= 100 THEN 'personal' ELSE 'team' END AS audience,
                   CASE WHEN n > 160 THEN 'documents' ELSE 'memory' END AS kind,
                   CASE WHEN n <= 100 THEN CASE n % 3 WHEN 0 THEN @reader WHEN 1 THEN @viewer ELSE (@authors)[2 + n % 19] END
                   END AS owner
            FROM generate_series(1, 199) n;
            INSERT INTO containers (id, name, description, kind, audience, owner_user_id, created_at, updated_at)
            SELECT id, 'load-space-' || n, NULL, kind, audience, owner, @t0, @t0 FROM spaces;

            CREATE TEMP TABLE small AS
            SELECT s.id AS container, s.audience, s.owner, f, gen_random_uuid() AS id
            FROM spaces s CROSS JOIN generate_series(1, 50) f;
            INSERT INTO memory_facts (id, chain_id, container_id, subject, attribute, value, recorded_at, author_user_id, scope)
            SELECT id, id, container, 'topic ' || f % 10, 'attribute ' || f % 7, 'small note ' || f,
                   @t0 + make_interval(secs => f), COALESCE(owner, (@authors)[1 + f % 20]),
                   CASE WHEN audience = 'personal' THEN 0 ELSE 1 END
            FROM small;
            INSERT INTO memory_fact_sources (fact_id, ordinal, kind, ref)
            SELECT id, 0, 0, (@docs)[1 + (f * 97) % @documents] FROM small WHERE f % 10 < 7;

            ANALYZE;
            SELECT id FROM long WHERE v = 500;
            """, connection);
        seed.CommandTimeout = 600;
        seed.Parameters.AddWithValue("big", big);
        seed.Parameters.AddWithValue("t0", _start);
        seed.Parameters.AddWithValue("facts", Facts);
        seed.Parameters.AddWithValue("documents", Documents);
        seed.Parameters.AddWithValue("authors", _authors);
        seed.Parameters.AddWithValue("docs", _documents);
        seed.Parameters.AddWithValue("reader", _reader);
        seed.Parameters.AddWithValue("viewer", _viewer);
        Guid longFact = (Guid)(await seed.ExecuteScalarAsync())!;
        return (big, longFact);
    }

    private static OperationContext As(Guid user) =>
        new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], "test")), OperationSurface.Rest);

    /// <summary>Runs <paramref name="operation"/> twice to warm up, then <see cref="Runs"/> times, one after another.</summary>
    private async Task TimeAsync(string name, string reader, double targetMs, Func<Task<string?>> operation)
    {
        await operation();
        await operation();
        double[] times = new double[Runs];
        string? note = null;
        for (int i = 0; i < Runs; i++)
        {
            long started = Stopwatch.GetTimestamp();
            note = await operation();
            times[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(times);
        // Nearest rank: the 95th percentile of 20 runs is the 19th fastest.
        double p95 = times[(int)Math.Ceiling(0.95 * Runs) - 1];
        double p50 = times[(int)Math.Ceiling(0.50 * Runs) - 1];
        _timings.Add(new Timing(name, reader, p50, p95, targetMs, note ?? ""));
    }

    [Fact]
    public async Task EveryOperation_FiftyThousandMemories_MeetsItsTarget()
    {
        await MigrateAsync();
        Stopwatch seeding = Stopwatch.StartNew();
        (Guid big, Guid longFact) = await SeedAsync();
        output.WriteLine($"Seeded in {seeding.Elapsed.TotalSeconds:F0} s");

        var factory = new PooledDbContextFactory<KnowledgeDbContext>(Options());
        var access = new FakeAccess(new()
        {
            [_reader] = [.. _documents],
            [_viewer] = [.. _documents.Where((_, i) => i % 100 == 0)],
        });
        var memory = new MemoryService(new PostgresMemoryStore(factory, TimeProvider.System), access, new NoLinks(),
            Substitute.For<IAuditLogger>(), NullLogger<MemoryService>.Instance);
        var containers = new PostgresContainerStore(factory, NullLogger<PostgresContainerStore>.Instance);
        var authorizer = new ReadOnlyViewer(_viewer);
        var list = new ListMemoryFacts(containers, memory, authorizer);
        var count = new CountMemoryFacts(containers, memory, authorizer);
        var history = new ListMemoryHistory(containers, memory, authorizer);
        var spaces = new ListMemorySpaces(
            new PostgresMemorySpaceStore(factory, access, new NoLinks(), TimeProvider.System, NullLogger<PostgresMemorySpaceStore>.Instance),
            authorizer);
        string space = big.ToString();

        // The viewer is read-only: a save is refused before anything is written.
        var saved = await new RememberFact(containers, memory, authorizer).RunAsync(
            As(_viewer), space, new NewMemoryFact("Refunds", "window", "30 days", null, null, null));
        saved.Status.Should().Be(MemoryStatus.Forbidden);

        HashSet<string> viewerReads = [.. _documents.Where((_, i) => i % 100 == 0)];
        foreach ((Guid user, string who) in new[] { (_reader, "full reader"), (_viewer, "1% viewer") })
        {
            OperationContext ctx = As(user);

            async Task<MemoryFactsListed> Page(MemoryFactFilter filter, MemoryFactSort sort = MemoryFactSort.Recorded, string? cursor = null, int? limit = null)
            {
                var listed = await list.RunAsync(ctx, space, new MemoryFactListing(filter, sort, cursor, limit));
                listed.Succeeded.Should().BeTrue(listed.Detail);
                return listed.Result!;
            }

            Task FirstPage(string name, MemoryFactFilter filter, MemoryFactSort sort = MemoryFactSort.Recorded) =>
                TimeAsync($"First page, {name}", who, 150, async () => $"{(await Page(filter, sort)).Facts.Count} facts");

            await FirstPage("newest first", new MemoryFactFilter());
            await FirstPage("by subject", new MemoryFactFilter(), MemoryFactSort.Subject);
            await FirstPage("by valid from", new MemoryFactFilter(), MemoryFactSort.ValidFrom);
            // Subject 47's facts cite nothing, so the viewer sees them too.
            await FirstPage("one subject", new MemoryFactFilter(Subject: "subject 47"));
            await FirstPage("one author", new MemoryFactFilter(Author: _authors[3]));
            await FirstPage("cites something", new MemoryFactFilter(Cites: MemoryCites.Some));
            await FirstPage("cites nothing", new MemoryFactFilter(Cites: MemoryCites.None));
            await FirstPage("two-day range", new MemoryFactFilter(From: _start.AddDays(10), To: _start.AddDays(12)));
            await FirstPage("every version", new MemoryFactFilter(Status: MemoryFactStatus.All));

            // Only sources the viewer can read are ever shown to them.
            MemoryFactsListed cited = await Page(new MemoryFactFilter(Cites: MemoryCites.Some));
            cited.Facts.Should().NotBeEmpty();
            if (user == _viewer)
                cited.Facts.SelectMany(f => f.Sources).Should().OnlyContain(s => viewerReads.Contains(s.Ref));

            string second = (await Page(new MemoryFactFilter())).NextCursor!;
            await TimeAsync("Page 2, newest first", who, 150,
                async () => $"{(await Page(new MemoryFactFilter(), cursor: second)).Facts.Count} facts");
            string secondBySubject = (await Page(new MemoryFactFilter(), MemoryFactSort.Subject)).NextCursor!;
            await TimeAsync("Page 2, by subject", who, 150,
                async () => $"{(await Page(new MemoryFactFilter(), MemoryFactSort.Subject, secondBySubject)).Facts.Count} facts");

            // Walk to 80% of the way through, 100 at a time, and time the page there.
            List<string> cursors = [];
            string? next = null;
            int seen = 0;
            do
            {
                MemoryFactsListed page = await Page(new MemoryFactFilter(), cursor: next, limit: 100);
                seen += page.Facts.Count;
                next = page.NextCursor;
                if (next is not null)
                    cursors.Add(next);
            }
            while (next is not null);
            string deep = cursors[(int)(cursors.Count * 0.8)];
            int depth = ((int)(cursors.Count * 0.8) + 1) * 100;
            await TimeAsync($"Deep page, newest first", who, 150,
                async () => $"{(await Page(new MemoryFactFilter(), cursor: deep)).Facts.Count} facts, {depth:N0} in of {seen:N0}");

            await TimeAsync("Search, best match", who, 300,
                async () => $"{(await Page(new MemoryFactFilter(Text: "refund"), MemoryFactSort.Relevance)).Facts.Count} facts");
            await TimeAsync("Search, newest first", who, 300,
                async () => $"{(await Page(new MemoryFactFilter(Text: "refund"))).Facts.Count} facts");

            async Task<string?> Count(MemoryFactFilter filter)
            {
                var counted = await count.RunAsync(ctx, space, filter);
                counted.Succeeded.Should().BeTrue(counted.Detail);
                return $"{counted.Result!.Count:N0}{(counted.Result.Capped ? " (capped)" : "")}";
            }

            await TimeAsync("Count, every current fact", who, 2000, () => Count(new MemoryFactFilter()));
            await TimeAsync("Count, cites something", who, 2000, () => Count(new MemoryFactFilter(Cites: MemoryCites.Some)));
            await TimeAsync("Count, search", who, 2000, () => Count(new MemoryFactFilter(Text: "refund")));

            await TimeAsync("Memory page list, with counts", who, 300, async () =>
            {
                var listed = await spaces.RunAsync(ctx, new MemorySpaceQuery());
                listed.Succeeded.Should().BeTrue(listed.Detail);
                return $"{listed.Result!.Total} spaces";
            });

            await TimeAsync("History, first page of 500 versions", who, 100, async () =>
            {
                var read = await history.RunByFactAsync(ctx, longFact.ToString());
                read.Succeeded.Should().BeTrue(read.Detail);
                read.Result!.Total.Should().Be(500);
                return $"{read.Result.Versions.Count} of {read.Result.Total}";
            });
        }

        var table = new StringBuilder();
        table.AppendLine("| Operation | Reader | p50 ms | p95 ms | Target ms | Result | Met |");
        table.AppendLine("|---|---|---:|---:|---:|---|---|");
        foreach (Timing t in _timings)
        {
            table.AppendLine(CultureInfo.InvariantCulture,
                $"| {t.Operation} | {t.Reader} | {t.P50:F1} | {t.P95:F1} | {t.Target:F0} | {t.Note} | {(t.P95 < t.Target ? "yes" : "**no**")} |");
        }
        output.WriteLine(table.ToString());

        _timings.Where(t => t.P95 >= t.Target).Select(t => $"{t.Operation} ({t.Reader}): p95 {t.P95:F1} ms, target {t.Target:F0} ms")
            .Should().BeEmpty("every operation meets its target at 50,000 memories");
    }
}
