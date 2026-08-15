# Connector/Source Split — Phase 2 (Backfill and Compatibility Read) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Migrate every non-managed container into a connection plus source pair, repoint its documents to `source_id`, and keep existing container IDs working through a compatibility read.

**Architecture:** Each migrated source **reuses the old container's GUID**. Because `documents.owner_id` is `COALESCE(container_id, source_id)`, moving a document from `container_id` to `source_id` under the same GUID leaves `owner_id` byte-identical — so `chunks` and `chunk_vectors` need no writes, no vector reindex, and search results are unchanged by construction. The backfill runs once at startup, transactionally per container, and is idempotent. Container endpoints then resolve an unknown ID against `sources` before returning 404.

**Tech Stack:** .NET 10, EF Core (Npgsql), PostgreSQL/pgvector, xUnit, FluentAssertions, Testcontainers.

## Global Constraints

- Target framework .NET 10; file-scoped namespaces; nullable enabled; implicit usings.
- `Connapse.Core` has zero external dependencies — models and interfaces only.
- Records for DTOs and settings models; primary constructors for DI.
- Async all the way — never `.Result` or `.Wait()`.
- Never use `var` for primitive types. Never use `dynamic`.
- Parameterized SQL only — never string interpolation into SQL.
- Wrap user-controlled values in `LogSanitizer.Sanitize(...)` from `Connapse.Core.Utilities` when logging.
- Always use `IDbContextFactory<KnowledgeDbContext>` and short-lived contexts.
- Test naming: `MethodName_Scenario_ExpectedResult`. Tag `[Trait("Category", "Unit")]` or `[Trait("Category", "Integration")]`.
- Integration tests use `[Collection("Integration Tests")]` and reach DI via `fixture.Factory.Services.CreateAsyncScope()` — `IDbContextFactory` is Scoped and cannot resolve from the root provider.
- Commit format: `<type>: <summary>`. Milestone v0.4.0. Branch `feature/350-backfill-sources`.

## Scope

Phase 2 only, from `docs/superpowers/specs/2026-08-13-connector-source-split-design.md`. Out of scope: the connector contract split, the sync engine, any UI, and removing the compatibility read (Phases 3–5).

## Key design decisions

**Source IDs are preserved from the container.** This is what makes the phase safe. Alternatives (new GUIDs plus an ID map) would require rewriting `chunks.owner_id` and `chunk_vectors.owner_id` across the largest table in the system, forcing an IVFFlat reindex and making search parity something to verify rather than something guaranteed.

**Connections are deduplicated by credential identity, not per container.** Ten buckets in one AWS region under one role become one connection with ten sources. Dedup keys: S3 `(region, roleArn)`, Azure Blob `(storageAccountName, managedIdentityClientId)`, Filesystem `(rootPath)`.

Dedup is implemented by deriving a **deterministic connection name** from that key and looking it up against the unique index on `connections.name`. The obvious alternative — comparing the stored `ConfigJson` — is wrong: Postgres `jsonb` normalizes property order and whitespace on storage, so the round-tripped text does not match what `JsonSerializer` emitted, and every container would silently get its own connection.

**No backfilled connection carries a secret.** S3 uses `DefaultAWSCredentials` or an assumed role, Azure uses managed identity, and Filesystem needs none. Every backfilled connection has `SecretProtected = null`, so the migration never touches DataProtection.

**The container row is deleted after migration.** Its GUID lives on as the source, so nothing dangles. Folder rows are deleted with it — sources have no folder tree.

## File Structure

**Create:**
- `src/Connapse.Core/Models/BackfillModels.cs` — `ConnectionIdentity`, `BackfillPlanItem`, `BackfillReport` records.
- `src/Connapse.Core/Interfaces/IConnectorConfigMapper.cs` — splits legacy `connector_config` into connection identity plus source scope.
- `src/Connapse.Storage/Backfill/ConnectorConfigMapper.cs` — the pure mapping, unit-testable with no database.
- `src/Connapse.Storage/Backfill/SourceBackfillService.cs` — the transactional, resumable migration.
- `src/Connapse.Web/Services/SourceBackfillHostedService.cs` — runs it once at startup, after migrations.
- `tests/Connapse.Core.Tests/Backfill/ConnectorConfigMapperTests.cs`
- `tests/Connapse.Integration.Tests/SourceBackfillIntegrationTests.cs`
- `tests/Connapse.Integration.Tests/BackfillSearchParityTests.cs` — the test that makes this phase safe to ship.

**Modify:**
- `src/Connapse.Web/Endpoints/ContainersEndpoints.cs` — compatibility read at the four `containerStore.GetAsync` sites (lines 107, 139, 174, 329).
- `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs` — register mapper and backfill service.
- `src/Connapse.Web/Program.cs` — register the hosted service.

---

### Task 1: Connector config mapping

