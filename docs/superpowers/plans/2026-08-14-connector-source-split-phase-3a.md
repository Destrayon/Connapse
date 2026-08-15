# Connector/Source Split — Phase 3a (Connector Contract Split) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a source structurally incapable of being written to, by moving write methods off `IConnector`, closing container creation to managed storage only, and deleting `ContainerWriteGuard`.

**Architecture:** `WriteFileAsync`/`DeleteFileAsync` move to a new `IWritableConnector : IConnector` that only `MinioConnector` implements. Container creation rejects any non-managed `ConnectorType`, which is what makes the guard's runtime checks genuinely dead rather than merely unused. `ContainerWriteGuard` and its tests are then deleted, along with the Filesystem connector's per-container write flags. `ISyncCursorConnector` is introduced here so PR 3b has a contract to build the sync engine against.

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
- Commit format: `<type>: <summary>`. Milestone v0.4.0. Branch `feature/351-connector-contract-split`.

## Scope

**This plan covers PR 3a only.** Phase 3 (#351) ships as three PRs, and the later two get their own plans once their predecessor lands — 3b's sync engine has to be built against the `ISyncCursorConnector` shape as actually implemented here, not as imagined now.

- **3a (this plan):** interface split, creation restriction, guard deletion, `ISyncCursorConnector` contract.
- **3b:** `SourceSyncService` replacing `ConnectorWatcherService`, wiring `TryAdvanceSyncStateAsync`.
- **3c:** `/api/sources` endpoints and the MCP `kind` discriminator.

Out of scope here: any sync behaviour change, any endpoint for sources, any UI.

## Why the creation restriction belongs in this PR

`ContainerWriteGuard` exists to stop writes to non-managed containers. After Phase 2's backfill, every row in `containers` is `ManagedStorage`, so the guard's checks never fire — *except* that `POST /api/containers` still accepts a `ConnectorType` parameter. Someone can create a new S3 container today.

Deleting the guard without closing that would reopen exactly the hole the guard was added for. The two changes are one change.

## Scoping note: the Filesystem write flags stay for now

The epic calls for removing `FilesystemConnectorConfig`'s `AllowUpload`/`AllowDelete`/`AllowCreateFolder`. That is **deferred to Phase 4 (#352)**, not done here.

Those flags are read in more than ten places in `FileBrowser.razor`, including a settings panel with editable checkboxes for each. Removing them in this PR would drag a substantial UI rework into what is otherwise a backend contract change, and #352 rewrites that component anyway.

Nothing is lost by waiting: this PR removes the *enforcement* path (the guard), and Filesystem containers no longer exist as containers after the Phase 2 backfill, so the flags are already inert — dead config that only drives which buttons render. Record this on #352 so it is not forgotten.

## Deferred findings this PR does NOT address

#351 carries three findings from earlier reviews. Two belong to PR 3b, where the sync engine first produces source-owned writes:

- Propagating the document's `owner_id` into `PgVectorStore` upserts instead of defaulting a missing `containerId` to `Guid.Empty`.
- Deciding whether `chunks.owner_id` needs database-level enforcement against `documents.owner_id` once a second write path exists.

The third — using `TryAdvanceSyncStateAsync` for forward cursor progress and reserving `UpdateSyncStateAsync` for deliberate resets — is also 3b, since nothing calls either method until the sync engine exists.

## File Structure

**Create:**
- `src/Connapse.Core/Interfaces/IWritableConnector.cs` — write surface, implemented only by managed storage.
- `src/Connapse.Core/Interfaces/ISyncCursorConnector.cs` — delta-sync contract plus the `SyncDelta` record.
- `tests/Connapse.Core.Tests/Connectors/ConnectorCapabilityTests.cs` — asserts which connectors expose which capability.

**Modify:**
- `src/Connapse.Core/Interfaces/IConnector.cs` — remove `WriteFileAsync`, `DeleteFileAsync`, `SupportsWrite`.
- `src/Connapse.Storage/Connectors/MinioConnector.cs` — implement `IWritableConnector`.
- `src/Connapse.Storage/Connectors/S3Connector.cs`, `AzureBlobConnector.cs`, `FilesystemConnector.cs` — drop write members.
- `src/Connapse.Web/Services/UploadService.cs:68,92,186` — require `IWritableConnector`; drop guard calls.
- `src/Connapse.Web/Endpoints/DocumentsEndpoints.cs:270`, `FoldersEndpoints.cs:35,96`, `Mcp/McpTools.cs:395` — drop guard calls.
- `src/Connapse.Web/Endpoints/ContainersEndpoints.cs` — reject non-managed `ConnectorType` on create.

**Delete:**
- `src/Connapse.Core/ContainerWriteGuard.cs`
- `tests/Connapse.Core.Tests/ContainerWriteGuardTests.cs`

---

### Task 1: Close container creation to managed storage

**Files:**
- Modify: `src/Connapse.Web/Endpoints/ContainersEndpoints.cs` (the `MapPost("/")` handler)
- Test: `tests/Connapse.Integration.Tests/ContainerCreationRestrictionTests.cs`

**Interfaces:**
- Consumes: `CreateContainerApiRequest(string Name, string? Description, ConnectorType ConnectorType, string? ConnectorConfig)`.
- Produces: `POST /api/containers` returning 400 for any non-managed connector type.

This goes first: it is what makes the guard deletion in Task 3 safe.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Integration.Tests/ContainerCreationRestrictionTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ContainerCreationRestrictionTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string p) => $"{p}-{Guid.NewGuid():N}"[..20];

    [Theory]
    [InlineData(ConnectorType.S3)]
    [InlineData(ConnectorType.AzureBlob)]
    [InlineData(ConnectorType.Filesystem)]
    public async Task CreateContainer_NonManagedConnectorType_Returns400(ConnectorType type)
    {
        // External storage is a source now. Allowing a container of this type would
        // recreate the unwritable-container case that ContainerWriteGuard existed for.
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = ShortName("ext"), ConnectorType = type });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("source");
    }

    [Fact]
    public async Task CreateContainer_ManagedStorage_StillSucceeds()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = ShortName("managed") });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateContainer_ExplicitManagedStorage_StillSucceeds()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = ShortName("managed2"), ConnectorType = ConnectorType.ManagedStorage });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~ContainerCreationRestrictionTests"`
