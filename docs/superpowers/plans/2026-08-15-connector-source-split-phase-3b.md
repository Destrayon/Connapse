# Connector/Source Split — Phase 3b (Source Sync Engine) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore syncing for external sources — which has been silently broken since #359 — by re-keying the watcher from containers to sources, and make the ingestion path owner-aware.

**Architecture:** `ConnectorWatcherService` enumerates `containerStore.ListAsync()` and filters to Filesystem/S3/AzureBlob. Phase 2 moved every one of those rows into `sources`, so it now finds nothing and no code enumerates sources. `SourceSyncService` replaces it, driven by `ISourceStore`, resolving each source's connector through a new `IConnectorFactory.Create(Source, Connection)` overload. Connectors implementing `ISyncCursorConnector` take a delta path guarded by `TryAdvanceSyncStateAsync`; the rest keep the existing list-and-diff logic as a fallback. The ingestion pipeline learns to write `source_id` instead of always writing `container_id`.

**Tech Stack:** .NET 10, EF Core (Npgsql), PostgreSQL/pgvector, xUnit, FluentAssertions, NSubstitute, Testcontainers.

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
- Integration tests use `[Collection("Integration Tests")]` and reach DI via `fixture.Factory.Services.CreateAsyncScope()`.
- Commit format: `<type>: <summary>`. Milestone v0.4.0. Branch `feature/351-source-sync-engine`.

## Read this first: sources are not syncing right now

`ConnectorWatcherService.ExecuteAsync` starts watchers for `containers.Where(c => IsWatchableConnector(c.ConnectorType))`, where `IsWatchableConnector` is `Filesystem | S3 | AzureBlob`. Managed storage is deliberately excluded — it has no remote to poll.

Phase 2's backfill (#359) moved every Filesystem, S3, and Azure Blob row out of `containers` and into `sources`. Nothing enumerates `sources`. So since that merge:

- No external source picks up new, changed, or deleted remote files.
- Already-indexed content stays searchable, so this is a stalled pipeline rather than data loss.
- `POST /api/containers/{id}/sync` cannot paper over it — a source ID 404s there, by design.

**This is the reason PR 3b exists.** Treat it as restoring lost function, not as an optimisation, and prioritise the fallback path working over the delta path being elegant.

## The three carried findings, and where each lands

1. **Owner ID propagation (Task 2).** `IngestionPipeline` sets `documentEntity.ContainerId = containerId` unconditionally, and passes `["containerId"] = containerId.ToString()` into vector metadata. `PgVectorStore` then defaults a missing or unparseable value to `Guid.Empty`. Ingesting into a source today would violate the `ck_documents_single_owner` CHECK, and any vector written would carry a zero owner and never match an owner-scoped query.
2. **`chunks.owner_id` enforcement (Task 5, decision).** This PR introduces the second write path, which is what makes divergence from `documents.owner_id` plausible.
3. **Cursor compare-and-swap (Task 4).** `TryAdvanceSyncStateAsync` exists and is tested but has no caller. Forward progress uses it; a `RequiresFullResync` response uses `UpdateSyncStateAsync` instead, because clearing a stale cursor must be unconditional — gating that behind CAS would break the recovery it exists for.

## File Structure

**Create:**
- `src/Connapse.Core/Models/SyncModels.cs` — `SourceSyncResult`, `OwnerRef`.
- `src/Connapse.Storage/Connectors/SourceConnectorFactory.cs` — builds a connector from a source plus its connection.
- `src/Connapse.Web/Services/SourceSyncService.cs` — the background service.
- `tests/Connapse.Core.Tests/Sync/OwnerRefTests.cs`
- `tests/Connapse.Integration.Tests/SourceSyncIntegrationTests.cs`
- `tests/Connapse.Integration.Tests/SourceIngestionOwnershipTests.cs`