**Files:**
- Create: `src/Connapse.Core/Models/BackfillModels.cs`
- Create: `src/Connapse.Core/Interfaces/IConnectorConfigMapper.cs`
- Create: `src/Connapse.Storage/Backfill/ConnectorConfigMapper.cs`
- Test: `tests/Connapse.Core.Tests/Backfill/ConnectorConfigMapperTests.cs`

**Interfaces:**
- Consumes: `ConnectorType`, `ConnectionProvider` from Phase 1.
- Produces: `ConnectionIdentity(ConnectionProvider Provider, string DedupKey, string Name, string? ConfigJson)`; `IConnectorConfigMapper.Map(ConnectorType type, string? connectorConfig, string containerName)` returning `(ConnectionIdentity Connection, string ScopeJson)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Core.Tests/Backfill/ConnectorConfigMapperTests.cs`:

```csharp
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Backfill;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Backfill;

[Trait("Category", "Unit")]
public class ConnectorConfigMapperTests
{
    private readonly IConnectorConfigMapper _mapper = new ConnectorConfigMapper();

    [Fact]
    public void Map_S3_SplitsCredentialFromScope()
    {
        var config = """{"bucketName":"docs","region":"us-east-1","prefix":"team/","roleArn":"arn:aws:iam::1:role/r"}""";

        var (connection, scopeJson) = _mapper.Map(ConnectorType.S3, config, "docs-container");

        connection.Provider.Should().Be(ConnectionProvider.S3);
        connection.ConfigJson.Should().Contain("us-east-1").And.Contain("arn:aws:iam::1:role/r");
        // The bucket is scope, not credential — two buckets in one region share a connection.
        connection.ConfigJson.Should().NotContain("docs");
        scopeJson.Should().Contain("docs").And.Contain("team/");
    }

    [Fact]
    public void Map_S3_SameRegionAndRole_ProducesSameDedupKey()
    {
        var a = """{"bucketName":"one","region":"us-east-1","roleArn":"arn:r"}""";
        var b = """{"bucketName":"two","region":"us-east-1","roleArn":"arn:r"}""";

        _mapper.Map(ConnectorType.S3, a, "a").Connection.DedupKey
            .Should().Be(_mapper.Map(ConnectorType.S3, b, "b").Connection.DedupKey);
    }

    [Fact]
    public void Map_S3_DifferentRegion_ProducesDifferentDedupKey()
    {
        var a = """{"bucketName":"one","region":"us-east-1"}""";
        var b = """{"bucketName":"one","region":"eu-west-1"}""";

        _mapper.Map(ConnectorType.S3, a, "a").Connection.DedupKey
            .Should().NotBe(_mapper.Map(ConnectorType.S3, b, "b").Connection.DedupKey);
    }

    [Fact]
    public void Map_AzureBlob_SplitsAccountFromContainerName()
    {
        var config = """{"storageAccountName":"acct","containerName":"blobs","prefix":"p/","managedIdentityClientId":"mi-1"}""";

        var (connection, scopeJson) = _mapper.Map(ConnectorType.AzureBlob, config, "azure-container");

        connection.Provider.Should().Be(ConnectionProvider.AzureBlob);
        connection.ConfigJson.Should().Contain("acct").And.Contain("mi-1");
        scopeJson.Should().Contain("blobs").And.Contain("p/");
    }

    [Fact]
    public void Map_Filesystem_RootIsCredentialScopeIsPatterns()
    {
        var config = """{"rootPath":"/data","includePatterns":["*.md"],"excludePatterns":["*.tmp"]}""";

        var (connection, scopeJson) = _mapper.Map(ConnectorType.Filesystem, config, "fs-container");

        connection.Provider.Should().Be(ConnectionProvider.Filesystem);
        connection.ConfigJson.Should().Contain("/data");
        scopeJson.Should().Contain("*.md").And.Contain("*.tmp");
    }

    [Fact]
    public void Map_NullConfig_DoesNotThrow()
    {
        var (connection, scopeJson) = _mapper.Map(ConnectorType.S3, null, "bare");

        connection.Provider.Should().Be(ConnectionProvider.S3);
        scopeJson.Should().NotBeNull();
    }

    [Fact]
    public void Map_ManagedStorage_Throws()
    {
        // Managed storage is never migrated — it is Connapse's own backend.
        Action act = () => _mapper.Map(ConnectorType.ManagedStorage, null, "managed");

        act.Should().Throw<ArgumentException>();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~ConnectorConfigMapperTests"`
Expected: FAIL — `ConnectorConfigMapper` and `IConnectorConfigMapper` do not exist.

- [ ] **Step 3: Write the models and interface**

Create `src/Connapse.Core/Models/BackfillModels.cs`:

```csharp
namespace Connapse.Core;

/// <summary>
/// The credential-and-endpoint identity of a connection, extracted from a legacy
/// container's connector config. DedupKey is what decides whether two migrated
/// containers share one connection: it covers the credential and endpoint only,
/// never the scope (bucket, blob container, subpath).
/// </summary>
public record ConnectionIdentity(
    ConnectionProvider Provider,
    string DedupKey,
    string Name,
    string? ConfigJson);

public record BackfillPlanItem(
    Guid ContainerId,
    string ContainerName,
    ConnectorType ConnectorType,
    ConnectionIdentity Connection,
    string ScopeJson);

public record BackfillReport(
    int ContainersMigrated,
    int ConnectionsCreated,
    int DocumentsRepointed,
    int FoldersDeleted,
    IReadOnlyList<string> Failures);
```

Create `src/Connapse.Core/Interfaces/IConnectorConfigMapper.cs`:

```csharp
namespace Connapse.Core.Interfaces;

public interface IConnectorConfigMapper
{
    /// <summary>
    /// Splits a legacy containers.connector_config blob into the connection identity
    /// (credential + endpoint, shared across containers) and the source scope (the
    /// specific bucket/prefix/subpath this container pointed at).
    /// Throws ArgumentException for ManagedStorage, which is never migrated.
    /// </summary>
    (ConnectionIdentity Connection, string ScopeJson) Map(
        ConnectorType type, string? connectorConfig, string containerName);
}
```

- [ ] **Step 4: Write the mapper**

Create `src/Connapse.Storage/Backfill/ConnectorConfigMapper.cs`:

```csharp
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.Backfill;

public class ConnectorConfigMapper : IConnectorConfigMapper
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public (ConnectionIdentity Connection, string ScopeJson) Map(
        ConnectorType type, string? connectorConfig, string containerName)
    {
        if (type == ConnectorType.ManagedStorage)
            throw new ArgumentException("Managed storage containers are never migrated to sources.", nameof(type));

        using var doc = string.IsNullOrWhiteSpace(connectorConfig)
            ? JsonDocument.Parse("{}")
            : JsonDocument.Parse(connectorConfig);
        var root = doc.RootElement;

        return type switch
        {
            ConnectorType.S3 => MapS3(root, containerName),
            ConnectorType.AzureBlob => MapAzure(root, containerName),
            ConnectorType.Filesystem => MapFilesystem(root, containerName),
            _ => throw new ArgumentException($"Unsupported connector type '{type}'.", nameof(type))
        };
    }

    private static (ConnectionIdentity, string) MapS3(JsonElement root, string containerName)
    {
        string region = Str(root, "region") ?? "us-east-1";
        string? roleArn = Str(root, "roleArn");
        string bucket = Str(root, "bucketName") ?? "";
        string? prefix = Str(root, "prefix");

        string dedupKey = $"s3|{region}|{roleArn ?? "-"}";
        string configJson = JsonSerializer.Serialize(new { region, roleArn }, Options);
        string scopeJson = JsonSerializer.Serialize(new { bucketName = bucket, prefix }, Options);

        return (new ConnectionIdentity(ConnectionProvider.S3, dedupKey, NameFor("s3", region, containerName), configJson), scopeJson);
    }

    private static (ConnectionIdentity, string) MapAzure(JsonElement root, string containerName)
    {
        string account = Str(root, "storageAccountName") ?? "";
        string? clientId = Str(root, "managedIdentityClientId");
        string blobContainer = Str(root, "containerName") ?? "";
        string? prefix = Str(root, "prefix");

        string dedupKey = $"azure|{account}|{clientId ?? "-"}";
        string configJson = JsonSerializer.Serialize(new { storageAccountName = account, managedIdentityClientId = clientId }, Options);
        string scopeJson = JsonSerializer.Serialize(new { containerName = blobContainer, prefix }, Options);

        return (new ConnectionIdentity(ConnectionProvider.AzureBlob, dedupKey, NameFor("azure", account, containerName), configJson), scopeJson);
    }

    private static (ConnectionIdentity, string) MapFilesystem(JsonElement root, string containerName)
    {
        string rootPath = Str(root, "rootPath") ?? "";
        string[] include = Arr(root, "includePatterns");
        string[] exclude = Arr(root, "excludePatterns");

        string dedupKey = $"fs|{rootPath}";
        string configJson = JsonSerializer.Serialize(new { allowedRoot = rootPath }, Options);
        string scopeJson = JsonSerializer.Serialize(new { subPath = "", includePatterns = include, excludePatterns = exclude }, Options);

        return (new ConnectionIdentity(ConnectionProvider.Filesystem, dedupKey, NameFor("fs", rootPath, containerName), configJson), scopeJson);
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string[] Arr(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
            : [];

    /// <summary>
    /// Derives a deterministic connection name from the provider and endpoint. This is
    /// load-bearing for deduplication: two containers sharing a credential produce the
    /// same name, and the unique index on connections.name makes "find by name" the
    /// dedup mechanism. Do not compare serialized config JSON instead — Postgres jsonb
    /// normalizes property order and whitespace on storage, so a round-tripped blob will
    /// not match what JsonSerializer produced and every container would get its own
    /// connection.
    /// </summary>
    private static string NameFor(string provider, string endpoint, string containerName)
    {
        string slug = new string((endpoint.Length > 0 ? endpoint : containerName)
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray()).Trim('-');

        if (slug.Length == 0) slug = "default";
        string name = $"{provider}-{slug}";
        return name.Length <= 128 ? name : name[..128];
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~ConnectorConfigMapperTests"`
Expected: PASS, 7 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Core/Models/BackfillModels.cs src/Connapse.Core/Interfaces/IConnectorConfigMapper.cs src/Connapse.Storage/Backfill/ConnectorConfigMapper.cs tests/Connapse.Core.Tests/Backfill/
git commit -m "feat(storage): map legacy connector config to connection identity and source scope