Expected: the three `Returns400` cases FAIL with 201 Created; the two managed cases pass.

- [ ] **Step 3: Add the restriction**

In `src/Connapse.Web/Endpoints/ContainersEndpoints.cs`, at the top of the `MapPost("/")` handler body, before any store call:

```csharp
            // External storage is modelled as a source (#348), not a container. Rejecting
            // it here is what makes ContainerWriteGuard's runtime checks dead code rather
            // than merely unused — without this, a new S3 container would be writable.
            if (request.ConnectorType != ConnectorType.ManagedStorage)
            {
                return Results.BadRequest(new
                {
                    error = $"Containers are managed storage only. To ingest from {request.ConnectorType}, "
                          + "create a connection and a source instead."
                });
            }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~ContainerCreationRestrictionTests"`
Expected: PASS, 5 tests.

- [ ] **Step 5: Check for existing tests that create non-managed containers**

Run: `grep -rn "ConnectorType = ConnectorType\.\(S3\|AzureBlob\|Filesystem\)" tests/ --include=*.cs`

Any test creating such a container **through the API** now expects 400 and must be updated. Tests seeding rows directly via SQL are unaffected — that is how the backfill tests work and they should stay that way.

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Web/Endpoints/ContainersEndpoints.cs tests/Connapse.Integration.Tests/ContainerCreationRestrictionTests.cs
git commit -m "feat(api): reject non-managed connector types on container creation

Part of #351"
```

---

### Task 2: Split the write surface off IConnector

**Files:**
- Create: `src/Connapse.Core/Interfaces/IWritableConnector.cs`
- Modify: `src/Connapse.Core/Interfaces/IConnector.cs`
- Modify: `src/Connapse.Storage/Connectors/MinioConnector.cs`, `S3Connector.cs`, `AzureBlobConnector.cs`, `FilesystemConnector.cs`
- Test: `tests/Connapse.Core.Tests/Connectors/ConnectorCapabilityTests.cs`

**Interfaces:**
- Consumes: existing `IConnector`.
- Produces: `IWritableConnector : IConnector` with `Task WriteFileAsync(string path, Stream content, string? contentType, CancellationToken ct)` and `Task DeleteFileAsync(string path, CancellationToken ct)`. `IConnector` loses those two methods and the `SupportsWrite` flag.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Core.Tests/Connectors/ConnectorCapabilityTests.cs`:

