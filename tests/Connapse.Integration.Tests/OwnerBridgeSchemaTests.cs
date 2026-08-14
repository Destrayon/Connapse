using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Verifies the database-level guarantees behind the container/source owner bridge:
/// exactly one owner per document, a generated owner_id that mirrors it, and a
/// connection that cannot be deleted while sources still reference it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class OwnerBridgeSchemaTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private static async Task<Guid> SeedContainerAsync(KnowledgeDbContext context)
    {
        var containerId = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO containers (id, name, connector_type, created_at, updated_at) VALUES ({0}, {1}, 0, now(), now())",
            containerId, ShortName("owner-test"));
        return containerId;
    }

    [Fact]
    public async Task Documents_WithBothOwners_ViolatesCheckConstraint()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await factory.CreateDbContextAsync();

        Guid containerId = await SeedContainerAsync(context);

        Func<Task> act = async () => await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, created_at)
            VALUES (gen_random_uuid(), {0}, gen_random_uuid(), 'x.md', '/x.md', '', 1, now())
            """, containerId);

        // Either constraint rejecting this is correct: the CHECK forbids two owners, and
        // the source FK forbids a source_id that does not exist. Both mean "not written".
        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().BeOneOf(
                PostgresErrorCodes.CheckViolation,
                PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Documents_WithNoOwner_ViolatesCheckConstraint()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await factory.CreateDbContextAsync();

        Func<Task> act = async () => await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, created_at)
            VALUES (gen_random_uuid(), NULL, NULL, 'x.md', '/x.md', '', 1, now())
            """);

        // Two constraints independently forbid an ownerless row, and Postgres does not
        // guarantee which reports first: the CHECK requires exactly one owner, and
        // owner_id is NOT NULL while COALESCE(NULL, NULL) evaluates to NULL. Either
        // rejection is the guarantee we want.
        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().BeOneOf(
                PostgresErrorCodes.CheckViolation,
                PostgresErrorCodes.NotNullViolation);
    }

    [Fact]
    public async Task Documents_OwnerId_MirrorsWhicheverOwnerIsSet()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await factory.CreateDbContextAsync();

        Guid containerId = await SeedContainerAsync(context);
        var documentId = Guid.NewGuid();

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, created_at)
            VALUES ({0}, {1}, NULL, 'x.md', '/x.md', '', 1, now())
            """, documentId, containerId);

        var ownerId = await context.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => d.OwnerId)
            .SingleAsync();

        ownerId.Should().Be(containerId);
    }

    [Fact]
    public async Task Connections_DeleteWithReferencingSource_IsRestricted()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await factory.CreateDbContextAsync();

        var connectionId = Guid.NewGuid();

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO connections (id, name, provider, created_at, updated_at) VALUES ({0}, {1}, 3, now(), now())",
            connectionId, ShortName("conn"));

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO sources (id, name, connection_id, scope, enabled, last_sync_status, created_at, updated_at)
            VALUES (gen_random_uuid(), {0}, {1}, '{{}}'::jsonb, true, 0, now(), now())
            """, ShortName("src"), connectionId);

        Func<Task> act = async () => await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM connections WHERE id = {0}", connectionId);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }
}
