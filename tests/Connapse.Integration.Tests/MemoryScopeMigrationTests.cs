using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Testcontainers.PostgreSql;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-234: upgrading to the release that adds personal and team memory leaves every existing fact, version by version,
/// seen by exactly the people who saw it before: a version citing sources by everyone who can read them, a version
/// citing none by its author alone. Owns a PostgreSQL container because it must stop the migrations short of head.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemoryScopeMigrationTests : IAsyncLifetime
{
    /// <summary>The last migration before memory scopes.</summary>
    private const string BeforeScopes = "20261103010402_AddContainerKind";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_scope_migration")
        .WithUsername("migration_test")
        .WithPassword("migration_test")
        .WithCreateParameterModifier(TestContainerLimits.Apply)
        .Build();

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

    [Fact]
    public async Task Migrate_ExistingFacts_KeepTheirAudienceVersionByVersion()
    {
        await using (var context = new KnowledgeDbContext(Options()))
        {
            await context.GetService<IMigrator>().MigrateAsync(BeforeScopes);
        }

        Guid container = Guid.NewGuid(), alice = Guid.NewGuid(), bob = Guid.NewGuid(), carol = Guid.NewGuid();
        string shared = Guid.NewGuid().ToString(), restricted = Guid.NewGuid().ToString();
        // One chain whose versions alternate: private, citing a document everyone reads, private again; and a fact by
        // Bob citing a document only Bob and Alice read.
        Guid chain = Guid.NewGuid(), v1 = chain, v2 = Guid.NewGuid(), v3 = Guid.NewGuid(), bobs = Guid.NewGuid();
        (Guid Id, Guid Chain, Guid? Replaces, Guid Author, string Value, string? Source)[] rows =
        [
            (v1, chain, null, alice, "v1", null),
            (v2, chain, v1, alice, "v2", shared),
            (v3, chain, v2, alice, "v3", null),
            (bobs, bobs, null, bob, "bobs", restricted),
        ];
        await using (var context = new KnowledgeDbContext(Options()))
        {
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO containers (id, name, description, created_at, updated_at) VALUES ({0}, 'legacy', NULL, now(), now())", container);
            foreach (var row in rows)
            {
                await context.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO memory_facts (id, chain_id, container_id, subject, attribute, value, recorded_at, superseded_at, replaces_id, author_user_id)
                    VALUES ({0}, {1}, {2}, 'Halcyon', 'database', {3}, now(), CASE WHEN {4} THEN NULL ELSE now() END, NULL, {5})
                    """,
                    row.Id, row.Chain, container, row.Value, row.Id == v3 || row.Id == bobs, row.Author);
                if (row.Replaces is { } replaces)
                {
                    await context.Database.ExecuteSqlRawAsync("UPDATE memory_facts SET replaces_id = {0} WHERE id = {1}", replaces, row.Id);
                }
                if (row.Source is { } source)
                {
                    await context.Database.ExecuteSqlRawAsync(
                        "INSERT INTO memory_fact_sources (fact_id, ordinal, kind, ref) VALUES ({0}, 0, {1}, {2})",
                        row.Id, (short)MemorySourceKind.Document, source);
                }
            }

            await context.GetService<IMigrator>().MigrateAsync();
        }

        Dictionary<Guid, HashSet<string>> readable = new() { [alice] = [shared, restricted], [bob] = [shared, restricted], [carol] = [shared] };
        var service = new MemoryService(new PostgresMemoryStore(new PooledDbContextFactory<KnowledgeDbContext>(Options()), TimeProvider.System),
            new FakeAccess(readable), new NoLinks(), Substitute.For<IAuditLogger>(), NullLogger<MemoryService>.Instance);

        foreach (Guid reader in new[] { alice, bob, carol })
        {
            // The rule before scopes, written out: a version citing sources was seen by whoever could read them all,
            // one citing none by its author alone.
            string[] before = [.. rows
                .Where(r => r.Source is { } s ? readable[reader].Contains(s) : r.Author == reader)
                .Select(r => r.Value).Order(StringComparer.Ordinal)];

            MemoryRecall after = await service.RecallAsync(new MemoryQuery(container, History: true, Limit: 100), new MemoryCaller(reader, null, null));

            after.Facts.Select(f => f.Fact.Value).Order(StringComparer.Ordinal).Should().Equal(before, $"reader {reader} must see what they saw before");
        }

        await using var check = new KnowledgeDbContext(Options());
        Dictionary<Guid, short> scopes = await check.MemoryFacts.Where(f => f.ContainerId == container).ToDictionaryAsync(f => f.Id, f => f.Scope);
        scopes.Should().BeEquivalentTo(new Dictionary<Guid, short>
        {
            [v1] = (short)MemoryScope.Personal,
            [v2] = (short)MemoryScope.Team,
            [v3] = (short)MemoryScope.Personal,
            [bobs] = (short)MemoryScope.Team,
        });
    }
}
