using System.Diagnostics;
using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Memory;
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
using NpgsqlTypes;
using NSubstitute;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-236: moving today's memory into spaces leaves every reader seeing exactly the facts they saw before, keeps
/// every fact and container id, and leaves agent memory containers as team spaces under their old ids. Owns a
/// PostgreSQL container because it must stop the migrations short of head.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemorySpaceMigrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    /// <summary>The last migration before memory moves into spaces.</summary>
    private const string BeforeMove = "20261104183245_AddMemorySpaces";

    private const short Personal = (short)MemoryScope.Personal;
    private const short Team = (short)MemoryScope.Team;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_space_migration")
        .WithUsername("migration_test")
        .WithPassword("migration_test")
        .WithCreateParameterModifier(TestContainerLimits.Apply)
        .Build();

    private readonly Guid _alice = Guid.NewGuid(), _bob = Guid.NewGuid(), _carol = Guid.NewGuid();
    private readonly Guid _agentAlone = Guid.NewGuid(), _alicesAgent = Guid.NewGuid();
    private readonly string _shared = Guid.NewGuid().ToString(), _restricted = Guid.NewGuid().ToString();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private DbContextOptions<KnowledgeDbContext> Options() => new DbContextOptionsBuilder<KnowledgeDbContext>()
        .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseVector())
        .Options;

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
        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task<ILookup<string, string>> NodesOfAsync(IReadOnlyCollection<string> documentIds, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ILookup<string, string>> DocumentsOfAsync(IReadOnlyCollection<string> nodeIds, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<(string, string)>().ToLookup(p => p.Item1, p => p.Item2));
    }

    private sealed class AllowAll : Connapse.Application.Ports.IOperationAuthorizer
    {
        public Task<bool> AllowsAsync(ClaimsPrincipal user, string policy) => Task.FromResult(true);
    }

    private MemoryService Service() => new(
        new PostgresMemoryStore(new PooledDbContextFactory<KnowledgeDbContext>(Options()), TimeProvider.System),
        new FakeAccess(new() { [_alice] = [_shared, _restricted], [_bob] = [_shared, _restricted], [_carol] = [_shared] }),
        new NoLinks(), Substitute.For<IAuditLogger>(), NullLogger<MemoryService>.Instance);

    private PostgresContainerStore Containers() =>
        new(new PooledDbContextFactory<KnowledgeDbContext>(Options()), NullLogger<PostgresContainerStore>.Instance);

    /// <summary>Everyone whose view is compared: users, an agent acting for a user, an agent alone and nobody at all.</summary>
    private MemoryCaller[] Readers() =>
    [
        new(_alice, null, "alice"),
        new(_alice, _alicesAgent, "alice's agent"),
        new(_bob, null, "bob"),
        new(_carol, null, "carol"),
        new(null, _agentAlone, "lone agent"),
        new(null, null, null),
    ];

    private async Task MigrateAsync(string? target = null)
    {
        await using var context = new KnowledgeDbContext(Options());
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<Guid> ContainerAsync(string name, string kind = "documents", Guid? ownerUser = null, Guid? ownerAgent = null)
    {
        Guid id = Guid.NewGuid();
        await using var context = new KnowledgeDbContext(Options());
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO containers (id, name, description, kind, audience, owner_user_id, owner_agent_id, created_at, updated_at)
            VALUES ({0}, {1}, 'kept as it was', {2}, {3}, {4}, {5}, now(), now())
            """,
            id, name, kind, ownerUser is null && ownerAgent is null ? "team" : "personal",
            Uuid(ownerUser), Uuid(ownerAgent));
        return id;
    }

    private static NpgsqlParameter Uuid(Guid? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)value ?? DBNull.Value };

    /// <summary>Writes one chain, oldest version first; every version but the last is superseded by the next.</summary>
    private async Task<Guid[]> ChainAsync(Guid container, params (Guid? User, Guid? Agent, short Scope, string? Source)[] versions)
    {
        Guid[] ids = [.. versions.Select(_ => Guid.NewGuid())];
        await using var context = new KnowledgeDbContext(Options());
        for (int i = 0; i < versions.Length; i++)
        {
            (Guid? user, Guid? agent, short scope, string? source) = versions[i];
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO memory_facts (id, chain_id, container_id, subject, attribute, value, recorded_at, superseded_at,
                                          replaces_id, author_user_id, author_agent_id, scope)
                VALUES ({0}, {1}, {2}, 'Halcyon', 'database', {3}, now() + make_interval(secs => {4}),
                        CASE WHEN {5} THEN NULL ELSE now() + make_interval(secs => {4} + 1) END, {6}, {7}, {8}, {9})
                """,
                ids[i], ids[0], container, $"v{i} of {ids[0]}", i, i == versions.Length - 1,
                Uuid(i == 0 ? null : ids[i - 1]), Uuid(user), Uuid(agent), scope);
            if (source is not null)
            {
                await context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO memory_fact_sources (fact_id, ordinal, kind, ref) VALUES ({0}, 0, {1}, {2})",
                    ids[i], (short)MemorySourceKind.Document, source);
            }
        }
        return ids;
    }

    /// <summary>Every fact id a reader sees in every container and space they can see, with history and without.</summary>
    private async Task<(string[] Current, string[] History)> ViewAsync(MemoryCaller reader)
    {
        MemoryService service = Service();
        List<string> current = [], history = [];
        foreach (Container container in await Containers().ListAsync(0, 1000, reader))
        {
            Guid id = Guid.Parse(container.Id);
            MemoryRecall now = await service.RecallAsync(new MemoryQuery(id, Limit: 100), reader);
            MemoryRecall all = await service.RecallAsync(new MemoryQuery(id, History: true, Limit: 100), reader);
            now.Denied.Should().BeFalse();
            all.Denied.Should().BeFalse();
            current.AddRange(now.Facts.Select(f => $"{f.Fact.Id} {f.Fact.Value}"));
            history.AddRange(all.Facts.Select(f => $"{f.Fact.Id} {f.Fact.Value}"));
        }
        return ([.. current.Order(StringComparer.Ordinal)], [.. history.Order(StringComparer.Ordinal)]);
    }

    private async Task<Dictionary<Guid, Guid>> FactContainersAsync()
    {
        await using var context = new KnowledgeDbContext(Options());
        return await context.MemoryFacts.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.ContainerId);
    }

    [Fact]
    public async Task Migrate_EveryKindOfMemory_EveryReaderSeesTheSameFacts()
    {
        await MigrateAsync(BeforeMove);

        Guid halcyon = await ContainerAsync("halcyon");
        Guid agentNotes = await ContainerAsync("agent-notes", kind: "memory");
        string longName = new string('l', 120) + "-x";
        Guid longContainer = await ContainerAsync(longName);
        Guid alicesSpace = await ContainerAsync("halcyon-personal", kind: "memory", ownerUser: _alice);

        Guid[] team = await ChainAsync(halcyon, (_bob, null, Team, _shared));
        Guid[] alices = await ChainAsync(halcyon, (_alice, null, Personal, null), (_alice, _alicesAgent, Personal, null));
        Guid[] carols = await ChainAsync(halcyon, (_carol, null, Personal, null));
        Guid[] lone = await ChainAsync(halcyon, (null, _agentAlone, Personal, null));
        Guid[] bobsCited = await ChainAsync(halcyon, (_bob, null, Personal, _restricted));
        // Saved before scopes existed: personal, corrected by Bob citing a document, then corrected again with nothing.
        Guid[] mixed = await ChainAsync(halcyon, (_alice, null, Personal, null), (_bob, null, Team, _shared), (_alice, null, Personal, null));
        // Personal versions by two different people: nobody can own a space it fits in.
        Guid[] twoAuthors = await ChainAsync(halcyon, (_alice, null, Personal, null), (_bob, null, Personal, null));
        Guid[] noAuthor = await ChainAsync(halcyon, (null, null, Personal, null));
        Guid[] agentTeam = await ChainAsync(agentNotes, (null, _agentAlone, Team, null));
        Guid[] agentPersonal = await ChainAsync(agentNotes, (_alice, _alicesAgent, Personal, null));
        Guid[] longPersonal = await ChainAsync(longContainer, (_bob, null, Personal, null));
        Guid[] alreadyInSpace = await ChainAsync(alicesSpace, (_alice, null, Personal, null));

        Dictionary<Guid, Guid> placesBefore = await FactContainersAsync();
        Dictionary<string, (string[] Current, string[] History)> before = [];
        foreach (MemoryCaller reader in Readers())
            before[reader.Name ?? "nobody"] = await ViewAsync(reader);

        await MigrateAsync();

        foreach (MemoryCaller reader in Readers())
        {
            (string[] current, string[] history) = await ViewAsync(reader);
            current.Should().Equal(before[reader.Name ?? "nobody"].Current, $"{reader.Name ?? "nobody"} must see the same current facts");
            history.Should().Equal(before[reader.Name ?? "nobody"].History, $"{reader.Name ?? "nobody"} must see the same history");
        }
        before["alice"].History.Should().HaveCountGreaterThan(5, "the comparison must cover real facts");

        Dictionary<Guid, Guid> places = await FactContainersAsync();
        places.Keys.Should().BeEquivalentTo(placesBefore.Keys, "no fact is added or lost and no id changes");

        await using var context = new KnowledgeDbContext(Options());
        var spaces = await context.Containers.AsNoTracking()
            .Where(c => c.Audience == MemoryScope.Personal)
            .Select(c => new { c.Id, c.Name, c.Description, c.OwnerUserId, c.OwnerAgentId })
            .ToListAsync();
        Guid SpaceOf(string name, Guid? user, Guid? agent) =>
            spaces.Single(s => s.Name == name && s.OwnerUserId == user && s.OwnerAgentId == agent).Id;

        foreach (Guid id in alices.Concat(alreadyInSpace))
            places[id].Should().Be(alicesSpace, "Alice's space from CON-235 is reused, not duplicated");
        places[carols[0]].Should().Be(SpaceOf("halcyon-personal", _carol, null));
        places[lone[0]].Should().Be(SpaceOf("halcyon-personal", null, _agentAlone), "an agent with no user owns its own space");
        places[bobsCited[0]].Should().Be(SpaceOf("halcyon-personal", _bob, null));
        places[agentPersonal[0]].Should().Be(SpaceOf("agent-notes-personal", _alice, null), "the owner is the user an agent acts for");
        places[longPersonal[0]].Should().Be(SpaceOf(new string('l', 119) + "-personal", _bob, null), "the name is cut to stay valid");
        foreach (Guid id in team.Concat(mixed).Concat(twoAuthors).Concat(noAuthor))
            places[id].Should().Be(halcyon, "team, mixed, many-author and authorless chains stay where they are");
        places[agentTeam[0]].Should().Be(agentNotes);

        spaces.Single(s => s.Id == SpaceOf("halcyon-personal", _carol, null)).Description
            .Should().StartWith("Moved from halcyon's Memory tab on ");
        spaces.Single(s => s.Id == alicesSpace).Description.Should().Be("kept as it was");

        Container? agentSpace = await Containers().GetAsync(agentNotes, new MemoryCaller(null, _agentAlone, null));
        agentSpace.Should().NotBeNull("an agent memory container opens under its old id");
        agentSpace!.Kind.Should().Be(ContainerKind.Memory);
        agentSpace.Audience.Should().Be(MemoryScope.Team);
        agentSpace.Name.Should().Be("agent-notes");

        // Running it again moves nothing.
        await context.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%_MoveMemoryIntoSpaces'");
        await MigrateAsync();
        (await FactContainersAsync()).Should().BeEquivalentTo(places);
    }

    [Fact]
    public async Task Migrate_MovedFact_CorrectAndForgetStillReachItThroughItsOldContainer()
    {
        await MigrateAsync(BeforeMove);
        Guid halcyon = await ContainerAsync("halcyon");
        Guid[] alices = await ChainAsync(halcyon, (_alice, null, Personal, null));
        await MigrateAsync();
        Guid moved = (await FactContainersAsync())[alices[0]];
        moved.Should().NotBe(halcyon, "the fact moved into Alice's space");

        MemoryService service = Service();
        var correct = new CorrectFact(service, new AllowAll());
        var forget = new ForgetFact(service, new AllowAll());
        OperationContext alice = As(_alice), bob = As(_bob);
        var replacement = new NewMemoryFact("Halcyon", "database", "PostgreSQL 17", DateTimeOffset.UtcNow.AddDays(1), null, null);

        (await correct.RunAsync(bob, halcyon.ToString(), alices[0].ToString(), MemoryCorrection.Changed, replacement))
            .Status.Should().Be(MemoryStatus.FactNotFound, "Bob never saw Alice's personal fact");
        (await forget.RunAsync(bob, "halcyon", alices[0].ToString())).Status.Should().Be(MemoryStatus.FactNotFound);

        var corrected = await correct.RunAsync(alice, halcyon.ToString(), alices[0].ToString(), MemoryCorrection.Changed, replacement);
        corrected.Status.Should().Be(MemoryStatus.Success);
        corrected.Result!.Fact.ContainerId.Should().Be(moved, "a correction stays in the fact's space");
        corrected.Result.Fact.Scope.Should().Be(MemoryScope.Personal);

        var forgotten = await forget.RunAsync(alice, "halcyon", corrected.Result.Fact.Id.ToString());
        forgotten.Status.Should().Be(MemoryStatus.Success);
        forgotten.Result.Should().Be(2);
        (await FactContainersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Migrate_FiftyThousandFacts_MovesInUnderTwoMinutes()
    {
        await MigrateAsync(BeforeMove);
        Guid big = await ContainerAsync("big");
        await using (var context = new KnowledgeDbContext(Options()))
        {
            // 50,000 single-version chains by 50 people, 45,000 personal and 5,000 team, as one container could hold.
            context.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
            await context.Database.ExecuteSqlRawAsync(
                """
                WITH authors AS (SELECT gen_random_uuid() AS id, row_number() OVER () AS n FROM generate_series(1, 50))
                INSERT INTO memory_facts (id, chain_id, container_id, subject, attribute, value, recorded_at, author_user_id, scope)
                SELECT f.id, f.id, {0}, 'subject ' || f.g % 500, 'attribute ' || f.g % 7, 'value ' || f.g, now(), a.id,
                       CASE WHEN f.g % 10 = 0 THEN 1 ELSE 0 END
                FROM (SELECT g, gen_random_uuid() AS id FROM generate_series(1, 50000) g) f
                JOIN authors a ON a.n = (f.g / 10) % 50 + 1
                """, big);
        }

        Stopwatch watch = Stopwatch.StartNew();
        await MigrateAsync();
        watch.Stop();
        output.WriteLine($"50,000 facts migrated in {watch.Elapsed.TotalSeconds:F1} s");

        await using var check = new KnowledgeDbContext(Options());
        (await check.MemoryFacts.CountAsync(f => f.ContainerId == big)).Should().Be(5000, "only team facts stay");
        (await check.Containers.CountAsync(c => c.Audience == MemoryScope.Personal)).Should().Be(50);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(2), "a 50,000-fact container moves in under 2 minutes");
    }

    private static OperationContext As(Guid user) =>
        new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], "test")), OperationSurface.Mcp);
}
