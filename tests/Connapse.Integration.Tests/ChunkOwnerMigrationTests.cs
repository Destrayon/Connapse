using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Applies the composite-owner FK migration to a database that already holds documents,
/// chunks and vectors.
/// <para>
/// Every other test starts from an empty schema, so the constraint had only ever been added
/// to empty tables — the upgrade path rested on reasoning about <c>NOT VALID</c> rather than
/// on evidence. This runs the real upgrade: migrate to the release before it, write rows the
/// way an existing deployment would hold them, then migrate the rest of the way.
/// </para>
/// <para>
/// Owns a PostgreSQL container because it must control which migrations have been applied;
/// the shared fixture arrives already at head.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ChunkOwnerMigrationTests : IAsyncLifetime
{
    /// <summary>The release immediately before the constraint was introduced.</summary>
    private const string BeforeConstraint = "20260814053525_AddConnectionsAndSources";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_migration_test")
        .WithUsername("migration_test")
        .WithPassword("migration_test")
        .Build();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private KnowledgeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<KnowledgeDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseVector())
            .Options;

        return new KnowledgeDbContext(options);
    }

    /// <summary>Writes a container, a document, and one chunk and vector owned by the given owner.</summary>
    private static async Task SeedOwnedRowsAsync(
        KnowledgeDbContext context, Guid documentId, Guid documentOwner, Guid chunkOwner)
    {
        var containerId = documentOwner;
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO containers (id, name, created_at, updated_at) VALUES ({0}, {1}, now(), now()) ON CONFLICT DO NOTHING",
            containerId, $"c-{containerId:N}"[..20]);

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, created_at) VALUES ({0}, {1}, NULL, 'd.md', {2}, '', 1, now())",
            documentId, containerId, $"/{documentId:N}.md");

        var chunkId = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO chunks (id, document_id, owner_id, content, chunk_index, token_count, start_offset, end_offset) VALUES ({0}, {1}, {2}, 'legacy content', 0, 2, 0, 14)",
            chunkId, documentId, chunkOwner);

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO chunk_vectors (chunk_id, document_id, owner_id, embedding, model_id) VALUES ({0}, {1}, {2}, '[0.1,0.2,0.3]'::vector, 'test-model')",
            chunkId, documentId, chunkOwner);
    }

    [Fact]
    public async Task Migration_OnPopulatedDatabaseIncludingADivergedRow_AppliesWithoutFailing()
    {
        await using var context = CreateContext();
        var migrator = context.GetService<IMigrator>();

        // Bring the schema to the release before the constraint, then fill it the way a
        // running deployment would have.
        await migrator.MigrateAsync(BeforeConstraint);

        var healthyDocument = Guid.NewGuid();
        var healthyOwner = Guid.NewGuid();
        await SeedOwnedRowsAsync(context, healthyDocument, healthyOwner, chunkOwner: healthyOwner);

        // A chunk whose owner does not match its document — the corruption the constraint
        // exists to stop. Seeded deliberately: if the migration validated existing rows, this
        // is the row that would abort startup for the whole deployment.
        var divergedDocument = Guid.NewGuid();
        var divergedOwner = Guid.NewGuid();
        await SeedOwnedRowsAsync(context, divergedDocument, divergedOwner, chunkOwner: Guid.NewGuid());

        Func<Task> migrate = async () => await migrator.MigrateAsync();

        await migrate.Should().NotThrowAsync(
            "NOT VALID skips the scan of existing rows, so legacy divergence must not block the upgrade");

        // The pre-existing bad row is still there — tolerated, not silently repaired.
        (await context.Chunks.CountAsync(c => c.DocumentId == divergedDocument))
            .Should().Be(1, "the migration must not delete or rewrite data behind the operator's back");
    }

    [Fact]
    public async Task Migration_OnPopulatedDatabase_StillRejectsNewDivergedRows()
    {
        await using var context = CreateContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(BeforeConstraint);

        var documentId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        await SeedOwnedRowsAsync(context, documentId, owner, chunkOwner: Guid.NewGuid());

        await migrator.MigrateAsync();

        // The point of NOT VALID: tolerate what is already there, enforce everything after.
        Func<Task> act = async () => await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO chunks (id, document_id, owner_id, content, chunk_index, token_count, start_offset, end_offset) VALUES (gen_random_uuid(), {0}, gen_random_uuid(), 'new', 1, 1, 0, 3)",
            documentId);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }
}