Part of #350"
```

---

### Task 2: The backfill service

**Files:**
- Create: `src/Connapse.Storage/Backfill/SourceBackfillService.cs`
- Modify: `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs`
- Test: `tests/Connapse.Integration.Tests/SourceBackfillIntegrationTests.cs`

**Interfaces:**
- Consumes: `IConnectorConfigMapper.Map(...)` from Task 1; `BackfillReport` from Task 1.
- Produces: `SourceBackfillService.RunAsync(CancellationToken)` returning `Task<BackfillReport>`.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Integration.Tests/SourceBackfillIntegrationTests.cs`:

```csharp
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Backfill;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SourceBackfillIntegrationTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string p) => $"{p}-{Guid.NewGuid():N}"[..20];

    private static async Task<Guid> SeedLegacyContainerAsync(
        KnowledgeDbContext ctx, int connectorType, string config, int documentCount)
    {
        var id = Guid.NewGuid();
        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO containers (id, name, connector_type, connector_config, created_at, updated_at) VALUES ({0}, {1}, {2}, CAST({3} AS jsonb), now(), now())",
            id, ShortName("legacy"), connectorType, config);

        for (int i = 0; i < documentCount; i++)
        {
            await ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, created_at) VALUES (gen_random_uuid(), {0}, NULL, {1}, {2}, '', 1, now())",
                id, $"f{i}.md", $"/f{i}.md");
        }

        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO folders (id, container_id, path, created_at) VALUES (gen_random_uuid(), {0}, '/sub', now())", id);

        return id;
    }

    [Fact]
    public async Task RunAsync_S3Container_BecomesSourceWithSameId()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();
        await using var ctx = await factory.CreateDbContextAsync();

        Guid containerId = await SeedLegacyContainerAsync(
            ctx, (int)ConnectorType.S3, """{"bucketName":"b","region":"us-east-1"}""", documentCount: 2);

        await backfill.RunAsync(CancellationToken.None);

        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var source = await sources.GetAsync(containerId);

        // ID preservation is the whole safety argument: owner_id is unchanged, so
        // chunks and vectors need no rewrite.
        source.Should().NotBeNull();
        source!.DocumentCount.Should().Be(2);
        (await ctx.Containers.AnyAsync(c => c.Id == containerId)).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_RepointsDocumentsAndLeavesOwnerIdUnchanged()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();
        await using var ctx = await factory.CreateDbContextAsync();

        Guid containerId = await SeedLegacyContainerAsync(
            ctx, (int)ConnectorType.AzureBlob, """{"storageAccountName":"a","containerName":"c"}""", documentCount: 3);

        await backfill.RunAsync(CancellationToken.None);

        await using var fresh = await factory.CreateDbContextAsync();
        var docs = await fresh.Documents.AsNoTracking().Where(d => d.SourceId == containerId).ToListAsync();

        docs.Should().HaveCount(3);
        docs.Should().OnlyContain(d => d.ContainerId == null);
        docs.Should().OnlyContain(d => d.OwnerId == containerId);
    }

    [Fact]
    public async Task RunAsync_DeletesFolderRowsForMigratedContainers()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();
        await using var ctx = await factory.CreateDbContextAsync();

        Guid containerId = await SeedLegacyContainerAsync(
            ctx, (int)ConnectorType.Filesystem, """{"rootPath":"/data"}""", documentCount: 1);

        await backfill.RunAsync(CancellationToken.None);

        await using var fresh = await factory.CreateDbContextAsync();
        (await fresh.Folders.AnyAsync(f => f.ContainerId == containerId)).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_TwoContainersSameCredential_ShareOneConnection()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        await using var ctx = await factory.CreateDbContextAsync();

        Guid a = await SeedLegacyContainerAsync(ctx, (int)ConnectorType.S3, """{"bucketName":"one","region":"eu-north-1"}""", 1);
        Guid b = await SeedLegacyContainerAsync(ctx, (int)ConnectorType.S3, """{"bucketName":"two","region":"eu-north-1"}""", 1);

        await backfill.RunAsync(CancellationToken.None);

        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var sourceA = await sources.GetAsync(a);
        var sourceB = await sources.GetAsync(b);

        sourceA!.ConnectionId.Should().Be(sourceB!.ConnectionId);
        (await connections.GetAsync(sourceA.ConnectionId))!.SourceCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task RunAsync_LeavesManagedContainersAlone()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();
        await using var ctx = await factory.CreateDbContextAsync();

        var managedId = Guid.NewGuid();
        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO containers (id, name, connector_type, created_at, updated_at) VALUES ({0}, {1}, 0, now(), now())",
            managedId, ShortName("managed"));

        await backfill.RunAsync(CancellationToken.None);

        await using var fresh = await factory.CreateDbContextAsync();
        (await fresh.Containers.AnyAsync(c => c.Id == managedId)).Should().BeTrue();
        (await fresh.Sources.AnyAsync(s => s.Id == managedId)).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_RunTwice_IsIdempotent()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();
        await using var ctx = await factory.CreateDbContextAsync();

        Guid containerId = await SeedLegacyContainerAsync(
            ctx, (int)ConnectorType.S3, """{"bucketName":"idem","region":"us-west-2"}""", documentCount: 2);

        await backfill.RunAsync(CancellationToken.None);
        var second = await backfill.RunAsync(CancellationToken.None);

        // The second pass finds nothing left to migrate.
        second.ContainersMigrated.Should().Be(0);

        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        (await sources.GetAsync(containerId))!.DocumentCount.Should().Be(2);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~SourceBackfillIntegrationTests"`
