using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search.Keyword;
using Connapse.Storage.Data;
using Connapse.Storage.Documents;
using Connapse.Storage.Vectors;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Upgrades a v0.3.x database that holds an S3 container and proves its old documents can't be read
/// afterwards (CON-226). In v0.3.x the bucket's grants decided who saw those documents; v0.4.0 drops the
/// container's connector record, which left the rows looking like uploads that every reader of the
/// container could see.
/// <para>
/// Owns a PostgreSQL container because it must control which migrations have been applied; the shared
/// fixture arrives already at head.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class LegacyCloudContainerUpgradeTests : IAsyncLifetime
{
    /// <summary>The last migration v0.3.x shipped.</summary>
    private const string V03Head = "20260305151037_RemoveEphemeralConnector";

    private const string Term = "zorblaxquarterly";
    private const string Model = "legacy-model";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_legacy_upgrade_test")
        .WithUsername("migration_test")
        .WithPassword("migration_test")
        .WithCreateParameterModifier(TestContainerLimits.Apply)
        .Build();

    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var builder = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString());
        builder.EnableDynamicJson();
        builder.UseVector();
        _dataSource = builder.Build();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private KnowledgeDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<KnowledgeDbContext>()
            .UseNpgsql(_dataSource, npgsql => npgsql.UseVector())
            .Options);

    /// <summary>A v0.3.x container (connector type 0 is MinIO uploads, 3 is S3) holding one indexed document.</summary>
    private static async Task<Guid> SeedV03ContainerAsync(KnowledgeDbContext context, int connectorType, string? connectorConfig)
    {
        Guid containerId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();
        Guid chunkId = Guid.NewGuid();

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO containers (id, name, connector_type, connector_config) VALUES ({0}, {1}, {2}, {3}::jsonb)",
            containerId, $"c-{containerId:N}"[..20], connectorType, connectorConfig!);
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO folders (id, container_id, path) VALUES (gen_random_uuid(), {0}, '/finance/')",
            containerId);
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO documents (id, container_id, file_name, path, content_hash, size_bytes, status) VALUES ({0}, {1}, 'q3.md', '/finance/q3.md', 'h', 1, 'Ready')",
            documentId, containerId);
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO chunks (id, document_id, container_id, content, chunk_index, token_count, start_offset, end_offset) VALUES ({0}, {1}, {2}, {3}, 0, 3, 0, 20)",
            chunkId, documentId, containerId, $"the {Term} numbers");
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO chunk_vectors (chunk_id, document_id, container_id, embedding, model_id) VALUES ({0}, {1}, {2}, '[1,0,0]'::vector, {3})",
            chunkId, documentId, containerId, Model);

        return containerId;
    }

    [Fact]
    public async Task Upgrade_V03S3Container_ItsOldDocumentsAreUnreadableByARestrictedReader()
    {
        Guid s3Container, uploads;
        await using (var context = CreateContext())
        {
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(V03Head);
            s3Container = await SeedV03ContainerAsync(context, connectorType: 3, """{"bucketName":"acme","region":"us-east-1"}""");
            uploads = await SeedV03ContainerAsync(context, connectorType: 0, connectorConfig: null);
            await migrator.MigrateAsync();

            (await context.Containers.CountAsync()).Should().Be(2, "the upgrade keeps the containers themselves");
        }

        // A reader holding a grant for some other part of the bucket, and one holding none at all.
        SearchScopes[] readers = [SearchScopes.OfPrefixes(["s3://acme/public/"]), SearchScopes.None];
        foreach (SearchScopes reader in readers)
        {
            (await KeywordHitsAsync(s3Container, reader)).Should().Be(0, "keyword search must not reach the bucket's old documents");
            (await VectorHitsAsync(s3Container, reader)).Should().Be(0, "vector search must not reach them either");

            // The upload container is the control: the same reader still finds its document both ways.
            (await KeywordHitsAsync(uploads, reader)).Should().Be(1);
            (await VectorHitsAsync(uploads, reader)).Should().Be(1);
        }

        (await ReadableDocumentsAsync(s3Container)).Should().BeEmpty("a direct file read must not hand them out");
        (await ReadableDocumentsAsync(uploads)).Should().HaveCount(1);

        await using (var context = CreateContext())
        {
            (await context.Folders.CountAsync(f => f.ContainerId == s3Container))
                .Should().Be(0, "folder names are the bucket's paths");
            (await context.Folders.CountAsync(f => f.ContainerId == uploads)).Should().Be(1);
        }
    }

    [Theory]
    [InlineData(ConnectionProvider.S3, true, false)]
    [InlineData(ConnectionProvider.AzureBlob, true, false)]
    [InlineData(ConnectionProvider.Atlassian, false, false)]
    [InlineData(ConnectionProvider.S3, false, false)]
    [InlineData(ConnectionProvider.Sftp, true, true)]
    [InlineData(ConnectionProvider.GitHub, false, true)]
    [InlineData(ConnectionProvider.Custom, false, true)]
    public async Task Search_SourceDocumentWithNoResourceUri_AdmittedOnlyWhenItsProviderNeverReportsOne(
        ConnectionProvider provider, bool throughConnection, bool admitted)
    {
        await using var context = CreateContext();
        await context.GetService<IMigrator>().MigrateAsync();

        Guid sourceId = await SeedUnaddressedSourceDocumentAsync(context, provider, throughConnection);
        int expected = admitted ? 1 : 0;

        foreach (SearchScopes reader in new[] { SearchScopes.OfPrefixes(["s3://acme/public/"]), SearchScopes.None })
        {
            (await KeywordHitsAsync(sourceId, reader)).Should().Be(expected);
            (await VectorHitsAsync(sourceId, reader)).Should().Be(expected);
        }
        (await ReadableDocumentsAsync(sourceId, bySource: true)).Should().HaveCount(expected);
    }

    /// <summary>A source of <paramref name="provider"/> holding one indexed document that has no resource URI.</summary>
    private static async Task<Guid> SeedUnaddressedSourceDocumentAsync(
        KnowledgeDbContext context, ConnectionProvider provider, bool throughConnection)
    {
        Guid? connectionId = null;
        if (throughConnection)
        {
            var connection = new Storage.Data.Entities.ConnectionEntity
            {
                Id = Guid.NewGuid(),
                Name = $"cn-{Guid.NewGuid():N}"[..20],
                Provider = (int)provider,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            context.Connections.Add(connection);
            connectionId = connection.Id;
        }

        var source = new Storage.Data.Entities.SourceEntity
        {
            Id = Guid.NewGuid(),
            Name = $"src-{Guid.NewGuid():N}"[..20],
            ConnectionId = connectionId,
            Provider = throughConnection ? null : (int)provider,
            ScopeJson = System.Text.Json.JsonDocument.Parse("{}"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Sources.Add(source);

        var document = new Storage.Data.Entities.DocumentEntity
        {
            Id = Guid.NewGuid(),
            SourceId = source.Id,
            FileName = "q3.md",
            Path = "/finance/q3.md",
            ContentHash = "h",
            IngestionStatus = DocumentStatus.Ready,
            CreatedAt = DateTime.UtcNow,
            Metadata = [],
        };
        context.Documents.Add(document);

        Guid chunkId = Guid.NewGuid();
        context.Chunks.Add(new Storage.Data.Entities.ChunkEntity
        {
            Id = chunkId,
            DocumentId = document.Id,
            OwnerId = source.Id,
            Content = $"the {Term} numbers",
            ChunkIndex = 0,
            Metadata = [],
        });
        context.ChunkVectors.Add(new Storage.Data.Entities.ChunkVectorEntity
        {
            ChunkId = chunkId,
            DocumentId = document.Id,
            OwnerId = source.Id,
            Embedding = new Pgvector.Vector(new float[] { 1f, 0f, 0f }),
            ModelId = Model,
            Dimensions = 3,
        });
        await context.SaveChangesAsync();

        return source.Id;
    }

    private async Task<int> KeywordHitsAsync(Guid containerId, SearchScopes scopes)
    {
        await using var context = CreateContext();
        var hits = await new KeywordSearchService(context, NullLogger<KeywordSearchService>.Instance).SearchAsync(
            Term, new SearchOptions(TopK: 10, ContainerId: containerId.ToString(), Mode: SearchMode.Keyword), scopes);
        return hits.Count;
    }

    private async Task<int> VectorHitsAsync(Guid containerId, SearchScopes scopes)
    {
        await using var context = CreateContext();
        var settings = Substitute.For<IOptionsMonitor<SearchSettings>>();
        settings.CurrentValue.Returns(new SearchSettings());
        var hits = await new PgVectorStore(context, settings, NullLogger<PgVectorStore>.Instance).SearchAsync(
            [1f, 0f, 0f], topK: 10,
            new Dictionary<string, string> { ["containerId"] = containerId.ToString(), ["modelId"] = Model },
            scopes);
        return hits.Count;
    }

    /// <summary>The owner's documents a reader with no cloud grants could open directly by id.</summary>
    private async Task<IReadOnlyList<string>> ReadableDocumentsAsync(Guid ownerId, bool bySource = false)
    {
        var store = new PostgresDocumentStore(new Factory(this), NullLogger<PostgresDocumentStore>.Instance);
        var denyAll = Substitute.For<ISearchResultVerifier>();
        denyAll.VerifyAsync(Arg.Any<IReadOnlyList<SearchHit>>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SearchHit>());
        var guard = new DocumentReadGuard(denyAll, store, NullLogger<DocumentReadGuard>.Instance);

        List<string> ids;
        await using (var context = CreateContext())
            ids = await context.Documents
                .Where(d => bySource ? d.SourceId == ownerId : d.ContainerId == ownerId)
                .Select(d => d.Id.ToString())
                .ToListAsync();

        var readable = new List<string>();
        Guid reader = Guid.NewGuid();
        foreach (string id in ids)
        {
            if (await store.GetAsync(id) is { } document && await guard.CanReadAsync(reader, document, CancellationToken.None))
                readable.Add(id);
        }
        return readable;
    }

    private sealed class Factory(LegacyCloudContainerUpgradeTests owner) : IDbContextFactory<KnowledgeDbContext>
    {
        public KnowledgeDbContext CreateDbContext() => owner.CreateContext();
    }
}