```csharp
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public class ConnectorCapabilityTests
{
    [Fact]
    public void IConnector_HasNoWriteSurface()
    {
        // A source holds an IConnector. If write methods live here, "read-only" is a
        // runtime promise; moving them to IWritableConnector makes it a type guarantee.
        var names = typeof(IConnector).GetMethods().Select(m => m.Name).ToList();

        names.Should().NotContain("WriteFileAsync");
        names.Should().NotContain("DeleteFileAsync");
        typeof(IConnector).GetProperty("SupportsWrite").Should().BeNull();
    }

    [Fact]
    public void MinioConnector_IsWritable()
    {
        typeof(IWritableConnector).IsAssignableFrom(typeof(MinioConnector))
            .Should().BeTrue("managed storage is the only writable backend");
    }

    [Theory]
    [InlineData(typeof(S3Connector))]
    [InlineData(typeof(AzureBlobConnector))]
    [InlineData(typeof(FilesystemConnector))]
    public void ExternalConnectors_AreNotWritable(Type connectorType)
    {
        typeof(IWritableConnector).IsAssignableFrom(connectorType)
            .Should().BeFalse("external storage is mirrored, never mutated through Connapse");
    }

}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~ConnectorCapabilityTests"`
Expected: FAIL — `IWritableConnector` does not exist.

- [ ] **Step 3: Create the writable interface**

Create `src/Connapse.Core/Interfaces/IWritableConnector.cs`:

```csharp
namespace Connapse.Core.Interfaces;

/// <summary>
/// A connector whose backing store Connapse owns and may mutate. Implemented only by
/// managed storage. External connectors deliberately do not implement this: a source
/// mirrors someone else's system, so "read-only" is enforced by the type rather than
/// by a runtime check that every call site has to remember to make.
/// </summary>
public interface IWritableConnector : IConnector
{
    Task WriteFileAsync(string path, Stream content, string? contentType = null, CancellationToken ct = default);
    Task DeleteFileAsync(string path, CancellationToken ct = default);
}
```

- [ ] **Step 4: Trim IConnector**

In `src/Connapse.Core/Interfaces/IConnector.cs`, delete the `SupportsWrite` property and the `WriteFileAsync` and `DeleteFileAsync` declarations, leaving:

```csharp
namespace Connapse.Core.Interfaces;

public interface IConnector
{
    ConnectorType Type { get; }
    bool SupportsLiveWatch { get; }

    Task<Stream> ReadFileAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default);
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Resolves a virtual/relative path to the actual job path for the ingestion queue.
    /// Filesystem connectors return OS-native absolute paths; cloud connectors return virtual paths.
    /// </summary>
    string ResolveJobPath(string relativePath);

    // Throws NotSupportedException if SupportsLiveWatch is false
    IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default);
}
```

- [ ] **Step 5: Update the connector implementations**

In `MinioConnector.cs`, change the declaration to `public class MinioConnector : IWritableConnector, IDisposable` and delete its `SupportsWrite` property. Keep `WriteFileAsync` and `DeleteFileAsync` as they are.

In `S3Connector.cs`, `AzureBlobConnector.cs`, and `FilesystemConnector.cs`: delete the `SupportsWrite` property and the entire `WriteFileAsync` and `DeleteFileAsync` method bodies.

**Leave `FilesystemConnectorConfig`'s `AllowUpload`/`AllowDelete`/`AllowCreateFolder` alone** — see the scoping note below.

- [ ] **Step 6: Fix the resulting compilation errors**

Run: `dotnet build`

Expected failures and their fixes:
- `UploadService.cs:186` calls `connector.WriteFileAsync(...)`. This is the only `WriteFileAsync` caller in the codebase. Change the local to `IWritableConnector` — see Task 3, which rewrites this method anyway.
- Any test double implementing `IConnector` with write members: remove them, or implement `IWritableConnector` if the test needs writes.

Re-run `dotnet build` until it succeeds.

- [ ] **Step 7: Run the capability tests**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~ConnectorCapabilityTests"`
Expected: PASS, 6 tests.

- [ ] **Step 8: Commit**

