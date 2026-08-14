# Connector/Source Split — Phase 1 (Schema and Stores) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the `connections` and `sources` tables, bridge `documents` to both owner kinds, and expose `IConnectionStore` / `ISourceStore` — with nothing in the application reading them yet.

**Architecture:** A `Connection` holds an admin-registered credential for an external provider; a `Source` is a read-only scope inside that connection. `documents` gains a nullable `source_id` beside `container_id` with a CHECK that exactly one is set, plus a stored generated `owner_id` column so the search path never classifies its owner. `chunks` and `chunk_vectors` rename their denormalized `container_id` to `owner_id` and drop the FK, which is a catalog-only change that avoids rewriting the largest table in the system.

**Tech Stack:** .NET 10, EF Core (Npgsql), PostgreSQL with pgvector, ASP.NET Core DataProtection, xUnit, FluentAssertions, NSubstitute, Testcontainers.

## Global Constraints

- Target framework .NET 10; file-scoped namespaces; nullable enabled; implicit usings.
- `Connapse.Core` has zero external dependencies — models and interfaces only, no EF, no DataProtection.
- Records for DTOs and settings models; primary constructors for DI.
- Async all the way — never `.Result` or `.Wait()`.
- Never use `var` for primitive types. Never use `dynamic`.
- Parameterized SQL only — never string interpolation into SQL.
- Wrap user-controlled values in `LogSanitizer.Sanitize(...)` from `Connapse.Core.Utilities` when logging.
- Always use `IDbContextFactory<KnowledgeDbContext>` and create short-lived contexts: `await using var context = await factory.CreateDbContextAsync(ct);`
- Test naming: `MethodName_Scenario_ExpectedResult`. Tag with `[Trait("Category", "Unit")]` or `[Trait("Category", "Integration")]`.
- Commit message format: `<type>: <summary>` where type is one of `feat|fix|docs|test|refactor|perf|chore`.
- Milestone: v0.4.0. Branch: `feature/<issue>-connector-source-split-phase-1`.

## Scope

This plan covers **Phase 1 only** from `docs/superpowers/specs/2026-08-13-connector-source-split-design.md`. Phases 2–5 (backfill, sync engine, UI, cleanup) each produce independently shippable software and get their own plans, written once their predecessor lands — the Phase 2 backfill tests depend on the store API this phase produces.

**Explicitly out of scope here:** any endpoint, any UI, any change to `ConnectorWatcherService`, deleting `ContainerWriteGuard`, and the backfill of existing containers. Nothing in the running application reads `connections` or `sources` when this phase is done.

## File Structure

**Create:**
- `src/Connapse.Core/Models/ConnectionModels.cs` — `Connection`, `CreateConnectionRequest`, `UpdateConnectionRequest` records and the `ConnectionProvider` enum.
- `src/Connapse.Core/Models/SourceModels.cs` — `Source`, `CreateSourceRequest`, `UpdateSourceRequest` records and the `SyncStatus` enum.
- `src/Connapse.Core/Interfaces/IConnectionStore.cs`
- `src/Connapse.Core/Interfaces/ISourceStore.cs`
- `src/Connapse.Storage/Data/Entities/ConnectionEntity.cs`
- `src/Connapse.Storage/Data/Entities/SourceEntity.cs`
- `src/Connapse.Storage/Connections/PostgresConnectionStore.cs`
- `src/Connapse.Storage/Sources/PostgresSourceStore.cs`
- `tests/Connapse.Core.Tests/Sources/SourceModelTests.cs`
- `tests/Connapse.Integration.Tests/ConnectionStoreIntegrationTests.cs`
- `tests/Connapse.Integration.Tests/SourceStoreIntegrationTests.cs`
- `tests/Connapse.Integration.Tests/OwnerBridgeSchemaTests.cs`

**Modify:**
- `src/Connapse.Storage/Data/KnowledgeDbContext.cs` — two new `DbSet`s and their `OnModelCreating` configuration; `documents` CHECK constraint and computed column; `chunks`/`chunk_vectors` property rename.
- `src/Connapse.Storage/Data/Entities/DocumentEntity.cs` — nullable `ContainerId`, new `SourceId`, new read-only `OwnerId`.
- `src/Connapse.Storage/Data/Entities/ChunkEntity.cs` — `ContainerId` → `OwnerId`.
- `src/Connapse.Storage/Data/Entities/ChunkVectorEntity.cs` — `ContainerId` → `OwnerId`.
- `src/Connapse.Storage/ServiceCollectionExtensions.cs` — register the two stores.

---

### Task 1: Core models for Connection and Source

**Files:**
- Create: `src/Connapse.Core/Models/ConnectionModels.cs`
- Create: `src/Connapse.Core/Models/SourceModels.cs`
- Test: `tests/Connapse.Core.Tests/Sources/SourceModelTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ConnectionProvider` enum (`S3 = 3`, `AzureBlob = 4`, `Filesystem = 1` — values deliberately match the existing `ConnectorType` enum so a backfill in Phase 2 is a straight cast); `Connection` record; `CreateConnectionRequest(string Name, ConnectionProvider Provider, string? ConfigJson, string? Secret)`; `UpdateConnectionRequest(string? Name, string? ConfigJson, string? Secret)`; `SyncStatus` enum (`Never = 0`, `Running = 1`, `Succeeded = 2`, `Failed = 3`); `Source` record; `CreateSourceRequest(string Name, Guid ConnectionId, string ScopeJson, string? Description = null, int? SyncIntervalSeconds = null)` — note the required parameters come first, so positional construction is `new CreateSourceRequest(name, connectionId, scopeJson)`; `UpdateSourceRequest(string? Name = null, string? Description = null, string? ScopeJson = null, int? SyncIntervalSeconds = null, bool? Enabled = null)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Core.Tests/Sources/SourceModelTests.cs`:

```csharp
using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Sources;

[Trait("Category", "Unit")]
public class SourceModelTests
{
    [Fact]
    public void ConnectionProvider_Values_MatchConnectorTypeForBackfill()
    {
        // Phase 2 backfills existing containers by casting ConnectorType to
        // ConnectionProvider. The numeric values must line up or the cast silently
        // mislabels every migrated connection.
        ((int)ConnectionProvider.Filesystem).Should().Be((int)ConnectorType.Filesystem);
        ((int)ConnectionProvider.S3).Should().Be((int)ConnectorType.S3);
        ((int)ConnectionProvider.AzureBlob).Should().Be((int)ConnectorType.AzureBlob);
    }

    [Fact]
    public void ConnectionProvider_DoesNotContainManagedStorage()
    {
        // Managed storage is Connapse's own backend, never an external system
        // it authenticates to, so it must not be expressible as a connection.
        Enum.GetNames<ConnectionProvider>().Should().NotContain("ManagedStorage");
    }

    [Fact]
    public void Source_NewInstance_DefaultsToNeverSynced()
    {
        var source = new Source(
            Id: Guid.NewGuid(),
            Name: "docs-bucket",
            Description: null,
            ConnectionId: Guid.NewGuid(),
            ScopeJson: """{"prefix":"docs/"}""",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);

        source.LastSyncStatus.Should().Be(SyncStatus.Never);
        source.SyncCursor.Should().BeNull();
        source.LastSyncedAt.Should().BeNull();
        source.Enabled.Should().BeTrue();
        source.DocumentCount.Should().Be(0);
    }

    [Fact]
    public void Connection_NeverExposesSecret()
    {
        // The Connection read model is returned to callers; the encrypted secret
        // must not be a property on it at all.
        typeof(Connection).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~SourceModelTests"`
Expected: FAIL — compilation errors, `ConnectionProvider` / `Source` / `Connection` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/Connapse.Core/Models/ConnectionModels.cs`:

```csharp
namespace Connapse.Core;

/// <summary>
/// External provider a Connection authenticates to. Values match ConnectorType
/// so Phase 2 can backfill existing containers with a direct cast.
/// ManagedStorage is deliberately absent: it is Connapse's own backend, not an
/// external system requiring credentials.
/// </summary>
public enum ConnectionProvider { Filesystem = 1, S3 = 3, AzureBlob = 4 }

public record Connection(
    Guid Id,
    string Name,
    ConnectionProvider Provider,
    string? ConfigJson,
    Guid? CreatedByUserId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool HasSecret = false,
    int SourceCount = 0);

public record CreateConnectionRequest(
    string Name,
    ConnectionProvider Provider,
    string? ConfigJson = null,
    string? Secret = null);

public record UpdateConnectionRequest(
    string? Name = null,
    string? ConfigJson = null,
    string? Secret = null);
```

Create `src/Connapse.Core/Models/SourceModels.cs`:

```csharp
namespace Connapse.Core;

public enum SyncStatus { Never = 0, Running = 1, Succeeded = 2, Failed = 3 }

public record Source(
    Guid Id,
    string Name,
    string? Description,
    Guid ConnectionId,
    string ScopeJson,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool Enabled = true,
    string? SyncCursor = null,
    DateTime? LastSyncedAt = null,
    SyncStatus LastSyncStatus = SyncStatus.Never,
    string? LastSyncError = null,
    int? SyncIntervalSeconds = null,
    ContainerSettingsOverrides? SettingsOverrides = null,
    string? Summary = null,
    DateTime? SummaryGeneratedAt = null,
    string? SummaryDocSetHash = null,
    int DocumentCount = 0);

public record CreateSourceRequest(
    string Name,
    Guid ConnectionId,
    string ScopeJson,
    string? Description = null,
    int? SyncIntervalSeconds = null);

public record UpdateSourceRequest(
    string? Name = null,
    string? Description = null,
    string? ScopeJson = null,
    int? SyncIntervalSeconds = null,
    bool? Enabled = null);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~SourceModelTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Models/ConnectionModels.cs src/Connapse.Core/Models/SourceModels.cs tests/Connapse.Core.Tests/Sources/SourceModelTests.cs
git commit -m "feat(core): add Connection and Source domain models"
```

---

### Task 2: Store interfaces

**Files:**
- Create: `src/Connapse.Core/Interfaces/IConnectionStore.cs`
- Create: `src/Connapse.Core/Interfaces/ISourceStore.cs`

**Interfaces:**
- Consumes: `Connection`, `CreateConnectionRequest`, `UpdateConnectionRequest`, `Source`, `CreateSourceRequest`, `UpdateSourceRequest`, `SyncStatus` from Task 1.
- Produces: `IConnectionStore` and `ISourceStore`, implemented in Tasks 6 and 7 and registered in Task 8.

There is no test step here — these are pure interface declarations with no behaviour. Their contract is exercised by the integration tests in Tasks 6 and 7.

- [ ] **Step 1: Write the interfaces**

Create `src/Connapse.Core/Interfaces/IConnectionStore.cs`:

```csharp
namespace Connapse.Core.Interfaces;