**Modify:**
- `src/Connapse.Core/Interfaces/IConnectorFactory.cs` — add the source overload.
- `src/Connapse.Core/Models/IngestionModels.cs` — `IngestionOptions` carries an owner reference rather than a bare container ID.
- `src/Connapse.Ingestion/Pipeline/IngestionPipeline.cs:166,191,205,344,356` — write `source_id` when the owner is a source; pass `ownerId` into vector metadata.
- `src/Connapse.Storage/Vectors/PgVectorStore.cs` — read `ownerId`, reject missing/invalid instead of defaulting to `Guid.Empty`.
- `src/Connapse.Web/Program.cs` — register `SourceSyncService`, retire `ConnectorWatcherService`.

**Delete:**
- `src/Connapse.Web/Services/ConnectorWatcherService.cs` (700 lines) once `SourceSyncService` covers its behaviour.

---

### Task 1: An explicit owner reference

**Files:**
- Create: `src/Connapse.Core/Models/SyncModels.cs`
- Test: `tests/Connapse.Core.Tests/Sync/OwnerRefTests.cs`

**Interfaces:**
- Produces: `OwnerRef(Guid Id, bool IsSource)` with factories `OwnerRef.ForContainer(Guid)` and `OwnerRef.ForSource(Guid)`; `SourceSyncResult(int Upserted, int Deleted, bool UsedDeltaPath, bool RequiredResync, string? Error)`.

Threading a bare `Guid` plus a boolean through the pipeline invites getting the boolean wrong at one call site. A single type makes the ownership explicit and keeps the XOR invariant expressible in one place.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Core.Tests/Sync/OwnerRefTests.cs`:

```csharp
using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Sync;

[Trait("Category", "Unit")]
public class OwnerRefTests
{
    [Fact]
    public void ForContainer_IsNotASource()
    {
        var id = Guid.NewGuid();

        var owner = OwnerRef.ForContainer(id);

        owner.Id.Should().Be(id);
        owner.IsSource.Should().BeFalse();
    }

    [Fact]
    public void ForSource_IsASource()
    {
        var id = Guid.NewGuid();

        var owner = OwnerRef.ForSource(id);

        owner.Id.Should().Be(id);
        owner.IsSource.Should().BeTrue();
    }