Expected: FAIL — `SourceBackfillService` does not exist.

- [ ] **Step 3: Write the backfill service**

Create `src/Connapse.Storage/Backfill/SourceBackfillService.cs`:

```csharp
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Storage.Backfill;

/// <summary>
/// Migrates legacy non-managed containers into connection + source pairs.
/// Each source reuses its container's GUID, so documents.owner_id — a generated
/// COALESCE(container_id, source_id) — is unchanged by the move. That is why no
/// chunk or vector rows are touched and search results cannot shift.
/// Transactional per container and safe to run repeatedly.
/// </summary>
public class SourceBackfillService(
    IDbContextFactory<KnowledgeDbContext> factory,
    IConnectorConfigMapper mapper,
    ILogger<SourceBackfillService> logger)
{
    public async Task<BackfillReport> RunAsync(CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var legacy = await context.Containers
            .AsNoTracking()
            .Where(c => c.ConnectorType != (int)ConnectorType.ManagedStorage)
            .Select(c => new { c.Id, c.Name, c.ConnectorType, c.ConnectorConfig })
            .ToListAsync(ct);

        if (legacy.Count == 0)
            return new BackfillReport(0, 0, 0, 0, []);

        logger.LogInformation("Backfill starting: {Count} legacy container(s) to migrate", legacy.Count);

        int migrated = 0, connectionsCreated = 0, documentsRepointed = 0, foldersDeleted = 0;
        var failures = new List<string>();

        foreach (var row in legacy)
        {
            try
            {
                var (connection, scopeJson) = mapper.Map(
                    (ConnectorType)row.ConnectorType,
                    row.ConnectorConfig?.RootElement.GetRawText(),
                    row.Name);

                // Fresh context per container so one failure cannot poison the next.
                await using var perItem = await factory.CreateDbContextAsync(ct);
                await using var tx = await perItem.Database.BeginTransactionAsync(ct);

                var (connectionId, wasCreated) = await EnsureConnectionAsync(perItem, connection, ct);
                if (wasCreated) connectionsCreated++;

                perItem.Sources.Add(new SourceEntity
                {
                    // Same GUID as the container it replaces — this is load-bearing.
                    Id = row.Id,
                    Name = row.Name,
                    ConnectionId = connectionId,
                    ScopeJson = JsonDocument.Parse(scopeJson),
                    Enabled = true,
                    LastSyncStatus = (int)SyncStatus.Never,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
                await perItem.SaveChangesAsync(ct);

                // Repoint documents. owner_id is generated, so it recomputes to the same value.
                documentsRepointed += await perItem.Documents
                    .Where(d => d.ContainerId == row.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.SourceId, row.Id)
                        .SetProperty(d => d.ContainerId, (Guid?)null), ct);

                foldersDeleted += await perItem.Folders
                    .Where(f => f.ContainerId == row.Id)
                    .ExecuteDeleteAsync(ct);

                await perItem.Containers.Where(c => c.Id == row.Id).ExecuteDeleteAsync(ct);

                await tx.CommitAsync(ct);
                migrated++;

                logger.LogInformation("Migrated container {ContainerId} ({Name}) to a source", row.Id, Sanitize(row.Name));
            }
            catch (Exception ex)
            {
                // Record and continue: one malformed config must not block the rest.
                logger.LogError(ex, "Failed to migrate container {ContainerId}", row.Id);
                failures.Add($"{row.Id}: {ex.Message}");
            }
        }

        logger.LogInformation(
            "Backfill complete: {Migrated} migrated, {Connections} connection(s) created, {Docs} document(s) repointed, {Failures} failure(s)",
            migrated, connectionsCreated, documentsRepointed, failures.Count);

        return new BackfillReport(migrated, connectionsCreated, documentsRepointed, foldersDeleted, failures);
    }

    private static async Task<(Guid Id, bool WasCreated)> EnsureConnectionAsync(
        KnowledgeDbContext context, ConnectionIdentity identity, CancellationToken ct)
    {
        // Dedup by the deterministic name, which encodes the credential identity and is
        // backed by the unique index on connections.name. Comparing serialized ConfigJson
        // would be wrong: Postgres jsonb normalizes property order and whitespace, so the
        // stored text does not round-trip to what JsonSerializer emitted.
        Guid existingId = await context.Connections
            .Where(c => c.Name == identity.Name)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(ct);

        if (existingId != Guid.Empty) return (existingId, false);

        var entity = new ConnectionEntity
        {
            Id = Guid.NewGuid(),
            Name = identity.Name,
            Provider = (int)identity.Provider,
            ConfigJson = identity.ConfigJson is null ? null : JsonDocument.Parse(identity.ConfigJson),
            // Backfilled connections never carry a secret: S3 uses DefaultAWSCredentials or an
            // assumed role, Azure uses managed identity, Filesystem needs none.
            SecretProtected = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Connections.Add(entity);
        await context.SaveChangesAsync(ct);
        return (entity.Id, true);
    }
}
```