public interface IConnectionStore
{
    Task<Connection> CreateAsync(CreateConnectionRequest request, Guid? createdByUserId, CancellationToken ct = default);
    Task<Connection?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Connection>> ListAsync(int skip = 0, int take = 50, CancellationToken ct = default);
    Task<Connection?> UpdateAsync(Guid id, UpdateConnectionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Deletes a connection. Throws InvalidOperationException if any source still
    /// references it — sources must be removed or repointed first.
    /// </summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Decrypts and returns the stored secret. Only the sync engine and connection
    /// testers should call this; it is never surfaced through the Connection model.
    /// Returns null when the connection has no secret (for example Filesystem).
    /// Throws System.Security.Cryptography.CryptographicException if the stored
    /// ciphertext cannot be unprotected, which happens after DataProtection key loss.
    /// </summary>
    Task<string?> GetSecretAsync(Guid id, CancellationToken ct = default);
}
```

Create `src/Connapse.Core/Interfaces/ISourceStore.cs`:

```csharp
namespace Connapse.Core.Interfaces;

public interface ISourceStore
{
    Task<Source> CreateAsync(CreateSourceRequest request, CancellationToken ct = default);
    Task<Source?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Source?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Source>> ListAsync(int skip = 0, int take = 50, CancellationToken ct = default);
    Task<IReadOnlyList<Source>> ListByConnectionAsync(Guid connectionId, CancellationToken ct = default);
    Task<Source?> UpdateAsync(Guid id, UpdateSourceRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Persists the sync cursor and outcome after a sync cycle. Passing a null
    /// cursor clears it, which is what a RequiresFullResync response demands.
    /// </summary>
    Task UpdateSyncStateAsync(Guid id, string? cursor, SyncStatus status, string? error, DateTime? syncedAt, CancellationToken ct = default);

    Task<ContainerSettingsOverrides?> GetSettingsOverridesAsync(Guid id, CancellationToken ct = default);
    Task SaveSettingsOverridesAsync(Guid id, ContainerSettingsOverrides overrides, CancellationToken ct = default);
    Task UpdateSummaryAsync(Guid id, string? summary, DateTime? generatedAt, string? docSetHash, CancellationToken ct = default);
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/Connapse.Core`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Connapse.Core/Interfaces/IConnectionStore.cs src/Connapse.Core/Interfaces/ISourceStore.cs
git commit -m "feat(core): add IConnectionStore and ISourceStore interfaces"
```

---

### Task 3: EF entities for connections and sources

**Files:**
- Create: `src/Connapse.Storage/Data/Entities/ConnectionEntity.cs`
- Create: `src/Connapse.Storage/Data/Entities/SourceEntity.cs`
- Modify: `src/Connapse.Storage/Data/KnowledgeDbContext.cs`

**Interfaces:**
- Consumes: `ConnectionProvider`, `SyncStatus` from Task 1.
- Produces: `ConnectionEntity`, `SourceEntity`, `KnowledgeDbContext.Connections`, `KnowledgeDbContext.Sources` — used by Tasks 4, 6, and 7.

- [ ] **Step 1: Write the entities**

Create `src/Connapse.Storage/Data/Entities/ConnectionEntity.cs`:

```csharp
using System.Text.Json;

namespace Connapse.Storage.Data.Entities;

public class ConnectionEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Provider { get; set; } // maps to ConnectionProvider enum
    public JsonDocument? ConfigJson { get; set; } // JSONB: non-secret provider settings
    public string? SecretProtected { get; set; } // DataProtection ciphertext, purpose "Connection.v1"
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public List<SourceEntity> Sources { get; set; } = [];
}
```

Create `src/Connapse.Storage/Data/Entities/SourceEntity.cs`:

```csharp
using System.Text.Json;

namespace Connapse.Storage.Data.Entities;

public class SourceEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid ConnectionId { get; set; }
    public JsonDocument ScopeJson { get; set; } = null!; // JSONB: bucket prefix, root subpath, space key
    public JsonDocument? SettingsOverridesJson { get; set; }
    public bool Enabled { get; set; } = true;

    // Sync state
    public string? SyncCursor { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public int LastSyncStatus { get; set; } // maps to SyncStatus enum
    public string? LastSyncError { get; set; }
    public int? SyncIntervalSeconds { get; set; } // null inherits the connection default

    // Auto-generated summary (agent-optimized prose for routing)
    public string? Summary { get; set; }
    public DateTime? SummaryGeneratedAt { get; set; }
    public string? SummaryDocSetHash { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public ConnectionEntity Connection { get; set; } = null!;
    public List<DocumentEntity> Documents { get; set; } = [];
}
```

- [ ] **Step 2: Register the DbSets**

In `src/Connapse.Storage/Data/KnowledgeDbContext.cs`, after the existing `BatchDocuments` DbSet on line 15, add:

```csharp
    public DbSet<ConnectionEntity> Connections => Set<ConnectionEntity>();
    public DbSet<SourceEntity> Sources => Set<SourceEntity>();
```

- [ ] **Step 3: Configure the entities**

In the same file, inside `OnModelCreating`, after the existing `BatchDocumentEntity` block, add:

```csharp
        modelBuilder.Entity<ConnectionEntity>(entity =>
        {
            entity.ToTable("connections");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.ConfigJson).HasColumnName("config").HasColumnType("jsonb");
            entity.Property(e => e.SecretProtected).HasColumnName("secret_protected");
            entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");

            entity.HasIndex(e => e.Name).IsUnique().HasDatabaseName("ix_connections_name");
        });

        modelBuilder.Entity<SourceEntity>(entity =>
        {
            entity.ToTable("sources");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.ConnectionId).HasColumnName("connection_id").IsRequired();
            entity.Property(e => e.ScopeJson).HasColumnName("scope").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.SettingsOverridesJson).HasColumnName("settings_overrides").HasColumnType("jsonb");
            entity.Property(e => e.Enabled).HasColumnName("enabled").HasDefaultValue(true);
            entity.Property(e => e.SyncCursor).HasColumnName("sync_cursor");
            entity.Property(e => e.LastSyncedAt).HasColumnName("last_synced_at");
            entity.Property(e => e.LastSyncStatus).HasColumnName("last_sync_status").HasDefaultValue(0);
            entity.Property(e => e.LastSyncError).HasColumnName("last_sync_error");
            entity.Property(e => e.SyncIntervalSeconds).HasColumnName("sync_interval_seconds");
            entity.Property(e => e.Summary).HasColumnName("summary");
            entity.Property(e => e.SummaryGeneratedAt).HasColumnName("summary_generated_at");
            entity.Property(e => e.SummaryDocSetHash).HasColumnName("summary_doc_set_hash");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");

            entity.HasIndex(e => e.Name).IsUnique().HasDatabaseName("ix_sources_name");
            entity.HasIndex(e => e.ConnectionId).HasDatabaseName("ix_sources_connection_id");

            entity.HasOne(e => e.Connection)
                  .WithMany(c => c.Sources)
                  .HasForeignKey(e => e.ConnectionId)
                  .OnDelete(DeleteBehavior.Restrict);
        });
```

`DeleteBehavior.Restrict` is what makes "3 sources use this connection" enforceable at the database level rather than only in application code.

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build src/Connapse.Storage`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/Data/Entities/ConnectionEntity.cs src/Connapse.Storage/Data/Entities/SourceEntity.cs src/Connapse.Storage/Data/KnowledgeDbContext.cs
git commit -m "feat(storage): add connection and source EF entities"
```

---

### Task 4: Bridge documents to both owner kinds

**Files:**
- Modify: `src/Connapse.Storage/Data/Entities/DocumentEntity.cs`
- Modify: `src/Connapse.Storage/Data/Entities/ChunkEntity.cs`
- Modify: `src/Connapse.Storage/Data/Entities/ChunkVectorEntity.cs`
- Modify: `src/Connapse.Storage/Data/KnowledgeDbContext.cs`

**Interfaces:**
- Consumes: `SourceEntity` from Task 3.
- Produces: `DocumentEntity.SourceId`, `DocumentEntity.OwnerId` (read-only, computed), `ChunkEntity.OwnerId`, `ChunkVectorEntity.OwnerId` — relied on by the migration in Task 5 and the schema test in Task 5.

- [ ] **Step 1: Modify DocumentEntity**

In `src/Connapse.Storage/Data/Entities/DocumentEntity.cs`, change `ContainerId` to nullable and add the two new properties beside it:

```csharp
    public Guid? ContainerId { get; set; }
    public Guid? SourceId { get; set; }

    /// <summary>
    /// Stored generated column: COALESCE(container_id, source_id). Never assign to
    /// this — PostgreSQL computes it. Search filters on it so the query path does
    /// not need to know whether the owner is a container or a source.
    /// </summary>
    public Guid OwnerId { get; private set; }
```

Add a `SourceEntity? Source` navigation property alongside the existing `Container` navigation property, and make `Container` nullable:

```csharp
    public ContainerEntity? Container { get; set; }
    public SourceEntity? Source { get; set; }
```

- [ ] **Step 2: Rename the chunk owner columns**

In `src/Connapse.Storage/Data/Entities/ChunkEntity.cs`, replace `public Guid ContainerId { get; set; }` with:

```csharp
    /// <summary>Denormalized owner: the container or source that owns this chunk.</summary>
    public Guid OwnerId { get; set; }
```

Make the identical change in `src/Connapse.Storage/Data/Entities/ChunkVectorEntity.cs`.

- [ ] **Step 3: Update the model configuration**

In `KnowledgeDbContext.OnModelCreating`, inside the `DocumentEntity` block, replace the `ContainerId` property mapping and its indexes with:

```csharp
            entity.Property(e => e.ContainerId).HasColumnName("container_id");
            entity.Property(e => e.SourceId).HasColumnName("source_id");
            entity.Property(e => e.OwnerId)
                  .HasColumnName("owner_id")
                  .HasComputedColumnSql("COALESCE(container_id, source_id)", stored: true);

            entity.ToTable(t => t.HasCheckConstraint(
                "ck_documents_single_owner",
                "(container_id IS NULL) <> (source_id IS NULL)"));

            entity.HasIndex(e => e.OwnerId).HasDatabaseName("ix_documents_owner_id");
            entity.HasIndex(e => new { e.OwnerId, e.Path }).HasDatabaseName("ix_documents_owner_id_path");

            entity.HasOne(e => e.Source)
                  .WithMany(s => s.Documents)
                  .HasForeignKey(e => e.SourceId)
                  .OnDelete(DeleteBehavior.Cascade);
```

The CHECK uses `<>` on two boolean tests, which is XOR — exactly one of the two columns must be non-null. Keep the existing `container_id` index and FK to `containers` unchanged; both owners retain referential integrity.

In the `ChunkEntity` and `ChunkVectorEntity` blocks, replace each `entity.Property(e => e.ContainerId).HasColumnName("container_id");` with:

```csharp
            entity.Property(e => e.OwnerId).HasColumnName("owner_id");
```

and each `entity.HasIndex(e => e.ContainerId)` with `entity.HasIndex(e => e.OwnerId)`.

- [ ] **Step 4: Fix the resulting compilation errors**

Run: `dotnet build`
Expected: FAIL, with errors everywhere `ContainerId` was read from a chunk or assigned on a document. Work through them: chunk writers set `OwnerId` from whatever owner id they already have; document writers set `ContainerId` and leave `SourceId` null, since nothing creates source-owned documents until Phase 2.

Re-run `dotnet build` until it succeeds with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/Data/
git commit -m "feat(storage): bridge documents and chunks to container or source owners"
```

---

### Task 5: EF migration and schema verification

**Files:**
- Create: `src/Connapse.Storage/Migrations/<timestamp>_AddConnectionsAndSources.cs` (generated)
- Test: `tests/Connapse.Integration.Tests/OwnerBridgeSchemaTests.cs`

**Interfaces:**
- Consumes: all entity configuration from Tasks 3 and 4.
- Produces: the applied schema. Tasks 6 and 7 assume it exists.

- [ ] **Step 1: Write the failing schema test**

Create `tests/Connapse.Integration.Tests/OwnerBridgeSchemaTests.cs`:

```csharp
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class OwnerBridgeSchemaTests(SharedWebAppFixture fixture)
{
    private async Task<KnowledgeDbContext> CreateContextAsync()
    {
        var factory = fixture.Factory.Services.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        return await factory.CreateDbContextAsync();
    }

    [Fact]
    public async Task Documents_WithBothOwners_ViolatesCheckConstraint()
    {
        await using var context = await CreateContextAsync();

        Func<Task> act = async () => await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO documents (id, container_id, source_id, file_name, path, size_bytes, created_at, ingestion_state)
            VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'x.md', '/x.md', 1, now(), 0)
            """);

        await act.Should().ThrowAsync<PostgresException>()
            .Where(e => e.SqlState == PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Documents_WithNoOwner_ViolatesCheckConstraint()
    {
        await using var context = await CreateContextAsync();

        Func<Task> act = async () => await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO documents (id, container_id, source_id, file_name, path, size_bytes, created_at, ingestion_state)
            VALUES (gen_random_uuid(), NULL, NULL, 'x.md', '/x.md', 1, now(), 0)
            """);

        await act.Should().ThrowAsync<PostgresException>()
            .Where(e => e.SqlState == PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Documents_OwnerId_MirrorsWhicheverOwnerIsSet()
    {
        await using var context = await CreateContextAsync();
        var containerId = Guid.NewGuid();

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO containers (id, name, connector_type, created_at, updated_at)
            VALUES ({0}, {1}, 0, now(), now())
            """, containerId, $"owner-test-{containerId:N}"[..20]);

        var documentId = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO documents (id, container_id, source_id, file_name, path, size_bytes, created_at, ingestion_state)
            VALUES ({0}, {1}, NULL, 'x.md', '/x.md', 1, now(), 0)
            """, documentId, containerId);

        var ownerId = await context.Documents
            .Where(d => d.Id == documentId)
            .Select(d => d.OwnerId)
            .SingleAsync();

        ownerId.Should().Be(containerId);
    }

    [Fact]
    public async Task Connections_DeleteWithReferencingSource_IsRestricted()
    {
        await using var context = await CreateContextAsync();
        var connectionId = Guid.NewGuid();

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO connections (id, name, provider, created_at, updated_at)
            VALUES ({0}, {1}, 3, now(), now())
            """, connectionId, $"conn-{connectionId:N}"[..20]);

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO sources (id, name, connection_id, scope, enabled, last_sync_status, created_at, updated_at)
            VALUES (gen_random_uuid(), {0}, {1}, '{}'::jsonb, true, 0, now(), now())
            """, $"src-{connectionId:N}"[..20], connectionId);

        Func<Task> act = async () => await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM connections WHERE id = {0}", connectionId);

        await act.Should().ThrowAsync<PostgresException>()
            .Where(e => e.SqlState == PostgresErrorCodes.ForeignKeyViolation);
    }
}
```

The raw SQL above assumes the current `documents` and `containers` column names. Before running,
confirm them against `KnowledgeDbContextModelSnapshot.cs` or with `\d documents` in psql, and
adjust the column lists if they differ — a typo here produces a confusing `column does not exist`
failure that looks like a migration bug.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~OwnerBridgeSchemaTests"`
Expected: FAIL — `relation "connections" does not exist`, and the CHECK tests pass an insert that should have been rejected.

- [ ] **Step 3: Generate the migration**

Run:

```bash
dotnet ef migrations add AddConnectionsAndSources --project src/Connapse.Storage
```

- [ ] **Step 4: Correct the generated migration by hand**

EF generates a drop-and-recreate for the renamed chunk columns, which would destroy every vector in the database. Open the generated migration and replace whatever it produced for `chunks` and `chunk_vectors` with catalog-only renames, and drop the owner FKs by lookup rather than by hardcoded name:

```csharp
            // Catalog-only rename: instant regardless of row count, no table rewrite,
            // and no IVFFlat reindex. Do NOT let EF drop and recreate these columns.
            migrationBuilder.Sql("ALTER TABLE chunks RENAME COLUMN container_id TO owner_id;");
            migrationBuilder.Sql("ALTER TABLE chunk_vectors RENAME COLUMN container_id TO owner_id;");

            // Drop the FKs to containers by looking them up, since the constraint
            // names differ between EF conventions and hand-written migrations.
            migrationBuilder.Sql("""
                DO $$
                DECLARE r RECORD;
                BEGIN
                    FOR r IN
                        SELECT tc.table_name, tc.constraint_name
                        FROM information_schema.table_constraints tc
                        JOIN information_schema.key_column_usage kcu
                          ON tc.constraint_name = kcu.constraint_name
                         AND tc.table_schema = kcu.table_schema
                        WHERE tc.constraint_type = 'FOREIGN KEY'
                          AND tc.table_name IN ('chunks', 'chunk_vectors')
                          AND kcu.column_name = 'owner_id'
                    LOOP
                        EXECUTE format('ALTER TABLE %I DROP CONSTRAINT %I', r.table_name, r.constraint_name);
                    END LOOP;
                END $$;
                """);
```

Verify the rest of the generated migration contains: `CreateTable` for `connections` and `sources`; `AddColumn` for `documents.source_id`; `AlterColumn` making `documents.container_id` nullable; the computed `owner_id` column; and the `ck_documents_single_owner` check constraint. If the computed column or check constraint is missing, add them explicitly:

```csharp
            migrationBuilder.Sql("ALTER TABLE documents ADD COLUMN owner_id uuid GENERATED ALWAYS AS (COALESCE(container_id, source_id)) STORED;");
            migrationBuilder.Sql("ALTER TABLE documents ADD CONSTRAINT ck_documents_single_owner CHECK ((container_id IS NULL) <> (source_id IS NULL));");
```

Write the matching inverse operations into `Down`.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~OwnerBridgeSchemaTests"`
Expected: PASS, 4 tests. Migrations apply on startup, so the fixture picks up the new schema automatically.

- [ ] **Step 6: Verify no existing test regressed**

Run: `dotnet test`
Expected: all tests pass. Pay attention to search and ingestion tests — they exercise the renamed chunk columns.

- [ ] **Step 7: Commit**

```bash
git add src/Connapse.Storage/Migrations/ tests/Connapse.Integration.Tests/OwnerBridgeSchemaTests.cs
git commit -m "feat(storage): migration for connections, sources, and owner bridge"
```

---

### Task 6: PostgresConnectionStore with encrypted secrets

**Files:**
- Create: `src/Connapse.Storage/Connections/PostgresConnectionStore.cs`
- Test: `tests/Connapse.Integration.Tests/ConnectionStoreIntegrationTests.cs`

**Interfaces:**
- Consumes: `IConnectionStore` (Task 2), `ConnectionEntity` (Task 3), the applied schema (Task 5).
- Produces: `PostgresConnectionStore`, registered in Task 8.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Integration.Tests/ConnectionStoreIntegrationTests.cs`:

```csharp
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ConnectionStoreIntegrationTests(SharedWebAppFixture fixture)
{
    private IConnectionStore Store => fixture.Factory.Services.GetRequiredService<IConnectionStore>();

    private static CreateConnectionRequest NewRequest(string? secret = "super-secret-key") =>
        new($"conn-{Guid.NewGuid():N}"[..24], ConnectionProvider.S3, """{"region":"us-east-1"}""", secret);

    [Fact]
    public async Task CreateAsync_WithSecret_DoesNotExposeSecretOnReadModel()
    {
        var created = await Store.CreateAsync(NewRequest(), createdByUserId: null);

        created.HasSecret.Should().BeTrue();
        created.ConfigJson.Should().Contain("us-east-1");
    }

    [Fact]
    public async Task GetSecretAsync_AfterCreate_RoundTripsThePlaintext()
    {
        var created = await Store.CreateAsync(NewRequest("round-trip-me"), createdByUserId: null);

        string? secret = await Store.GetSecretAsync(created.Id);

        secret.Should().Be("round-trip-me");
    }

    [Fact]
    public async Task GetSecretAsync_WhenNoSecretStored_ReturnsNull()
    {
        var request = new CreateConnectionRequest(
            $"fs-{Guid.NewGuid():N}"[..24], ConnectionProvider.Filesystem, """{"root":"/data"}""", Secret: null);
        var created = await Store.CreateAsync(request, createdByUserId: null);

        string? secret = await Store.GetSecretAsync(created.Id);

        secret.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_Throws()
    {
        var request = NewRequest();
        await Store.CreateAsync(request, createdByUserId: null);

        Func<Task> act = async () => await Store.CreateAsync(request, createdByUserId: null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdateAsync_WithNullSecret_LeavesExistingSecretIntact()
    {
        var created = await Store.CreateAsync(NewRequest("keep-me"), createdByUserId: null);

        await Store.UpdateAsync(created.Id, new UpdateConnectionRequest(Name: "renamed-conn"));

        string? secret = await Store.GetSecretAsync(created.Id);
        secret.Should().Be("keep-me");
    }

    [Fact]
    public async Task DeleteAsync_WithNoSources_RemovesTheConnection()
    {
        var created = await Store.CreateAsync(NewRequest(), createdByUserId: null);

        bool deleted = await Store.DeleteAsync(created.Id);

        deleted.Should().BeTrue();
        (await Store.GetAsync(created.Id)).Should().BeNull();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~ConnectionStoreIntegrationTests"`
Expected: FAIL — no service registered for `IConnectionStore`.

- [ ] **Step 3: Write the implementation**

Create `src/Connapse.Storage/Connections/PostgresConnectionStore.cs`:

```csharp
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Storage.Connections;

public class PostgresConnectionStore(
    IDbContextFactory<KnowledgeDbContext> factory,
    IDataProtectionProvider dataProtection,
    ILogger<PostgresConnectionStore> logger) : IConnectionStore
{
    private IDataProtector Protector => dataProtection.CreateProtector("Connection.v1");

    public async Task<Connection> CreateAsync(CreateConnectionRequest request, Guid? createdByUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 128)
            throw new ArgumentException("Connection name must be 1-128 characters.", nameof(request));

        await using var context = await factory.CreateDbContextAsync(ct);

        bool exists = await context.Connections.AnyAsync(c => c.Name == name, ct);
        if (exists)
            throw new InvalidOperationException($"A connection with the name '{name}' already exists.");

        var entity = new ConnectionEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            Provider = (int)request.Provider,
            ConfigJson = string.IsNullOrEmpty(request.ConfigJson) ? null : JsonDocument.Parse(request.ConfigJson),
            SecretProtected = string.IsNullOrEmpty(request.Secret) ? null : Protector.Protect(request.Secret),
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Connections.Add(entity);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Created connection {ConnectionId} ({Name})", entity.Id, Sanitize(entity.Name));

        return MapToModel(entity, 0);
    }

    public async Task<Connection?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var result = await context.Connections
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { Connection = c, SourceCount = c.Sources.Count })
            .FirstOrDefaultAsync(ct);

        return result is null ? null : MapToModel(result.Connection, result.SourceCount);
    }

    public async Task<IReadOnlyList<Connection>> ListAsync(int skip = 0, int take = 50, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var results = await context.Connections
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Skip(skip)
            .Take(take)
            .Select(c => new { Connection = c, SourceCount = c.Sources.Count })
            .ToListAsync(ct);

        return results.Select(r => MapToModel(r.Connection, r.SourceCount)).ToList();
    }

    public async Task<Connection?> UpdateAsync(Guid id, UpdateConnectionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null) return null;

        if (!string.IsNullOrWhiteSpace(request.Name))
            entity.Name = request.Name.Trim();

        if (request.ConfigJson is not null)
            entity.ConfigJson = string.IsNullOrEmpty(request.ConfigJson) ? null : JsonDocument.Parse(request.ConfigJson);

        // A null Secret means "leave it alone" — only a non-empty value replaces it.
        if (!string.IsNullOrEmpty(request.Secret))
            entity.SecretProtected = Protector.Protect(request.Secret);

        entity.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);

        int sourceCount = await context.Sources.CountAsync(s => s.ConnectionId == id, ct);
        return MapToModel(entity, sourceCount);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null) return false;

        int sourceCount = await context.Sources.CountAsync(s => s.ConnectionId == id, ct);
        if (sourceCount > 0)
            throw new InvalidOperationException(
                $"Cannot delete this connection: {sourceCount} source(s) still use it. Remove or repoint them first.");

        context.Connections.Remove(entity);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Deleted connection {ConnectionId}", id);
        return true;
    }

    public async Task<string?> GetSecretAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        string? ciphertext = await context.Connections
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => c.SecretProtected)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrEmpty(ciphertext) ? null : Protector.Unprotect(ciphertext);
    }

    private static Connection MapToModel(ConnectionEntity entity, int sourceCount) => new(
        Id: entity.Id,
        Name: entity.Name,
        Provider: (ConnectionProvider)entity.Provider,
        ConfigJson: entity.ConfigJson?.RootElement.GetRawText(),
        CreatedByUserId: entity.CreatedByUserId,
        CreatedAt: entity.CreatedAt,
        UpdatedAt: entity.UpdatedAt,
        HasSecret: !string.IsNullOrEmpty(entity.SecretProtected),
        SourceCount: sourceCount);
}
```

- [ ] **Step 4: Run the test to verify it passes**

This test needs `IConnectionStore` resolvable from DI, which Task 8 does. Apply Task 8 Step 1 now (it is two lines), then run:

`dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~ConnectionStoreIntegrationTests"`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/Connections/ tests/Connapse.Integration.Tests/ConnectionStoreIntegrationTests.cs
git commit -m "feat(storage): add PostgresConnectionStore with encrypted secrets"
```

---

### Task 7: PostgresSourceStore

**Files:**
- Create: `src/Connapse.Storage/Sources/PostgresSourceStore.cs`
- Test: `tests/Connapse.Integration.Tests/SourceStoreIntegrationTests.cs`

**Interfaces:**
- Consumes: `ISourceStore` (Task 2), `SourceEntity` (Task 3), `IConnectionStore` (Task 6) for test setup.
- Produces: `PostgresSourceStore`, registered in Task 8.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Integration.Tests/SourceStoreIntegrationTests.cs`:

```csharp
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SourceStoreIntegrationTests(SharedWebAppFixture fixture)
{
    private ISourceStore Sources => fixture.Factory.Services.GetRequiredService<ISourceStore>();
    private IConnectionStore Connections => fixture.Factory.Services.GetRequiredService<IConnectionStore>();

    private async Task<Guid> NewConnectionAsync()
    {
        var created = await Connections.CreateAsync(
            new CreateConnectionRequest($"c-{Guid.NewGuid():N}"[..24], ConnectionProvider.S3, """{"region":"us-east-1"}"""),
            createdByUserId: null);
        return created.Id;
    }

    private async Task<Source> NewSourceAsync()
    {
        Guid connectionId = await NewConnectionAsync();
        return await Sources.CreateAsync(new CreateSourceRequest(
            Name: $"s-{Guid.NewGuid():N}"[..24],
            ConnectionId: connectionId,
            ScopeJson: """{"prefix":"docs/"}"""));
    }

    [Fact]
    public async Task CreateAsync_NewSource_StartsNeverSyncedAndEnabled()
    {
        var source = await NewSourceAsync();

        source.LastSyncStatus.Should().Be(SyncStatus.Never);
        source.SyncCursor.Should().BeNull();
        source.LastSyncedAt.Should().BeNull();
        source.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateSyncStateAsync_AfterSuccessfulSync_PersistsCursor()
    {
        var source = await NewSourceAsync();
        var syncedAt = DateTime.UtcNow;

        await Sources.UpdateSyncStateAsync(source.Id, "cursor-abc", SyncStatus.Succeeded, error: null, syncedAt);

        var reloaded = await Sources.GetAsync(source.Id);
        reloaded!.SyncCursor.Should().Be("cursor-abc");
        reloaded.LastSyncStatus.Should().Be(SyncStatus.Succeeded);
        reloaded.LastSyncError.Should().BeNull();
        reloaded.LastSyncedAt.Should().BeCloseTo(syncedAt, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UpdateSyncStateAsync_WithNullCursor_ClearsItForFullResync()
    {
        var source = await NewSourceAsync();
        await Sources.UpdateSyncStateAsync(source.Id, "cursor-abc", SyncStatus.Succeeded, null, DateTime.UtcNow);

        await Sources.UpdateSyncStateAsync(source.Id, null, SyncStatus.Failed, "410 Gone", DateTime.UtcNow);

        var reloaded = await Sources.GetAsync(source.Id);
        reloaded!.SyncCursor.Should().BeNull();
        reloaded.LastSyncStatus.Should().Be(SyncStatus.Failed);
        reloaded.LastSyncError.Should().Be("410 Gone");
    }

    [Fact]
    public async Task CreateAsync_UnknownConnection_Throws()
    {
        Func<Task> act = async () => await Sources.CreateAsync(new CreateSourceRequest(
            Name: $"s-{Guid.NewGuid():N}"[..24],
            ConnectionId: Guid.NewGuid(),
            ScopeJson: "{}"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ListByConnectionAsync_ReturnsOnlyThatConnectionsSources()
    {
        Guid connectionId = await NewConnectionAsync();
        await Sources.CreateAsync(new CreateSourceRequest($"a-{Guid.NewGuid():N}"[..24], connectionId, "{}"));
        await Sources.CreateAsync(new CreateSourceRequest($"b-{Guid.NewGuid():N}"[..24], connectionId, "{}"));
        await NewSourceAsync(); // belongs to a different connection

        var results = await Sources.ListByConnectionAsync(connectionId);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.ConnectionId == connectionId);
    }

    [Fact]
    public async Task UpdateAsync_SetEnabledFalse_Persists()
    {
        var source = await NewSourceAsync();

        await Sources.UpdateAsync(source.Id, new UpdateSourceRequest(Enabled: false));

        var reloaded = await Sources.GetAsync(source.Id);
        reloaded!.Enabled.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~SourceStoreIntegrationTests"`
Expected: FAIL — no service registered for `ISourceStore`.

- [ ] **Step 3: Write the implementation**

Create `src/Connapse.Storage/Sources/PostgresSourceStore.cs`:

```csharp
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Storage.Sources;

public class PostgresSourceStore(
    IDbContextFactory<KnowledgeDbContext> factory,
    ILogger<PostgresSourceStore> logger) : ISourceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<Source> CreateAsync(CreateSourceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 128)
            throw new ArgumentException("Source name must be 1-128 characters.", nameof(request));

        await using var context = await factory.CreateDbContextAsync(ct);

        bool connectionExists = await context.Connections.AnyAsync(c => c.Id == request.ConnectionId, ct);
        if (!connectionExists)
            throw new InvalidOperationException($"Connection '{request.ConnectionId}' does not exist.");

        bool nameTaken = await context.Sources.AnyAsync(s => s.Name == name, ct);
        if (nameTaken)
            throw new InvalidOperationException($"A source with the name '{name}' already exists.");

        var entity = new SourceEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = request.Description?.Trim(),
            ConnectionId = request.ConnectionId,
            ScopeJson = JsonDocument.Parse(string.IsNullOrEmpty(request.ScopeJson) ? "{}" : request.ScopeJson),
            SyncIntervalSeconds = request.SyncIntervalSeconds,
            Enabled = true,
            LastSyncStatus = (int)SyncStatus.Never,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Sources.Add(entity);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Created source {SourceId} ({Name})", entity.Id, Sanitize(entity.Name));

        return MapToModel(entity, 0);
    }

    public async Task<Source?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var result = await context.Sources
            .AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new { Source = s, DocumentCount = s.Documents.Count })
            .FirstOrDefaultAsync(ct);

        return result is null ? null : MapToModel(result.Source, result.DocumentCount);
    }

    public async Task<Source?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var normalized = name.Trim();

        await using var context = await factory.CreateDbContextAsync(ct);

        var result = await context.Sources
            .AsNoTracking()
            .Where(s => s.Name == normalized)
            .Select(s => new { Source = s, DocumentCount = s.Documents.Count })
            .FirstOrDefaultAsync(ct);

        return result is null ? null : MapToModel(result.Source, result.DocumentCount);
    }

    public async Task<IReadOnlyList<Source>> ListAsync(int skip = 0, int take = 50, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var results = await context.Sources
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .Skip(skip)
            .Take(take)
            .Select(s => new { Source = s, DocumentCount = s.Documents.Count })
            .ToListAsync(ct);

        return results.Select(r => MapToModel(r.Source, r.DocumentCount)).ToList();
    }

    public async Task<IReadOnlyList<Source>> ListByConnectionAsync(Guid connectionId, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var results = await context.Sources
            .AsNoTracking()
            .Where(s => s.ConnectionId == connectionId)
            .OrderBy(s => s.Name)
            .Select(s => new { Source = s, DocumentCount = s.Documents.Count })
            .ToListAsync(ct);

        return results.Select(r => MapToModel(r.Source, r.DocumentCount)).ToList();
    }

    public async Task<Source?> UpdateAsync(Guid id, UpdateSourceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Sources.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return null;

        if (!string.IsNullOrWhiteSpace(request.Name))
            entity.Name = request.Name.Trim();

        if (request.Description is not null)
            entity.Description = request.Description.Trim();

        if (request.ScopeJson is not null)
            entity.ScopeJson = JsonDocument.Parse(string.IsNullOrEmpty(request.ScopeJson) ? "{}" : request.ScopeJson);

        if (request.SyncIntervalSeconds.HasValue)
            entity.SyncIntervalSeconds = request.SyncIntervalSeconds;

        if (request.Enabled.HasValue)
            entity.Enabled = request.Enabled.Value;

        entity.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);

        int documentCount = await context.Documents.CountAsync(d => d.SourceId == id, ct);
        return MapToModel(entity, documentCount);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Sources.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return false;

        context.Sources.Remove(entity);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Deleted source {SourceId}", id);
        return true;
    }

    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        return await context.Sources.AnyAsync(s => s.Id == id, ct);
    }

    public async Task UpdateSyncStateAsync(Guid id, string? cursor, SyncStatus status, string? error, DateTime? syncedAt, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Sources.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return;

        entity.SyncCursor = cursor;
        entity.LastSyncStatus = (int)status;
        entity.LastSyncError = error;
        entity.LastSyncedAt = syncedAt;
        entity.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(ct);
    }

    public async Task<ContainerSettingsOverrides?> GetSettingsOverridesAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var json = await context.Sources
            .AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => s.SettingsOverridesJson)
            .FirstOrDefaultAsync(ct);

        return json is null
            ? null
            : JsonSerializer.Deserialize<ContainerSettingsOverrides>(json.RootElement.GetRawText(), SerializerOptions);
    }

    public async Task SaveSettingsOverridesAsync(Guid id, ContainerSettingsOverrides overrides, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Sources.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return;

        entity.SettingsOverridesJson = JsonDocument.Parse(JsonSerializer.Serialize(overrides, SerializerOptions));
        entity.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(ct);
    }

    public async Task UpdateSummaryAsync(Guid id, string? summary, DateTime? generatedAt, string? docSetHash, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Sources.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return;

        entity.Summary = summary;
        entity.SummaryGeneratedAt = generatedAt;
        entity.SummaryDocSetHash = docSetHash;
        entity.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(ct);
    }

    private static Source MapToModel(SourceEntity entity, int documentCount) => new(
        Id: entity.Id,
        Name: entity.Name,
        Description: entity.Description,
        ConnectionId: entity.ConnectionId,
        ScopeJson: entity.ScopeJson.RootElement.GetRawText(),
        CreatedAt: entity.CreatedAt,
        UpdatedAt: entity.UpdatedAt,
        Enabled: entity.Enabled,
        SyncCursor: entity.SyncCursor,
        LastSyncedAt: entity.LastSyncedAt,
        LastSyncStatus: (SyncStatus)entity.LastSyncStatus,
        LastSyncError: entity.LastSyncError,
        SyncIntervalSeconds: entity.SyncIntervalSeconds,
        SettingsOverrides: entity.SettingsOverridesJson is null
            ? null
            : JsonSerializer.Deserialize<ContainerSettingsOverrides>(entity.SettingsOverridesJson.RootElement.GetRawText(), SerializerOptions),
        Summary: entity.Summary,
        SummaryGeneratedAt: entity.SummaryGeneratedAt,
        SummaryDocSetHash: entity.SummaryDocSetHash,
        DocumentCount: documentCount);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~SourceStoreIntegrationTests"`
Expected: PASS, 6 tests. Task 8 Step 1 must already be applied for `ISourceStore` to resolve.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/Sources/ tests/Connapse.Integration.Tests/SourceStoreIntegrationTests.cs
git commit -m "feat(storage): add PostgresSourceStore with sync state persistence"
```

---

### Task 8: Register the stores and verify the full suite

**Files:**
- Modify: `src/Connapse.Storage/ServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: `PostgresConnectionStore` (Task 6), `PostgresSourceStore` (Task 7).
- Produces: `IConnectionStore` and `ISourceStore` resolvable from DI. Phase 2 depends on this.

- [ ] **Step 1: Register the stores**

In `src/Connapse.Storage/ServiceCollectionExtensions.cs`, find the line registering `IContainerStore` and add beside it:

```csharp
        services.AddScoped<IConnectionStore, PostgresConnectionStore>();
        services.AddScoped<ISourceStore, PostgresSourceStore>();
```

Add the matching `using Connapse.Storage.Connections;` and `using Connapse.Storage.Sources;` at the top. Match the existing lifetime used for `IContainerStore` — if it is registered as something other than Scoped, use that instead, because `IDbContextFactory` resolution must stay consistent with the surrounding code.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test`
Expected: every test passes, including the pre-existing search and ingestion tests that exercise the renamed chunk columns.

- [ ] **Step 3: Verify nothing reads the new tables yet**

Run: `git diff main --stat -- src/Connapse.Web/`
Expected: no output. Phase 1 must not touch the web layer at all; if it does, that work belongs in Phase 3 or 4.

- [ ] **Step 4: Commit**

```bash
git add src/Connapse.Storage/ServiceCollectionExtensions.cs
git commit -m "feat(storage): register connection and source stores"
```

---

## Phase 1 done-condition

Migrations apply cleanly against a fresh database and against a database with existing containers, documents, and vectors; `dotnet test` passes in full; `IConnectionStore` and `ISourceStore` resolve from DI; and no code outside `Connapse.Core` and `Connapse.Storage` references either. At that point Phase 2 (backfill and compatibility read) can be planned against a stable store API.
