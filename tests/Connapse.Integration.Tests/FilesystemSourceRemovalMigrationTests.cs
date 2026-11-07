using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-265: upgrading an install that saved Filesystem sources removes them, their connections and everything indexed
/// from them, and leaves every other source, connection and container as it was. Owns its PostgreSQL container, like
/// <see cref="StrandedAwsSignInSettingsMigrationTests"/>, because it must stop before the migration.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class FilesystemSourceRemovalMigrationTests : IAsyncLifetime
{
    private const string Before = "20261107053324_DropStrandedAwsSignInSettings";
    private const int Filesystem = 1;
    private const int S3 = 3;
    private const int Sftp = 5;
    private const int GitHub = 6;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_filesystem_removal")
        .WithUsername("migration_test")
        .WithPassword("migration_test")
        .WithCreateParameterModifier(TestContainerLimits.Apply)
        .Build();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private KnowledgeDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<KnowledgeDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseVector())
            .Options);

    private static NpgsqlParameter Nullable(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    private static async Task<Guid> ConnectionAsync(KnowledgeDbContext context, string name, int provider)
    {
        Guid id = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO connections (id, name, provider, config, created_at, updated_at) VALUES ({0}, {1}, {2}, '{{}}'::jsonb, now(), now())",
            id, name, provider);
        return id;
    }

    private static async Task<Guid> SourceAsync(KnowledgeDbContext context, string name, Guid? connection, int? provider)
    {
        Guid id = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO sources (id, name, connection_id, provider, scope, created_at, updated_at) VALUES ({0}, {1}, {2}, {3}, '{{}}'::jsonb, now(), now())",
            id, name, Nullable("connection", NpgsqlDbType.Uuid, connection), Nullable("provider", NpgsqlDbType.Integer, provider));
        return id;
    }

    /// <summary>A document with a chunk, a vector and link rows, owned by a source or a container.</summary>
    private static async Task<Guid> IndexedDocumentAsync(KnowledgeDbContext context, Guid? source, Guid? container)
    {
        Guid document = Guid.NewGuid();
        Guid chunk = Guid.NewGuid();
        Guid owner = (source ?? container)!.Value;
        string documentId = document.ToString();
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, created_at) VALUES ({0}, {1}, {2}, 'd.md', {3}, '', 1, now())",
            document, Nullable("container", NpgsqlDbType.Uuid, container), Nullable("source", NpgsqlDbType.Uuid, source), $"/{documentId}.md");
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO chunks (id, document_id, owner_id, content, chunk_index, token_count, start_offset, end_offset) VALUES ({0}, {1}, {2}, 'indexed text', 0, 2, 0, 12)",
            chunk, document, owner);
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO chunk_vectors (chunk_id, document_id, owner_id, embedding, model_id) VALUES ({0}, {1}, {2}, '[0.1,0.2,0.3]'::vector, 'test-model')",
            chunk, document, owner);
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO link_edges (edge_id, from_node, to_node, type, document_id, evidence, ordinal) VALUES ({0}, 'a', 'b', 'links', {0}, 'indexed text', 0)",
            documentId);
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO link_node_facts (node_id, field, text, document_id, ordinal) VALUES ('a', 0, 'indexed text', {0}, 0)",
            documentId);
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO link_node_documents (node_id, document_id) VALUES ('a', {0})", documentId);
        return document;
    }

    private async Task<(int Documents, int Chunks, int Vectors, int Edges, int Facts, int Nodes)> IndexedRowsAsync(Guid document)
    {
        await using var context = CreateContext();
        string documentId = document.ToString();
        return (
            await context.Documents.CountAsync(d => d.Id == document),
            await context.Chunks.CountAsync(c => c.DocumentId == document),
            await context.ChunkVectors.CountAsync(v => v.DocumentId == document),
            await context.LinkEdges.CountAsync(e => e.DocumentId == documentId),
            await context.LinkNodeFacts.CountAsync(f => f.DocumentId == documentId),
            await context.LinkNodeDocuments.CountAsync(n => n.DocumentId == documentId));
    }

    [Fact]
    public async Task Migration_FilesystemRows_AreGoneWithEverythingIndexedFromThem()
    {
        Guid onConnection, connectionLess;
        await using (var context = CreateContext())
        {
            await context.GetService<IMigrator>().MigrateAsync(Before);
            Guid connection = await ConnectionAsync(context, "local-docs", Filesystem);
            onConnection = await IndexedDocumentAsync(context, await SourceAsync(context, "team-a", connection, null), null);
            connectionLess = await IndexedDocumentAsync(context, await SourceAsync(context, "orphan", null, Filesystem), null);
            await SourceAsync(context, "never-synced", connection, null);

            await context.GetService<IMigrator>().MigrateAsync();
        }

        await using var after = CreateContext();
        (await after.Sources.AsNoTracking().ToListAsync()).Should().BeEmpty();
        (await after.Connections.AsNoTracking().ToListAsync()).Should().BeEmpty();
        (await IndexedRowsAsync(onConnection)).Should().Be((0, 0, 0, 0, 0, 0));
        (await IndexedRowsAsync(connectionLess)).Should().Be((0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task Migration_EverythingElse_IsUntouched()
    {
        Guid s3Connection, sftpConnection, s3Source, sftpSource, gitHubSource, container;
        Guid s3Document, sftpDocument, gitHubDocument, containerDocument;
        await using (var context = CreateContext())
        {
            await context.GetService<IMigrator>().MigrateAsync(Before);
            s3Connection = await ConnectionAsync(context, "bucket", S3);
            sftpConnection = await ConnectionAsync(context, "server", Sftp);
            s3Source = await SourceAsync(context, "reports", s3Connection, null);
            sftpSource = await SourceAsync(context, "folders", sftpConnection, null);
            gitHubSource = await SourceAsync(context, "public-docs", null, GitHub);
            container = Guid.NewGuid();
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO containers (id, name, created_at, updated_at) VALUES ({0}, 'uploads', now(), now())", container);
            s3Document = await IndexedDocumentAsync(context, s3Source, null);
            sftpDocument = await IndexedDocumentAsync(context, sftpSource, null);
            gitHubDocument = await IndexedDocumentAsync(context, gitHubSource, null);
            containerDocument = await IndexedDocumentAsync(context, null, container);

            Guid filesystem = await ConnectionAsync(context, "local-docs", Filesystem);
            await IndexedDocumentAsync(context, await SourceAsync(context, "team-a", filesystem, null), null);

            await context.GetService<IMigrator>().MigrateAsync();
        }

        await using var after = CreateContext();
        (await after.Connections.AsNoTracking().Select(c => c.Id).ToListAsync())
            .Should().BeEquivalentTo([s3Connection, sftpConnection]);
        (await after.Sources.AsNoTracking().Select(s => s.Id).ToListAsync())
            .Should().BeEquivalentTo([s3Source, sftpSource, gitHubSource]);
        (await after.Containers.AsNoTracking().Select(c => c.Id).ToListAsync()).Should().Contain(container);
        foreach (Guid document in new[] { s3Document, sftpDocument, gitHubDocument, containerDocument })
            (await IndexedRowsAsync(document)).Should().Be((1, 1, 1, 1, 1, 1));
    }
}