    [Fact]
    public void ContainerId_And_SourceId_AreMutuallyExclusive()
    {
        // These map straight onto the ck_documents_single_owner CHECK: exactly one of the
        // two columns is set, and this type is what decides which.
        var container = OwnerRef.ForContainer(Guid.NewGuid());
        var source = OwnerRef.ForSource(Guid.NewGuid());

        container.ContainerId.Should().NotBeNull();
        container.SourceId.Should().BeNull();
        source.ContainerId.Should().BeNull();
        source.SourceId.Should().NotBeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~OwnerRefTests"`
Expected: FAIL — `OwnerRef` does not exist.

- [ ] **Step 3: Write the models**

Create `src/Connapse.Core/Models/SyncModels.cs`:

```csharp
namespace Connapse.Core;

/// <summary>
/// Identifies who owns a document: a managed container or an external source. Exists so the
/// ownership decision is made once, at the point the owner is known, rather than by threading
/// a Guid and a boolean through every layer and hoping each call site pairs them correctly.
/// <para>
/// <see cref="ContainerId"/> and <see cref="SourceId"/> are exactly the two columns behind the
/// ck_documents_single_owner CHECK constraint — precisely one is ever non-null.
/// </para>
/// </summary>
public record OwnerRef(Guid Id, bool IsSource)
{
    public static OwnerRef ForContainer(Guid id) => new(id, IsSource: false);
    public static OwnerRef ForSource(Guid id) => new(id, IsSource: true);

    public Guid? ContainerId => IsSource ? null : Id;
    public Guid? SourceId => IsSource ? Id : null;
}

/// <summary>
/// Outcome of one sync cycle for one source.
/// </summary>
public record SourceSyncResult(
    int Upserted,
    int Deleted,
    bool UsedDeltaPath,
    bool RequiredResync,
    string? Error);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~OwnerRefTests"`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Models/SyncModels.cs tests/Connapse.Core.Tests/Sync/
git commit -m "feat(core): add OwnerRef to make document ownership explicit

Part of #351"
```

---

### Task 2: Make ingestion owner-aware

**Files:**
- Modify: `src/Connapse.Core/Models/IngestionModels.cs`
- Modify: `src/Connapse.Ingestion/Pipeline/IngestionPipeline.cs` (lines 166, 191, 205, 344, 356)
- Modify: `src/Connapse.Storage/Vectors/PgVectorStore.cs`
- Test: `tests/Connapse.Integration.Tests/SourceIngestionOwnershipTests.cs`

**Interfaces:**
- Consumes: `OwnerRef` from Task 1.
- Produces: documents ingested for a source carry `source_id` with `container_id` null; chunks and vectors carry that same `owner_id`.

This is carried finding 1. Without it, Task 3's sync engine would produce documents that violate the CHECK constraint.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Integration.Tests/SourceIngestionOwnershipTests.cs`:

```csharp
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SourceIngestionOwnershipTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string p) => $"{p}-{Guid.NewGuid():N}"[..20];

    private static async Task<Guid> SeedSourceAsync(IServiceProvider sp)
    {
        var connections = sp.GetRequiredService<IConnectionStore>();
        var sources = sp.GetRequiredService<ISourceStore>();

        var connection = await connections.CreateAsync(
            new CreateConnectionRequest(ShortName("c"), ConnectionProvider.S3, """{"region":"us-east-1"}"""),
            createdByUserId: null);

        var source = await sources.CreateAsync(
            new CreateSourceRequest(ShortName("s"), connection.Id, """{"bucketName":"b"}"""));

        return source.Id;
    }

    [Fact]
    public async Task Ingest_ForSourceOwner_WritesSourceIdNotContainerId()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid sourceId = await SeedSourceAsync(scope.ServiceProvider);

        var pipeline = scope.ServiceProvider.GetRequiredService<IIngestionPipeline>();
        await pipeline.IngestAsync(
            new MemoryStream("owner test content"u8.ToArray()),
            new IngestionOptions
            {
                Owner = OwnerRef.ForSource(sourceId),
                FileName = "owned.md",
                ContentType = "text/markdown",
            },
            CancellationToken.None);

        await using var ctx = await factory.CreateDbContextAsync();
        var doc = await ctx.Documents.AsNoTracking().SingleAsync(d => d.SourceId == sourceId);

        doc.ContainerId.Should().BeNull("the XOR check forbids both owners being set");
        doc.SourceId.Should().Be(sourceId);
        doc.OwnerId.Should().Be(sourceId);
    }

    [Fact]
    public async Task Ingest_ForSourceOwner_WritesChunksAndVectorsWithSourceOwner()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid sourceId = await SeedSourceAsync(scope.ServiceProvider);

        var pipeline = scope.ServiceProvider.GetRequiredService<IIngestionPipeline>();
        await pipeline.IngestAsync(
            new MemoryStream("chunk owner content for the source"u8.ToArray()),
            new IngestionOptions
            {
                Owner = OwnerRef.ForSource(sourceId),
                FileName = "chunks.md",
                ContentType = "text/markdown",
            },
            CancellationToken.None);

        await using var ctx = await factory.CreateDbContextAsync();

        // A zero owner here is the specific failure mode: vectors written with Guid.Empty
        // never match an owner-scoped search and the content is silently unreachable.
        (await ctx.Chunks.AsNoTracking().Where(c => c.OwnerId == sourceId).AnyAsync()).Should().BeTrue();
        (await ctx.Chunks.AsNoTracking().Where(c => c.OwnerId == Guid.Empty).AnyAsync()).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~SourceIngestionOwnershipTests"`
Expected: FAIL — `IngestionOptions` has no `Owner` property.

- [ ] **Step 3: Add the owner to IngestionOptions**

In `src/Connapse.Core/Models/IngestionModels.cs`, add an `OwnerRef? Owner` property to `IngestionOptions`, keeping the existing `ContainerId` string for now so unrelated callers still compile:

```csharp
    /// <summary>
    /// Who will own the ingested document. Preferred over ContainerId, which cannot express
    /// source ownership. When null, ContainerId is used and the owner is a container.
    /// </summary>
    public OwnerRef? Owner { get; init; }
```

- [ ] **Step 4: Resolve the owner once in the pipeline**

In `IngestionPipeline`, replace the container-id parsing at line 166 with a single owner resolution, and use it at every downstream site:

```csharp
        // Resolve ownership once. Everything below writes through this, so a source-owned
        // document cannot accidentally be recorded against container_id.
        var owner = options.Owner
            ?? (!string.IsNullOrEmpty(options.ContainerId) && Guid.TryParse(options.ContainerId, out var cId)
                ? OwnerRef.ForContainer(cId)
                : throw new ArgumentException("Ingestion requires an owner (Owner or a parseable ContainerId).", nameof(options)));
```

At line 191, replace `documentEntity.ContainerId = containerId;` with:

```csharp
                documentEntity.ContainerId = owner.ContainerId;
                documentEntity.SourceId = owner.SourceId;
```

At line 205 (`ContainerId = containerId,` in the document initializer), make the same pair of assignments. At line 344 (`OwnerId = containerId,` on the chunk) use `OwnerId = owner.Id`. At line 356, change the vector metadata key:

```csharp
                    ["ownerId"] = owner.Id.ToString(),
```

- [ ] **Step 5: Make PgVectorStore demand an owner**

In `PgVectorStore`, replace the silent `Guid.Empty` default with a hard failure. A vector with a zero owner is unreachable by every owner-scoped query, so failing loudly at write time is strictly better than discovering it at search time:

```csharp
            if (!metadata.TryGetValue("ownerId", out var ownerIdStr) || !Guid.TryParse(ownerIdStr, out var ownerId))
                throw new ArgumentException("Each item's metadata must contain a valid 'ownerId'", nameof(items));
```

Apply this in both `UpsertAsync` and `UpsertBatchAsync`, replacing every `containerId` local with `ownerId`.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~SourceIngestionOwnershipTests"`
Expected: PASS, 2 tests.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: all pass. Existing callers that passed `containerId` metadata now fail the stricter check — update them to pass `ownerId`. Grep for `"containerId"` under `src/` and `tests/` to find them.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(ingestion): write source ownership through the pipeline

Documents ingested for a source now set source_id rather than container_id,
and vector upserts require an explicit ownerId instead of silently defaulting
to Guid.Empty — a zero owner is unreachable by every owner-scoped query.

Part of #351"
```

---

### Task 3: Build a connector from a source

**Files:**
- Modify: `src/Connapse.Core/Interfaces/IConnectorFactory.cs`
- Create: `src/Connapse.Storage/Connectors/SourceConnectorFactory.cs`
- Test: `tests/Connapse.Integration.Tests/SourceConnectorFactoryTests.cs`

**Interfaces:**
- Consumes: `Source`, `Connection` from Phase 1.
- Produces: `IConnectorFactory.Create(Source source, Connection connection)` returning `IConnector`.

The existing factory takes a `Container` and reads `connector_config`. A source splits that across the connection (credential and endpoint) and its own scope, so the factory needs to recombine them.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Integration.Tests/SourceConnectorFactoryTests.cs`:

```csharp
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SourceConnectorFactoryTests(SharedWebAppFixture fixture)
{
    private static Connection MakeConnection(ConnectionProvider provider, string config) => new(
        Id: Guid.NewGuid(),
        Name: "c",
        Provider: provider,
        ConfigJson: config,
        CreatedByUserId: null,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);

    private static Source MakeSource(Guid connectionId, string scope) => new(
        Id: Guid.NewGuid(),
        Name: "s",
        Description: null,
        ConnectionId: connectionId,
        ScopeJson: scope,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);

    [Fact]
    public void Create_S3Source_RecombinesConnectionAndScope()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IConnectorFactory>();

        var connection = MakeConnection(ConnectionProvider.S3, """{"region":"eu-west-1","roleArn":null}""");
        var source = MakeSource(connection.Id, """{"bucketName":"my-bucket","prefix":"docs/"}""");

        var connector = factory.Create(source, connection);

        connector.Type.Should().Be(ConnectorType.S3);
    }

    [Fact]
    public void Create_SourceConnector_IsNotWritable()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IConnectorFactory>();

        var connection = MakeConnection(ConnectionProvider.S3, """{"region":"eu-west-1"}""");
        var source = MakeSource(connection.Id, """{"bucketName":"b"}""");

        var connector = factory.Create(source, connection);

        // The whole point of the epic: a source can never be mutated through Connapse.
        (connector is IWritableConnector).Should().BeFalse();
    }

    [Fact]
    public void Create_MismatchedConnection_Throws()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IConnectorFactory>();

        var connection = MakeConnection(ConnectionProvider.S3, """{"region":"eu-west-1"}""");
        var source = MakeSource(Guid.NewGuid(), """{"bucketName":"b"}"""); // different connection

        Action act = () => factory.Create(source, connection);

        act.Should().Throw<ArgumentException>();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~SourceConnectorFactoryTests"`
Expected: FAIL — no `Create(Source, Connection)` overload.

- [ ] **Step 3: Extend the interface**

In `src/Connapse.Core/Interfaces/IConnectorFactory.cs`:

```csharp
namespace Connapse.Core.Interfaces;

public interface IConnectorFactory
{
    IConnector Create(Container container);

    /// <summary>
    /// Builds a read-only connector for a source by recombining its connection's credential
    /// and endpoint with the source's own scope. Never returns an IWritableConnector.
    /// Throws ArgumentException when the connection does not own the source.
    /// </summary>
    IConnector Create(Source source, Connection connection);
}
```

- [ ] **Step 4: Implement the overload**

Add to the existing `ConnectorFactory` a method that merges the two JSON blobs into the connector config each connector already understands — for S3, `{region, roleArn}` from the connection plus `{bucketName, prefix}` from the scope; for Azure Blob, `{storageAccountName, managedIdentityClientId}` plus `{containerName, prefix}`; for Filesystem, `{allowedRoot}` plus `{subPath, includePatterns, excludePatterns}`, with the root and subpath combined and the result verified to stay beneath the allowed root.

Guard the ownership first:

```csharp
        if (source.ConnectionId != connection.Id)
            throw new ArgumentException(
                $"Connection '{connection.Id}' does not own source '{source.Id}'.", nameof(connection));
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~SourceConnectorFactoryTests"`
Expected: PASS, 3 tests.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(storage): build read-only connectors from a source and its connection

Part of #351"
```

---

### Task 4: The sync engine

**Files:**
- Create: `src/Connapse.Web/Services/SourceSyncService.cs`
- Modify: `src/Connapse.Web/Program.cs`
- Delete: `src/Connapse.Web/Services/ConnectorWatcherService.cs`
- Test: `tests/Connapse.Integration.Tests/SourceSyncIntegrationTests.cs`

**Interfaces:**
- Consumes: `ISourceStore`, `IConnectionStore`, `IConnectorFactory.Create(Source, Connection)`, `ISyncCursorConnector`, `OwnerRef`.
- Produces: `SourceSyncService.SyncSourceAsync(Source, CancellationToken)` returning `Task<SourceSyncResult>`.

This is carried finding 3. Port `CloudSyncAsync`, `EnqueueCloudIngestionAsync`, and `DeleteDocumentByVirtualPathAsync` from `ConnectorWatcherService` largely intact — they are load-bearing and already work; only their keying changes from container to source.

> **This task's tests are specified as intent, not code — unlike every other task in this plan and the two before it.** Each body needs a fake `ISyncCursorConnector` and a fake plain `IConnector` injected into the sync service, and their shape depends on `SourceSyncService`'s constructor, which this task is what decides. Writing the bodies now would mean inventing a seam and then bending the implementation to match it.
>
> **Do not treat the list below as sufficient to start.** Decide the service's dependencies first (Step 3), then come back and write these six tests in full before writing any of its logic. If Task 4 feels oversized while executing it — it is the largest in the plan — split it there: fallback path first as its own commit, then the delta path.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Integration.Tests/SourceSyncIntegrationTests.cs` covering, at minimum:

```csharp
    [Fact]
    public async Task SyncSourceAsync_FallbackPath_UpsertsAndDeletes()
    {
        // A connector with no ISyncCursorConnector must still sync via list-and-diff.
        // Assert new remote files are enqueued and vanished ones delete their documents.
    }

    [Fact]
    public async Task SyncSourceAsync_DeltaPath_AdvancesCursorWithCompareAndSwap()
    {
        // A connector implementing ISyncCursorConnector advances the stored cursor, and
        // the stored value matches the delta's NextCursor.
    }

    [Fact]
    public async Task SyncSourceAsync_ConcurrentCompletion_DoesNotRegressCursor()
    {
        // Two cycles reading the same starting cursor: the late finisher must lose.
        // This is why TryAdvanceSyncStateAsync exists.
    }

    [Fact]
    public async Task SyncSourceAsync_RequiresFullResync_ClearsCursorUnconditionally()
    {
        // A 410/409-equivalent response must clear the cursor even though CAS would
        // reject the write — the reset path is UpdateSyncStateAsync, not TryAdvance.
    }

    [Fact]
    public async Task SyncSourceAsync_DisabledSource_IsSkipped()
    {
        // Enabled = false means no remote calls at all.
    }

    [Fact]
    public async Task SyncSourceAsync_ConnectorThrows_RecordsFailureAndKeepsCursor()
    {
        // A transient remote error must not clear progress; LastSyncStatus goes Failed
        // and SyncCursor is unchanged so the next cycle resumes.
    }
```

Implement each body against a fake `ISyncCursorConnector` and a fake plain `IConnector` registered in the test scope.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~SourceSyncIntegrationTests"`
Expected: FAIL — `SourceSyncService` does not exist.

- [ ] **Step 3: Write SourceSyncService**

Structure it as: enumerate enabled sources; per source resolve connection and connector; if the connector implements `ISyncCursorConnector` take the delta path, else the fallback; record the outcome. The two state-writing rules are the part to get right:

```csharp
        // Forward progress is compare-and-swap: a cycle that started earlier but finished
        // later must not overwrite newer progress.
        if (!delta.RequiresFullResync)
        {
            bool advanced = await sourceStore.TryAdvanceSyncStateAsync(
                source.Id, expectedCursor: source.SyncCursor, newCursor: delta.NextCursor,
                SyncStatus.Succeeded, error: null, DateTime.UtcNow, ct);

            if (!advanced)
                logger.LogWarning("Source {SourceId} advanced by another cycle; discarding this result", source.Id);
        }
        else
        {
            // A resync is a deliberate reset and must land regardless of the stored value.
            // Gating it behind compare-and-swap would break the recovery it exists for.
            await sourceStore.UpdateSyncStateAsync(
                source.Id, cursor: null, SyncStatus.Failed,
                error: "Provider requested a full resync; cursor cleared.", DateTime.UtcNow, ct);
        }
```

Ingestion enqueued from here must pass `OwnerRef.ForSource(source.Id)`.

- [ ] **Step 4: Register it and retire the watcher**

In `Program.cs`, replace the `ConnectorWatcherService` singleton and hosted-service registrations with `SourceSyncService`. Remove `ConnectorWatcherService` injections from `ContainersEndpoints` and `Home.razor` — with containers managed-only, there is nothing for it to watch.

- [ ] **Step 5: Delete the watcher**

```bash
git rm src/Connapse.Web/Services/ConnectorWatcherService.cs
```

Run `dotnet build` and clear the fallout. Move any test in `tests/Connapse.Core.Tests/Connectors/CloudSyncTests.cs` that still describes real behaviour onto `SourceSyncService`; delete the rest.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(web): replace ConnectorWatcherService with SourceSyncService

Sources have not been syncing since #359: the watcher enumerated containers
and filtered to external connector types, which the backfill emptied. This
re-keys sync to sources and uses TryAdvanceSyncStateAsync for forward
progress, reserving the unconditional path for full resyncs.

Part of #351"
```

---

### Task 5: Decide on chunks.owner_id enforcement

**Files:**
- Possibly create: a migration adding a trigger
- Modify: `docs/superpowers/specs/2026-08-13-connector-source-split-design.md` with the decision

**Interfaces:** none.

This is carried finding 2, and it is a decision task, not a code task. Task 4 introduced the second write path, so record the outcome either way rather than leaving it open.

- [ ] **Step 1: Establish whether divergence is now reachable**

Run: `grep -rn "OwnerId = " src/ --include=*.cs`

Every write should derive from the same `OwnerRef` as its document. If exactly one code path sets `chunks.owner_id` and it takes the value from the owning document's `OwnerRef`, divergence is not reachable through application code and a trigger guards only against direct SQL.

- [ ] **Step 2: Record the decision**

If not reachable: add a short note to the spec's data model section explaining why no trigger was added, so the question is not re-litigated. If reachable: add a migration with a trigger deriving `chunks.owner_id` from `document_id`, plus tests covering insert and document-owner change.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "docs: record the chunks.owner_id enforcement decision

Part of #351"
```

---

## PR 3b done-condition

External sources sync again — new, changed, and deleted remote files are reflected. Documents ingested for a source carry `source_id`, and their chunks and vectors carry the same `owner_id` with no `Guid.Empty` anywhere. Forward cursor progress uses compare-and-swap and cannot regress; a resync clears the cursor unconditionally. `ConnectorWatcherService` is gone. `dotnet test` passes in full.

## Progress log

**Task 4 — SourceSyncService.** Written test-first. Two tests failed against the first cut, both real bugs: five `PostgresDocumentStore` queries still filtered on `ContainerId`, so every one returned nothing for a source-owned document; and the failure path wrote back the cursor from the cycle-start snapshot, rolling progress backwards after a transient outage. Files: `SourceSyncService.cs`, `PostgresDocumentStore.cs`, `SourceSyncIntegrationTests.cs`.

**Task 4 (cont.) — watcher retired.** `SourceSyncService` registered; `ConnectorWatcherService` (700 lines) and `CloudSyncTests` deleted. Its four call sites were already no-ops, since both watch methods return immediately for managed storage. Two things the plan did not anticipate:

- **Change detection was missing.** `CloudSyncTests` covered skipping unchanged and in-flight files; the first cut of `EnqueueAllAsync` enqueued every remote file every cycle. The content hash is computed after the download and parse, so it dedupes nothing that costs money — each source would have re-embedded its whole remote every five minutes. Now stores the remote's size and timestamp on the document and compares against it, which unlike the watcher's in-memory snapshot survives a restart. A document with no recorded signature is re-ingested once, which writes one; assuming "unchanged" instead would leave it permanently undetectable.
- **`MapToModel` handed out an empty `ContainerId` for source-owned rows.** `container_id` is NULL there and `Nullable<Guid>.ToString()` returns `""` rather than throwing, so consumers silently got an empty owner and `StoreAsync`'s `Guid.Parse` of it would throw. Now maps `OwnerId`.

Also noted, not fixed: `IDocumentStore.StoreAsync` always writes `container_id` and so cannot express a source-owned row at all. No live caller needs it — the pipeline writes entities directly — but anything that later tries to pre-register a source document through it breaks silently. `FileBrowserChangeNotifier` now has no publisher; #352 decides whether it gets one.

**Task 5 — chunks.owner_id enforcement: composite foreign key.** Divergence is *not* reachable through application code: exactly one production path writes each of `chunks.owner_id` and `chunk_vectors.owner_id`, and both take the value from the same `OwnerRef` that writes the document's own columns (`IngestionPipeline.cs:367` and `:379`). `IVectorStore.UpsertAsync` has no production caller.

Enforced anyway, because the consequence is the cross-owner exposure this epic exists to prevent, and the guard is nearly free: `documents` gains `UNIQUE (id, owner_id)`, and the existing single-column document FKs on `chunks` and `chunk_vectors` are replaced by composite FKs on `(document_id, owner_id)`. Declarative, still cascades, cheaper than the trigger the plan proposed, and it covers direct SQL too. Added `NOT VALID`: every insert and update is enforced from here on, but existing rows are not scanned, so a legacy diverged row cannot abort startup for every operator. Validating it is left to a later release, when it can be confirmed against real data. Files: `20260816021203_EnforceChunkOwnerMatchesDocument.cs`, `OwnerBridgeSchemaTests.cs`.

Verified: `dotnet test` — 1058 passed, 0 failed.

## Manual verification before merging

~~Automated tests use fakes for the remote. Also run once against LocalStack with a real S3 source.~~

**Done as an automated test instead** — `SourceSyncS3IntegrationTests`, three cases against a real S3 API in LocalStack: three seeded objects all enqueue, an object deleted from the bucket removes its document, and a second cycle over an untouched bucket skips it. Running on every build beats a one-off manual pass.

These resolve the real `IConnectorFactory` from DI rather than substituting it, so they are the only coverage of a connection's credentials and a source's scope being recombined into a working connector — every other sync test fakes that step. The class owns its LocalStack container directly instead of taking a collection fixture, because a second collection would get its own `SharedWebAppFixture` and duplicate the PostgreSQL and MinIO pair.

Added afterwards, from reviewing what the suite did *not* reach:

- **`SyncAllAsync` had no coverage at all.** Every other sync test entered at `SyncSourceAsync`, one level below the method the background loop calls, so the enumeration and the enabled filter were untested.
- **No Azure equivalent of the LocalStack test, and there cannot be one.** Azurite cannot authenticate `DefaultAzureCredential`, and `AzureBlobConnector` hardcodes `https://{account}.blob.core.windows.net`. Redirecting it would mean supporting shared-key auth — the stored cloud credential this project does not accept. Closed the reachable part instead: the connector configs are now `internal`-readable, and `SourceConnectorFactoryTests` asserts every recombined field for all three providers rather than just the resulting type. A dropped region or a mis-joined prefix still produces a connector of the right type and then reads the wrong bucket; the Azure `managedIdentityClientId` case matters most, since dropping it silently falls back to the default identity, which may have wider access.

- **The migration is now tested against a populated database.** `ChunkOwnerMigrationTests` owns a PostgreSQL container so it can control which migrations have been applied: it migrates to the release before the constraint, writes documents, chunks and vectors the way an existing deployment holds them — including a deliberately diverged chunk — then migrates the rest of the way. Proves the two halves of the `NOT VALID` decision: legacy divergence does not block the upgrade and is not silently repaired, while a diverged row inserted afterwards is rejected. Non-vacuous by construction, since seeding the diverged row would itself have failed had the staged migration not stopped short.

Still not covered: the delta path has no production implementer yet, so fakes are the only thing to test it against.

Verified: `dotnet test` — 1064 passed, 0 failed.