```bash
git add src/Connapse.Core/Interfaces/ src/Connapse.Storage/Connectors/ tests/Connapse.Core.Tests/Connectors/ConnectorCapabilityTests.cs
git commit -m "refactor(core): move write methods to IWritableConnector

Part of #351"
```

---

### Task 3: Delete ContainerWriteGuard

**Files:**
- Delete: `src/Connapse.Core/ContainerWriteGuard.cs`, `tests/Connapse.Core.Tests/ContainerWriteGuardTests.cs`
- Modify: `src/Connapse.Web/Services/UploadService.cs` (lines 68, 92, 186)
- Modify: `src/Connapse.Web/Endpoints/DocumentsEndpoints.cs:270`, `FoldersEndpoints.cs:35,96`, `Mcp/McpTools.cs:395`

**Interfaces:**
- Consumes: `IWritableConnector` from Task 2; the creation restriction from Task 1.
- Produces: no guard. Write paths obtain an `IWritableConnector` or fail at resolution.

- [ ] **Step 1: Confirm the guard is genuinely dead**

Run: `grep -rn "ContainerWriteGuard\." src/ --include=*.cs`
Expected: exactly six call sites — `DocumentsEndpoints.cs:270`, `FoldersEndpoints.cs:35`, `FoldersEndpoints.cs:96`, `McpTools.cs:395`, `UploadService.cs:68`, `UploadService.cs:92`.

Every one of these runs only after loading a `Container`. Post-backfill every container row is `ManagedStorage`, and Task 1 stops new non-managed ones being created, so `CheckWrite` can only return null. That is the argument for deleting rather than replacing it.

- [ ] **Step 2: Remove the guard calls**

At `DocumentsEndpoints.cs:270`, `FoldersEndpoints.cs:35`, and `FoldersEndpoints.cs:96`, delete the `ContainerWriteGuard.CheckWrite(...)` call and the `if (error is not null) return ...` block that follows it.

At `McpTools.cs:395`, delete the same pair.

In `UploadService.cs`, delete both guard calls (lines 68 and 92) and change the connector resolution so the write path demands a writable connector:

```csharp
        // Managed storage is the only writable backend, so this cast is the write
        // permission check. It cannot fail for a container: creation is restricted to
        // ManagedStorage and the backfill moved every external one to a source.
        if (connectorFactory.Create(container) is not IWritableConnector writable)
        {
            throw new InvalidOperationException(
                $"Container {container.Id} resolves to a non-writable connector; uploads require managed storage.");
        }
```

Then at the former line 186, call `writable.WriteFileAsync(relativePath, request.Content, contentType, ct)`.

- [ ] **Step 3: Delete the guard and its tests**

```bash
git rm src/Connapse.Core/ContainerWriteGuard.cs tests/Connapse.Core.Tests/ContainerWriteGuardTests.cs
```

- [ ] **Step 4: Build and fix fallout**

Run: `dotnet build`
Expected: errors only where `WriteOperation` or `ContainerWriteGuard` were still referenced. Remove those usings and references. `WriteOperation` lived in the same file and goes with it.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: all tests pass. Watch for upload and folder tests that asserted a 403/400 for read-only containers — those scenarios are now unreachable through the API and their tests should be deleted, not weakened. If a test creates an S3 container via the API and expects an upload rejection, it is superseded by `ContainerCreationRestrictionTests`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(core): delete ContainerWriteGuard

Every container is managed storage after the backfill, and creation now rejects
other connector types, so the runtime checks were unreachable. Write access is a
type guarantee via IWritableConnector instead.

Part of #351"
```

---

### Task 4: Add the cursor-sync contract

**Files:**
- Create: `src/Connapse.Core/Interfaces/ISyncCursorConnector.cs`
- Test: `tests/Connapse.Core.Tests/Connectors/SyncDeltaTests.cs`

**Interfaces:**
- Consumes: `IConnector`, `ConnectorFile`.
- Produces: `ISyncCursorConnector : IConnector` with `Task<SyncDelta> GetChangesAsync(string? cursor, CancellationToken ct)`, and `SyncDelta(IReadOnlyList<ConnectorFile> Upserted, IReadOnlyList<string> DeletedPaths, string? NextCursor, bool RequiresFullResync)`. PR 3b's `SourceSyncService` consumes this.

Nothing implements it in this PR — S3 and Azure Blob have no delta API and stay on the list-and-diff fallback. It lands here so 3b has a stable contract.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Core.Tests/Connectors/SyncDeltaTests.cs`:

```csharp
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public class SyncDeltaTests
{
    [Fact]
    public void SyncDelta_Empty_IsNotAResyncRequest()
    {
        var delta = new SyncDelta([], [], "cursor-1", RequiresFullResync: false);

        delta.Upserted.Should().BeEmpty();
        delta.DeletedPaths.Should().BeEmpty();
        delta.NextCursor.Should().Be("cursor-1");
        delta.RequiresFullResync.Should().BeFalse();
    }

    [Fact]
    public void SyncDelta_Resync_CarriesNoCursor()
    {
        // Graph answers a stale delta token with HTTP 410 Gone and Dropbox with a 409
        // reset. Both mean "start over": the caller must clear the stored cursor, so a
        // resync response must not also hand back a cursor to persist.
        var delta = new SyncDelta([], [], NextCursor: null, RequiresFullResync: true);

        delta.RequiresFullResync.Should().BeTrue();
        delta.NextCursor.Should().BeNull();
    }

    [Fact]
    public void ISyncCursorConnector_ExtendsIConnector()
    {
        typeof(IConnector).IsAssignableFrom(typeof(ISyncCursorConnector)).Should().BeTrue();
    }

    [Fact]
    public void ISyncCursorConnector_IsOptional()
    {
        // Connectors without a delta API must not be forced to implement it; the sync
        // engine falls back to list-and-diff for those.
        typeof(ISyncCursorConnector).IsAssignableFrom(typeof(Connapse.Storage.Connectors.S3Connector))
            .Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~SyncDeltaTests"`
Expected: FAIL — `SyncDelta` and `ISyncCursorConnector` do not exist.

- [ ] **Step 3: Write the contract**

Create `src/Connapse.Core/Interfaces/ISyncCursorConnector.cs`:

```csharp
namespace Connapse.Core.Interfaces;

/// <summary>
/// The result of one incremental sync call.
/// <para>
/// <paramref name="RequiresFullResync"/> is not an error signal. The strongest provider
/// APIs answer a stale delta token with an explicit "start over" — Microsoft Graph returns
/// HTTP 410 Gone, Dropbox a 409 reset — and ignoring that produces a corpus that silently
/// drifts out of date. When it is set, the caller must clear the stored cursor and re-list
/// from scratch, which is why a resync response carries no NextCursor.
/// </para>
/// </summary>
public record SyncDelta(
    IReadOnlyList<ConnectorFile> Upserted,
    IReadOnlyList<string> DeletedPaths,
    string? NextCursor,
    bool RequiresFullResync);

/// <summary>
/// A connector that can report what changed since a durable cursor, rather than requiring
/// the whole remote corpus to be listed and diffed on every poll.
/// <para>
/// Deliberately optional. S3 and Azure Blob have no delta API and do not implement it; the
/// sync engine falls back to list-and-diff for those. Implement it only where the provider
/// genuinely offers one.
/// </para>
/// </summary>
public interface ISyncCursorConnector : IConnector
{
    /// <summary>
    /// Returns changes since <paramref name="cursor"/>, or the initial set when it is null.
    /// </summary>
    Task<SyncDelta> GetChangesAsync(string? cursor, CancellationToken ct = default);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~SyncDeltaTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Core/Interfaces/ISyncCursorConnector.cs tests/Connapse.Core.Tests/Connectors/SyncDeltaTests.cs
git commit -m "feat(core): add ISyncCursorConnector contract for delta-based sync

Part of #351"
```

---

## PR 3a done-condition

`IConnector` exposes no write surface; only `MinioConnector` implements `IWritableConnector`; `POST /api/containers` rejects every non-managed connector type; `ContainerWriteGuard` and its tests no longer exist; `ISyncCursorConnector` is available for PR 3b; and `dotnet test` passes in full.

The Filesystem write flags are deliberately still present — they become dead UI config here and are removed in #352.

## Note for PR 3b

Write 3b's plan only after this lands. Its three carried findings — owner-ID propagation into vector upserts, `chunks.owner_id` enforcement, and using `TryAdvanceSyncStateAsync` for forward progress while reserving `UpdateSyncStateAsync` for resets — all depend on how `SourceSyncService` ends up structured, and that is a decision this PR does not make.