- [ ] **Step 4: Register the service**

In `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs`, add `using Connapse.Storage.Backfill;` and register beside the other stores:

```csharp
        services.AddSingleton<IConnectorConfigMapper, ConnectorConfigMapper>();
        services.AddScoped<SourceBackfillService>();
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~SourceBackfillIntegrationTests"`
Expected: PASS, 6 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Storage/Backfill/SourceBackfillService.cs src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs tests/Connapse.Integration.Tests/SourceBackfillIntegrationTests.cs
git commit -m "feat(storage): backfill legacy containers into connections and sources

Sources reuse the container GUID so owner_id is unchanged and no chunk or
vector rows are rewritten.

Part of #350"
```

---

### Task 3: Search parity proof

**Files:**
- Test: `tests/Connapse.Integration.Tests/BackfillSearchParityTests.cs`

**Interfaces:**
- Consumes: `SourceBackfillService.RunAsync(...)` from Task 2.
- Produces: nothing — this task is the phase's done-condition.

This is the test that makes an irreversible migration safe to ship. It seeds a container with real chunks and vectors, records search results, runs the backfill, and asserts the results are identical.

- [ ] **Step 1: Write the test**

Create `tests/Connapse.Integration.Tests/BackfillSearchParityTests.cs`:

```csharp
using Connapse.Core;
using Connapse.Storage.Backfill;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class BackfillSearchParityTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task Backfill_DoesNotChangeChunkOrVectorOwnership()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();

        Guid containerId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();
        Guid chunkId = Guid.NewGuid();

        await using (var seed = await factory.CreateDbContextAsync())
        {
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO containers (id, name, connector_type, connector_config, created_at, updated_at) VALUES ({0}, {1}, 3, CAST({2} AS jsonb), now(), now())",
                containerId, $"parity-{containerId:N}"[..20], """{"bucketName":"b","region":"us-east-1"}""");

            seed.Documents.Add(new DocumentEntity
            {
                Id = documentId,
                ContainerId = containerId,
                FileName = "p.md",
                Path = "/p.md",
                ContentHash = string.Empty,
                SizeBytes = 1,
                Status = "Ready",
                CreatedAt = DateTime.UtcNow,
                Metadata = new Dictionary<string, string>(),
            });
            seed.Chunks.Add(new ChunkEntity
            {
                Id = chunkId,
                DocumentId = documentId,
                OwnerId = containerId,
                Content = "parity probe content",
                ChunkIndex = 0,
                TokenCount = 3,
                StartOffset = 0,
                EndOffset = 20,
            });
            seed.ChunkVectors.Add(new ChunkVectorEntity
            {
                ChunkId = chunkId,
                DocumentId = documentId,
                OwnerId = containerId,
                Embedding = new Vector(new float[] { 1f, 0f, 0f }),
                ModelId = "parity-model",
                ContentHash = $"h-{chunkId:N}",
                Dimensions = 3,
            });
            await seed.SaveChangesAsync();
        }

        await backfill.RunAsync(CancellationToken.None);

        await using var after = await factory.CreateDbContextAsync();

        // The owner never moved. Chunks and vectors were not rewritten, so anything
        // filtering on owner_id — every search path — returns exactly what it did before.
        (await after.Chunks.AsNoTracking().Where(c => c.Id == chunkId).Select(c => c.OwnerId).SingleAsync())
            .Should().Be(containerId);
        (await after.ChunkVectors.AsNoTracking().Where(v => v.ChunkId == chunkId).Select(v => v.OwnerId).SingleAsync())
            .Should().Be(containerId);
        (await after.Documents.AsNoTracking().Where(d => d.Id == documentId).Select(d => d.OwnerId).SingleAsync())
            .Should().Be(containerId);

        // And the document is now source-owned rather than container-owned.
        var doc = await after.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId);
        doc.ContainerId.Should().BeNull();
        doc.SourceId.Should().Be(containerId);
    }

    [Fact]
    public async Task Backfill_KeywordSearchReturnsIdenticalHits()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();

        Guid containerId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();

        await using (var seed = await factory.CreateDbContextAsync())
        {
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO containers (id, name, connector_type, connector_config, created_at, updated_at) VALUES ({0}, {1}, 3, CAST({2} AS jsonb), now(), now())",
                containerId, $"kw-{containerId:N}"[..20], """{"bucketName":"b","region":"us-east-1"}""");

            seed.Documents.Add(new DocumentEntity
            {
                Id = documentId,
                ContainerId = containerId,
                FileName = "k.md",
                Path = "/k.md",
                ContentHash = string.Empty,
                SizeBytes = 1,
                Status = "Ready",
                CreatedAt = DateTime.UtcNow,
                Metadata = new Dictionary<string, string>(),
            });
            seed.Chunks.Add(new ChunkEntity
            {
                Id = Guid.NewGuid(),
                DocumentId = documentId,
                OwnerId = containerId,
                Content = "zarquon distinctive keyword",
                ChunkIndex = 0,
                TokenCount = 3,
                StartOffset = 0,
                EndOffset = 27,
            });
            await seed.SaveChangesAsync();
        }

        // The keyword path filters documents by owner_id, so query it directly before
        // and after; going through the HTTP endpoint would need an embedding provider.
        async Task<int> HitCountAsync()
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.Chunks.AsNoTracking()
                .Where(c => c.OwnerId == containerId && c.Content.Contains("zarquon"))
                .CountAsync();
        }

        int before = await HitCountAsync();
        await backfill.RunAsync(CancellationToken.None);
        int afterCount = await HitCountAsync();

        before.Should().Be(1);
        afterCount.Should().Be(before);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~BackfillSearchParityTests"`
