using Connapse.Core;
using Connapse.Storage.Containers;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-227: upgrading to the release that adds container kinds leaves every existing container a documents container,
/// with its documents and memory untouched. Owns a PostgreSQL container because it must stop the migrations short of
/// head, which the shared fixture never does.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ContainerKindMigrationTests : IAsyncLifetime
{
    /// <summary>The last migration before container kinds.</summary>
    private const string BeforeKinds = "20261031235835_RemoveSummaries";

    /// <summary>The last migration before CON-255 deletes the memory of document containers, which this test must not reach.</summary>
    private const string BeforeContainerMemoryDeleted = "20261104204230_MemoryPagingIndexes";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_kind_migration")
        .WithUsername("migration_test")
        .WithPassword("migration_test")
        .WithCreateParameterModifier(TestContainerLimits.Apply)
        .Build();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private DbContextOptions<KnowledgeDbContext> Options() => new DbContextOptionsBuilder<KnowledgeDbContext>()
        .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseVector())
        .Options;

    [Fact]
    public async Task Migrate_ExistingContainers_StayDocumentsWithTheirRows()
    {
        await using (var context = new KnowledgeDbContext(Options()))
        {
            await context.GetService<IMigrator>().MigrateAsync(BeforeKinds);
        }

        Guid container = Guid.NewGuid(), document = Guid.NewGuid(), fact = Guid.NewGuid();
        await using (var context = new KnowledgeDbContext(Options()))
        {
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO containers (id, name, description, created_at, updated_at) VALUES ({0}, 'legacy-docs', 'from v0.3', now(), now())",
                container);
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, created_at) VALUES ({0}, {1}, NULL, 'd.md', '/d.md', '', 1, now())",
                document, container);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO memory_facts (id, chain_id, container_id, subject, attribute, value, recorded_at)
                VALUES ({0}, {0}, {1}, 'project', 'language', 'C#', now())
                """,
                fact, container);

            await context.GetService<IMigrator>().MigrateAsync(BeforeContainerMemoryDeleted);
        }

        var factory = new PooledDbContextFactory<KnowledgeDbContext>(Options());
        var store = new PostgresContainerStore(factory, NullLogger<PostgresContainerStore>.Instance);
        var upgraded = await store.GetAsync(container);

        upgraded.Should().NotBeNull();
        upgraded!.Kind.Should().Be(ContainerKind.Documents, "every container before CON-227 held documents");
        upgraded.Name.Should().Be("legacy-docs");
        upgraded.Description.Should().Be("from v0.3");
        upgraded.DocumentCount.Should().Be(1);

        await using var check = new KnowledgeDbContext(Options());
        (await check.Database.SqlQueryRaw<string>("SELECT kind AS \"Value\" FROM containers WHERE id = {0}", container).SingleAsync())
            .Should().Be("documents");
        (await check.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM memory_facts WHERE id = {0}", fact).SingleAsync())
            .Should().Be(1, "the upgrade must not touch a container's memory");
    }

    [Fact]
    public async Task Migrate_ThenCreateMemoryContainer_RoundTripsItsKind()
    {
        await using (var context = new KnowledgeDbContext(Options()))
        {
            await context.GetService<IMigrator>().MigrateAsync();
        }

        var store = new PostgresContainerStore(new PooledDbContextFactory<KnowledgeDbContext>(Options()), NullLogger<PostgresContainerStore>.Instance);
        var created = await store.CreateAsync(new CreateContainerRequest("agent-notes", null, ContainerKind.Memory));

        created.Kind.Should().Be(ContainerKind.Memory);
        (await store.GetAsync(Guid.Parse(created.Id)))!.Kind.Should().Be(ContainerKind.Memory);
        (await store.GetByNameAsync("agent-notes"))!.Kind.Should().Be(ContainerKind.Memory);
        (await store.ListAsync()).Single().Kind.Should().Be(ContainerKind.Memory);
    }
}