Expected: PASS, 2 tests.

- [ ] **Step 3: Commit**

```bash
git add tests/Connapse.Integration.Tests/BackfillSearchParityTests.cs
git commit -m "test(storage): prove backfill leaves chunk and vector ownership untouched

Part of #350"
```

---

### Task 4: Compatibility read on container endpoints

**Files:**
- Modify: `src/Connapse.Web/Endpoints/ContainersEndpoints.cs` (the four `containerStore.GetAsync` sites at lines 107, 139, 174, 329)
- Test: append to `tests/Connapse.Integration.Tests/SourceBackfillIntegrationTests.cs`

**Interfaces:**
- Consumes: `ISourceStore.GetAsync(Guid, CancellationToken)` from Phase 1.
- Produces: container endpoints that resolve a migrated ID instead of returning 404.

- [ ] **Step 1: Write the failing test**

Append to `tests/Connapse.Integration.Tests/SourceBackfillIntegrationTests.cs`:

```csharp
    [Fact]
    public async Task GetContainer_AfterMigration_StillResolvesByOldId()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();
        await using var ctx = await factory.CreateDbContextAsync();

        Guid containerId = await SeedLegacyContainerAsync(
            ctx, (int)ConnectorType.S3, """{"bucketName":"compat","region":"us-east-1"}""", documentCount: 1);

        await backfill.RunAsync(CancellationToken.None);

        // The container row is gone, but agent prompts, CLI scripts, and bookmarks still
        // carry this ID. The compatibility read must keep them working.
        var response = await fixture.AdminClient.GetAsync($"/api/containers/{containerId}");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"kind\"");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~GetContainer_AfterMigration"`
Expected: FAIL with 404 — the container row no longer exists and nothing falls back to sources.

- [ ] **Step 3: Add the compatibility read**

In `src/Connapse.Web/Endpoints/ContainersEndpoints.cs`, add a shared local helper near the top of the class and use it at each of the four `containerStore.GetAsync(containerId, ct)` sites:

```csharp
    /// <summary>
    /// Resolves an owner ID to a container, falling back to a source. Phase 2 migrated
    /// external containers into sources that reuse the container's GUID, so IDs held by
    /// agents, CLI scripts, and bookmarks must keep resolving. Returns null when neither
    /// exists. Removed in Phase 5 (#353) once callers have moved to /api/sources.
    /// </summary>
    private static async Task<(Container? Container, Source? Source)> ResolveOwnerAsync(
        IContainerStore containerStore, ISourceStore sourceStore, Guid id, CancellationToken ct)
    {
        var container = await containerStore.GetAsync(id, ct);
        if (container is not null) return (container, null);

        var source = await sourceStore.GetAsync(id, ct);
        return (null, source);
    }
```

At each call site, replace:

```csharp
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null) return Results.NotFound();
```

with:

```csharp
            var (container, migratedSource) = await ResolveOwnerAsync(containerStore, sourceStore, containerId, ct);
            if (container is null && migratedSource is null) return Results.NotFound();
```

Then in the response projection, emit a `kind` discriminator so callers can tell the two apart:

```csharp
            return Results.Ok(container is not null
                ? new { id = container.Id, name = container.Name, description = container.Description, kind = "container", documentCount = container.DocumentCount }
                : new { id = migratedSource!.Id, name = migratedSource.Name, description = migratedSource.Description, kind = "source", documentCount = migratedSource.DocumentCount });
```

Add `ISourceStore sourceStore` to each affected endpoint's parameter list so DI injects it.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~SourceBackfillIntegrationTests"`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Web/Endpoints/ContainersEndpoints.cs tests/Connapse.Integration.Tests/SourceBackfillIntegrationTests.cs
git commit -m "feat(api): resolve migrated container IDs against sources

Part of #350"
```

---

### Task 5: Run the backfill at startup

**Files:**
- Create: `src/Connapse.Web/Services/SourceBackfillHostedService.cs`
- Modify: `src/Connapse.Web/Program.cs`

**Interfaces:**
- Consumes: `SourceBackfillService.RunAsync(CancellationToken)` from Task 2.
- Produces: the migration running automatically once per deployment.

- [ ] **Step 1: Write the hosted service**

Create `src/Connapse.Web/Services/SourceBackfillHostedService.cs`:

```csharp
using Connapse.Storage.Backfill;

namespace Connapse.Web.Services;

/// <summary>
/// Runs the container-to-source backfill once at startup, after EF migrations have
/// applied. Idempotent: a second run finds nothing to migrate and returns immediately,
/// so restarts and multiple replicas are safe.
/// </summary>
public class SourceBackfillHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<SourceBackfillHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();
            var report = await backfill.RunAsync(ct);

            if (report.ContainersMigrated > 0)
            {
                logger.LogInformation(
                    "Container-to-source backfill migrated {Count} container(s), repointing {Docs} document(s)",
                    report.ContainersMigrated, report.DocumentsRepointed);
            }

            foreach (var failure in report.Failures)
                logger.LogError("Backfill failure: {Failure}", failure);
        }
        catch (Exception ex)
        {
            // Never block startup on the backfill: the compatibility read means an
            // un-migrated install still serves every request correctly.
            logger.LogError(ex, "Container-to-source backfill failed; the application will start anyway");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

- [ ] **Step 2: Register it**

In `src/Connapse.Web/Program.cs`, after the existing migration-on-startup code, add:

```csharp
builder.Services.AddHostedService<SourceBackfillHostedService>();
```

- [ ] **Step 3: Verify the whole suite**

Run: `dotnet test`
Expected: all tests pass. The backfill now runs against the integration fixture's database at startup, which also proves it is harmless when there is nothing to migrate.

- [ ] **Step 4: Commit**

```bash
git add src/Connapse.Web/Services/SourceBackfillHostedService.cs src/Connapse.Web/Program.cs
git commit -m "feat(web): run the container-to-source backfill at startup

Part of #350"
```

---

## Phase 2 done-condition

An install seeded with pre-existing S3, Azure Blob, and Filesystem containers returns identical search results before and after the backfill; migrated container IDs still resolve through the container endpoints with a `kind` discriminator; folder rows for migrated containers are gone; managed containers are untouched; running the backfill twice migrates nothing the second time; and `dotnet test` passes in full.

## Manual verification before merging

The automated tests use a fresh Testcontainers database. Also run once against a database with real pre-existing data, the way Phase 1's migration was verified:

```bash
docker run -d --name connapse-backfilltest -e POSTGRES_PASSWORD=t -e POSTGRES_DB=t -p 55499:5432 pgvector/pgvector:pg17
dotnet ef database update --project src/Connapse.Storage --startup-project src/Connapse.Web --connection "Host=localhost;Port=55499;Database=t;Username=postgres;Password=t"
```

Seed a container of each connector type with documents, chunks, and vectors, start the app against that connection string, and confirm the log line reports the expected migration count with zero failures.
