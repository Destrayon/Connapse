# Delete Guard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop a source's index being wiped when its remote listing comes back empty-but-successful, while letting an admin approve a genuine bulk deletion in one action.

**Architecture:** A pure predicate decides whether a computed deletion set is too large to trust. When it trips, `SourceSyncService` applies the upserts, skips the deletions, and records how many were withheld on the source. The Sources page surfaces that count with an admin-only button that re-runs the sync with the guard lifted — recomputing the vanished set rather than replaying a stored one, so a recovered mount deletes nothing.

**Tech Stack:** .NET 10, EF Core + PostgreSQL, Blazor Server, xUnit + FluentAssertions + NSubstitute, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-08-23-sftp-ingestion-and-delete-guard-design.md`

## Global Constraints

- .NET 10, file-scoped namespaces, nullable enabled, implicit usings.
- Records for DTOs; primary constructors for DI; async all the way (never `.Result`/`.Wait()`).
- Never use `var` for primitive types.
- Parameterized SQL only — never string interpolation.
- Wrap user-controlled values in `LogSanitizer.Sanitize(...)` from `Connapse.Core.Utilities` before logging.
- Always use `IDbContextFactory<KnowledgeDbContext>` and short-lived contexts: `await using var ctx = await factory.CreateDbContextAsync(ct)`.
- Tag every test `[Trait("Category", "Unit")]` or `[Trait("Category", "Integration")]`.
- Test naming: `MethodName_Scenario_ExpectedResult`.
- Commit style: `<type>: <summary>` — `feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `perf:`, `chore:`.
- Branch: `feature/<issue>-desc`. File the GitHub issue **before** creating the branch.
- **The threshold is fixed and not configurable.** Do not add settings, options, or per-source config for it.
- **`SyncStatus` must not gain a member.** Withheld is a count, not a status.

---

## File Structure

**Create:**
- `src/Connapse.Core/Utilities/DeletionGuard.cs` — the predicate. Core has zero dependencies, and this is pure logic with no I/O.
- `src/Connapse.Storage/Migrations/<timestamp>_AddSourceWithheldDeletions.cs` — generated.
- `tests/Connapse.Core.Tests/Sources/DeletionGuardTests.cs`
- `tests/Connapse.Integration.Tests/DeleteGuardIntegrationTests.cs`

**Modify:**
- `src/Connapse.Core/Models/SourceModels.cs` — add `WithheldDeletions` to `Source`.
- `src/Connapse.Storage/Data/Entities/SourceEntity.cs` — add `WithheldDeletions`.
- `src/Connapse.Storage/Data/KnowledgeDbContext.cs` — map the column (block starts line 494).
- `src/Connapse.Storage/Sources/PostgresSourceStore.cs` — map it in `MapToModel`; add `UpdateWithheldDeletionsAsync`.
- `src/Connapse.Core/Interfaces/ISourceStore.cs` — declare `UpdateWithheldDeletionsAsync`.
- `src/Connapse.Core/Models/SyncModels.cs` — add `WithheldDeletions` to `SourceSyncResult`.
- `src/Connapse.Web/Services/SourceSyncService.cs` — apply the guard in `SyncViaListAndDiffAsync`; thread an override flag.
- `src/Connapse.Web/Endpoints/SourceResponse.cs` — expose the count.
- `src/Connapse.Web/Endpoints/SourcesEndpoints.cs` — accept the override on the sync route.
- `src/Connapse.Web/Components/Pages/Sources.razor` — surface the count and the button.

---

### Task 1: The deletion guard predicate

**Files:**
- Create: `src/Connapse.Core/Utilities/DeletionGuard.cs`
- Test: `tests/Connapse.Core.Tests/Sources/DeletionGuardTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static class DeletionGuard` with `public static bool ShouldWithhold(int vanished, int indexed)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Connapse.Core.Tests/Sources/DeletionGuardTests.cs`:

```csharp
using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Sources;

/// <summary>
/// The boundary this guard draws is the whole of its behaviour, so it is pinned by example
/// rather than described. Both terms of the rule are load-bearing: a percentage alone fires
/// constantly on small sources, an absolute alone is meaningless on large ones.
/// </summary>
[Trait("Category", "Unit")]
public class DeletionGuardTests
{
    [Theory]
    // Small sources clean themselves up: re-ingesting five files is cheap, and blocking
    // them would make the guard fire on ordinary tidying.
    [InlineData(5, 5, false)]
    [InlineData(10, 10, false)]
    // Losing everything from a source big enough to notice is suspicious.
    [InlineData(15, 15, true)]
    // Proportionality on large sources: 5% is plausible churn, 50% is not.
    [InlineData(5_000, 100_000, false)]
    [InlineData(50_000, 100_000, true)]
    // Exactly at each bound, so an off-by-one in either term is caught.
    [InlineData(10, 100, false)]
    [InlineData(11, 100, true)]
    // Nothing to delete is never withheld, including on an empty index.
    [InlineData(0, 0, false)]
    [InlineData(0, 100, false)]
    public void ShouldWithhold_AtTheBoundaries_MatchesTheRule(int vanished, int indexed, bool expected)
    {
        DeletionGuard.ShouldWithhold(vanished, indexed).Should().Be(expected);
    }

    [Fact]
    public void ShouldWithhold_MoreVanishedThanIndexed_DoesNotThrow()
    {
        // Not reachable through SyncViaListAndDiffAsync, which derives vanished from the
        // indexed set — but a predicate that throws on nonsense input would turn a caller
        // bug into a failed sync rather than a logged oddity.
        var act = () => DeletionGuard.ShouldWithhold(200, 100);

        act.Should().NotThrow();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~DeletionGuardTests"`
Expected: FAIL — `DeletionGuard` does not exist (compile error `CS0103`).

- [ ] **Step 3: Write the implementation**

Create `src/Connapse.Core/Utilities/DeletionGuard.cs`:

```csharp
namespace Connapse.Core.Utilities;

/// <summary>
/// Decides whether a computed deletion set is too large to trust.
/// <para>
/// A reconcile infers deletions from absence: anything indexed but missing from the remote
/// listing is assumed deleted. That inference is only as good as the listing, and a listing
/// can come back empty <em>and successful</em> — a narrowed bucket policy returning 200 OK
/// with zero keys, or a filesystem directory that is temporarily unmounted. Without this
/// check, one such listing deletes every document the source owns.
/// </para>
/// <para>
/// The rule deliberately does not try to decide whether a deletion is <em>correct</em>. For a
/// mirror, a wrong deletion is recoverable — the next sync re-ingests it — so what needs
/// preventing is the catastrophic case, not every false positive.
/// </para>
/// </summary>
public static class DeletionGuard
{
    /// <summary>Deletion sets at or below this size are always applied.</summary>
    /// <remarks>
    /// A floor rather than a pure percentage: on a five-document source, deleting three is
    /// 60% and would trip a percentage rule on completely ordinary tidying.
    /// </remarks>
    public const int AlwaysAllowedCount = 10;

    /// <summary>Proportion of a source's index above which a deletion set is withheld.</summary>
    /// <remarks>
    /// A ceiling rather than a pure count: ten documents out of a hundred thousand is noise,
    /// and an absolute-only rule would block routine churn on any large source.
    /// </remarks>
    public const int WithheldPercent = 10;

    /// <summary>
    /// True when the deletion set should be withheld pending an administrator's approval.
    /// Requires <em>both</em> bounds to be exceeded, so neither degenerate case fires.
    /// </summary>
    public static bool ShouldWithhold(int vanished, int indexed) =>
        vanished > AlwaysAllowedCount && vanished > indexed / (100 / WithheldPercent);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~DeletionGuardTests"`
Expected: PASS — 10 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Utilities/DeletionGuard.cs tests/Connapse.Core.Tests/Sources/DeletionGuardTests.cs
git commit -m "feat(core): add the deletion guard predicate"
```

---

### Task 2: Persist the withheld count

**Files:**
- Modify: `src/Connapse.Storage/Data/Entities/SourceEntity.cs`
- Modify: `src/Connapse.Storage/Data/KnowledgeDbContext.cs` (SourceEntity block, from line 494)
- Modify: `src/Connapse.Core/Models/SourceModels.cs`
- Modify: `src/Connapse.Storage/Sources/PostgresSourceStore.cs`
- Modify: `src/Connapse.Core/Interfaces/ISourceStore.cs`
- Create: migration (generated)
- Test: `tests/Connapse.Integration.Tests/DeleteGuardIntegrationTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `Source.WithheldDeletions` (`int?`); `ISourceStore.UpdateWithheldDeletionsAsync(Guid id, int? withheld, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Integration.Tests/DeleteGuardIntegrationTests.cs`:

```csharp
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class DeleteGuardIntegrationTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<Source> SeedSourceAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();

        var connection = await connections.CreateAsync(
            new CreateConnectionRequest(ShortName("conn"), ConnectionProvider.S3, """{"region":"us-east-1"}"""),
            createdByUserId: null);

        return await sources.CreateAsync(
            new CreateSourceRequest(ShortName("src"), connection.Id, """{"bucketName":"b"}"""));
    }

    [Fact]
    public async Task UpdateWithheldDeletionsAsync_RoundTripsTheCount()
    {
        var source = await SeedSourceAsync();

        using var scope = fixture.Factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();

        (await sources.GetAsync(source.Id))!.WithheldDeletions
            .Should().BeNull("a source with nothing pending must not claim a count of zero");

        await sources.UpdateWithheldDeletionsAsync(source.Id, 42);
        (await sources.GetAsync(source.Id))!.WithheldDeletions.Should().Be(42);

        // Clearing must return to null, not zero: the UI distinguishes "nothing pending"
        // from "a decision was made", and zero would leave the button showing forever.
        await sources.UpdateWithheldDeletionsAsync(source.Id, null);
        (await sources.GetAsync(source.Id))!.WithheldDeletions.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~UpdateWithheldDeletionsAsync_RoundTripsTheCount"`
Expected: FAIL — compile error, `Source` has no `WithheldDeletions` and `ISourceStore` has no `UpdateWithheldDeletionsAsync`.

- [ ] **Step 3: Add the field to the entity**

In `src/Connapse.Storage/Data/Entities/SourceEntity.cs`, after the `SyncIntervalSeconds` line in the `// Sync state` block:

```csharp
    /// <summary>
    /// How many deletions the last reconcile declined to apply, or null when none are pending.
    /// Null rather than zero: the Sources page distinguishes "nothing pending" from "an
    /// administrator decided", and zero would leave the approval button showing forever.
    /// </summary>
    public int? WithheldDeletions { get; set; }
```

- [ ] **Step 4: Map the column**

In `src/Connapse.Storage/Data/KnowledgeDbContext.cs`, inside the `Entity<SourceEntity>` block, after the `SyncIntervalSeconds` property mapping:

```csharp
            entity.Property(e => e.WithheldDeletions)
                .HasColumnName("withheld_deletions");
```

- [ ] **Step 5: Add the field to the domain record**

In `src/Connapse.Core/Models/SourceModels.cs`, add a parameter to `Source` **at the end of the parameter list**, after `int DocumentCount = 0`:

```csharp
    int DocumentCount = 0,
    int? WithheldDeletions = null);
```

Appending rather than inserting: every positional construction of `Source` in the codebase and tests would otherwise shift.

- [ ] **Step 6: Map it in the store and add the update method**

In `src/Connapse.Storage/Sources/PostgresSourceStore.cs`, in `MapToModel`, after `DocumentCount: documentCount`:

```csharp
        DocumentCount: documentCount,
        WithheldDeletions: entity.WithheldDeletions);
```

Then add the method to the same class:

```csharp
    public async Task UpdateWithheldDeletionsAsync(Guid id, int? withheld, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Sources.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return;

        entity.WithheldDeletions = withheld;
        entity.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);
    }
```

- [ ] **Step 7: Declare it on the interface**

In `src/Connapse.Core/Interfaces/ISourceStore.cs`, next to the other sync-state methods:

```csharp
    /// <summary>
    /// Records how many deletions the last reconcile declined to apply, or null to clear.
    /// Separate from <see cref="UpdateSyncStateAsync"/> because a sync that withholds
    /// deletions still succeeded — the count is orthogonal to the status, not a variant of it.
    /// </summary>
    Task UpdateWithheldDeletionsAsync(Guid id, int? withheld, CancellationToken ct = default);
```

- [ ] **Step 8: Generate the migration**

```bash
dotnet ef migrations add AddSourceWithheldDeletions --project src/Connapse.Storage --startup-project src/Connapse.Web
```

Open the generated `.cs` and confirm it contains exactly one `AddColumn<int>` for `withheld_deletions` on `sources`, nullable, with no default. If it contains anything else, the model has drifted — stop and investigate rather than editing the migration.

Then check `KnowledgeDbContextModelSnapshot.cs`: if `ProductVersion` changed, revert that one line. It reflects the scaffolding tool's runtime rather than the project's EF Core version, and flip-flops between contributors.

- [ ] **Step 9: Run the test to verify it passes**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~UpdateWithheldDeletionsAsync_RoundTripsTheCount"`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/Connapse.Core/Models/SourceModels.cs src/Connapse.Core/Interfaces/ISourceStore.cs src/Connapse.Storage/Data/Entities/SourceEntity.cs src/Connapse.Storage/Data/KnowledgeDbContext.cs src/Connapse.Storage/Sources/PostgresSourceStore.cs src/Connapse.Storage/Migrations/ tests/Connapse.Integration.Tests/DeleteGuardIntegrationTests.cs
git commit -m "feat(storage): persist a source's withheld deletion count"
```

---

### Task 3: Apply the guard during sync

**Files:**
- Modify: `src/Connapse.Core/Models/SyncModels.cs`
- Modify: `src/Connapse.Web/Services/SourceSyncService.cs` (`SyncViaListAndDiffAsync`, from line 206)
- Test: `tests/Connapse.Integration.Tests/DeleteGuardIntegrationTests.cs`

**Interfaces:**
- Consumes: `DeletionGuard.ShouldWithhold(int, int)` (Task 1); `ISourceStore.UpdateWithheldDeletionsAsync` (Task 2).
- Produces: `SourceSyncResult.WithheldDeletions` (`int`); `SourceSyncService.SyncSourceAsync(Source, Connection, CancellationToken, bool applyWithheldDeletions = false)`.

- [ ] **Step 1: Write the failing test**

Append to `tests/Connapse.Integration.Tests/DeleteGuardIntegrationTests.cs`. This is the regression test for the bug — it must fail against current `main`.

```csharp
    /// <summary>
    /// A connector whose listing is empty, without erroring — the shape a narrowed bucket
    /// policy or an unmounted directory actually takes. Before the guard this deleted every
    /// document the source owned.
    /// </summary>
    private sealed class EmptyListingConnector : IConnector
    {
        public ConnectorType Type => ConnectorType.S3;
        public bool SupportsLiveWatch => false;
        public string ResolveJobPath(string relativePath) => "/" + relativePath.TrimStart('/');
        public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConnectorFile>>([]);
        public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(false);
        public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task Sync_ListingCollapsesToEmpty_WithholdsDeletionsAndKeepsDocuments()
    {
        var source = await SeedSourceAsync();
        await SeedDocumentsAsync(source.Id, count: 40);

        var result = await SyncWithConnectorAsync(source, new EmptyListingConnector());

        result.Deleted.Should().Be(0, "an empty listing is not evidence that 40 files were deleted");
        result.WithheldDeletions.Should().Be(40);

        using var scope = fixture.Factory.Services.CreateScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        (await documents.ListAsync(source.Id, take: int.MaxValue)).Should().HaveCount(40);

        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        (await sources.GetAsync(source.Id))!.WithheldDeletions.Should().Be(40);
    }

    [Fact]
    public async Task Sync_WithOverride_AppliesTheWithheldDeletions()
    {
        var source = await SeedSourceAsync();
        await SeedDocumentsAsync(source.Id, count: 40);

        await SyncWithConnectorAsync(source, new EmptyListingConnector());
        var result = await SyncWithConnectorAsync(source, new EmptyListingConnector(), applyWithheldDeletions: true);

        result.Deleted.Should().Be(40);
        result.WithheldDeletions.Should().Be(0);

        using var scope = fixture.Factory.Services.CreateScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        (await documents.ListAsync(source.Id, take: int.MaxValue)).Should().BeEmpty();

        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        (await sources.GetAsync(source.Id))!.WithheldDeletions
            .Should().BeNull("the pending decision is resolved, so the button must stop showing");
    }

    [Fact]
    public async Task Sync_SmallDeletionSet_AppliesWithoutWithholding()
    {
        // Five of five is below the floor: small sources must stay able to tidy themselves.
        var source = await SeedSourceAsync();
        await SeedDocumentsAsync(source.Id, count: 5);

        var result = await SyncWithConnectorAsync(source, new EmptyListingConnector());

        result.Deleted.Should().Be(5);
        result.WithheldDeletions.Should().Be(0);
    }
```

You will also need two helpers in this class. `SeedDocumentsAsync(Guid sourceId, int count)` inserts `count` source-owned documents — follow the raw-SQL insert pattern in `tests/Connapse.Integration.Tests/OwnerBridgeSchemaTests.cs`, setting `source_id` and leaving `container_id` NULL. `SyncWithConnectorAsync(Source, IConnector, bool applyWithheldDeletions = false)` builds a `SourceSyncService` with a fixed-connector factory — copy `FixedConnectorFactory` from `tests/Connapse.Integration.Tests/SourceSyncIntegrationTests.cs`, which already exists for exactly this purpose.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~DeleteGuardIntegrationTests"`
Expected: FAIL — `SourceSyncResult` has no `WithheldDeletions`, and `SyncSourceAsync` has no `applyWithheldDeletions` parameter.

- [ ] **Step 3: Add the field to the result record**

In `src/Connapse.Core/Models/SyncModels.cs`:

```csharp
public record SourceSyncResult(
    int Upserted,
    int Deleted,
    bool UsedDeltaPath,
    bool RequiredResync,
    string? Error,
    bool AlreadyRunning = false,
    int WithheldDeletions = 0);
```

- [ ] **Step 4: Apply the guard in the reconcile**

In `src/Connapse.Web/Services/SourceSyncService.cs`, replace the body of `SyncViaListAndDiffAsync` between computing `vanished` and returning:

```csharp
        var remotePaths = remote.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var vanished = indexedPaths.Where(p => !remotePaths.Contains(p)).ToList();

        // Upserts apply regardless. A source that trips the guard must keep ingesting new
        // content, or the safety mechanism becomes the outage it exists to prevent.
        int upserted = await EnqueueAllAsync(source, remote, context, sp, ct);

        bool withhold = !applyWithheldDeletions
            && DeletionGuard.ShouldWithhold(vanished.Count, indexedPaths.Count);

        int deleted = 0;
        if (withhold)
        {
            logger.LogWarning(
                "Source {SourceId} reconcile would delete {Vanished} of {Indexed} document(s); "
                + "withholding pending administrator approval",
                source.Id, vanished.Count, indexedPaths.Count);
        }
        else
        {
            deleted = await DeleteByPathsAsync(source, vanished, context, sp, ct);
        }

        await sourceStore.UpdateWithheldDeletionsAsync(
            source.Id, withhold ? vanished.Count : null, ct);

        await sourceStore.UpdateSyncStateAsync(
            source.Id, source.SyncCursor, SyncStatus.Succeeded, error: null, DateTime.UtcNow, ct);

        return new SourceSyncResult(
            upserted, deleted, UsedDeltaPath: false, RequiredResync: false, Error: null,
            WithheldDeletions: withhold ? vanished.Count : 0);
```

Add `using Connapse.Core.Utilities;` if it is not already present.

- [ ] **Step 5: Thread the override parameter**

Add `bool applyWithheldDeletions = false` as the last parameter of `SyncViaListAndDiffAsync` and of the public `SyncSourceAsync`, passing it through. Do **not** add it to the delta path — the guard does not apply there.

Add this comment above the public method's new parameter:

```csharp
    /// <param name="applyWithheldDeletions">
    /// Lifts the deletion guard for this one cycle. The vanished set is recomputed rather
    /// than replayed from what was withheld earlier, so a source whose remote has recovered
    /// in the meantime deletes nothing.
    /// </param>
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~DeleteGuardIntegrationTests"`
Expected: PASS — 4 passed.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test`
Expected: all pass. `SourceSyncIntegrationTests` exercises the same method and must not regress.

- [ ] **Step 8: Commit**

```bash
git add src/Connapse.Core/Models/SyncModels.cs src/Connapse.Web/Services/SourceSyncService.cs tests/Connapse.Integration.Tests/DeleteGuardIntegrationTests.cs
git commit -m "fix(web): withhold implausibly large deletion sets during source sync"
```

---

### Task 4: Expose the count and the override over REST

**Files:**
- Modify: `src/Connapse.Web/Endpoints/SourceResponse.cs`
- Modify: `src/Connapse.Web/Endpoints/SourcesEndpoints.cs` (sync route, from line 190)
- Test: `tests/Connapse.Integration.Tests/DeleteGuardIntegrationTests.cs`

**Interfaces:**
- Consumes: `Source.WithheldDeletions` (Task 2); the `applyWithheldDeletions` parameter (Task 3).
- Produces: `SourceResponse.WithheldDeletions`; `POST /api/sources/{id}/sync?applyWithheldDeletions=true`.

- [ ] **Step 1: Write the failing test**

Append to `DeleteGuardIntegrationTests.cs`:

```csharp
    [Fact]
    public async Task GetSource_AfterWithholding_ReportsTheCount()
    {
        var source = await SeedSourceAsync();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
            await sources.UpdateWithheldDeletionsAsync(source.Id, 40);
        }

        var response = await fixture.AdminClient.GetAsync($"/api/sources/{source.Id}");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("withheldDeletions").GetInt32().Should().Be(40);
    }

    [Fact]
    public async Task GetSource_WithNothingWithheld_ReportsNull()
    {
        // Null rather than 0, because the page keys the approval button off "is there a
        // pending decision" — a zero would leave it showing forever.
        var source = await SeedSourceAsync();

        var response = await fixture.AdminClient.GetAsync($"/api/sources/{source.Id}");
        var body = await response.Content.ReadAsStringAsync();

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("withheldDeletions").ValueKind
            .Should().Be(System.Text.Json.JsonValueKind.Null);
    }
```

**Note on what these tests do not cover.** `SharedWebAppFixture` exposes only `AdminClient`, so there is no way here to assert that a viewer sees `withheldDeletions` or that a non-admin is refused the override. Both properties hold by construction — the field is mapped unconditionally in `SourceResponse.From` rather than behind `includeDiagnostics`, and the sync route already carries `.RequireAuthorization("RequireAdmin")` — but neither is pinned by a test. Adding a viewer client to the shared fixture is worth doing and is deliberately **not** part of this plan; do not expand scope to chase it.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~GetSource_AfterWithholding_ReportsTheCount"`
Expected: FAIL — `withheldDeletions` is not in the response body.

- [ ] **Step 3: Add the field to the DTO**

In `src/Connapse.Web/Endpoints/SourceResponse.cs`, add a parameter before `string Kind = "source"`:

```csharp
    /// <summary>
    /// How many deletions the last reconcile declined to apply, or null when none are pending.
    /// Unlike <see cref="LastSyncError"/> this is not administrator-only: it is a count, and
    /// names nothing about the remote's contents or structure.
    /// </summary>
    int? WithheldDeletions,
```

And in `From`, before `LastSyncError`:

```csharp
        WithheldDeletions: source.WithheldDeletions,
```

- [ ] **Step 4: Accept the override on the sync route**

In `src/Connapse.Web/Endpoints/SourcesEndpoints.cs`, add to the sync route's parameters:

```csharp
            [FromQuery] bool applyWithheldDeletions,
```

Pass it to `SyncSourceAsync`, and make the audit entry distinguish the two cases:

```csharp
            await auditLogger.LogAsync(
                applyWithheldDeletions ? "source.deletions_applied" : "source.synced",
                "source", source.Id.ToString(),
                new { source.Name, result.Upserted, result.Deleted, result.WithheldDeletions }, ct);
```

A separate action name so "an administrator approved 40 deletions" is findable in the audit log rather than indistinguishable from an ordinary sync.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~DeleteGuardIntegrationTests"`
Expected: PASS — 6 passed (four from Task 3, two here).

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Web/Endpoints/SourceResponse.cs src/Connapse.Web/Endpoints/SourcesEndpoints.cs tests/Connapse.Integration.Tests/DeleteGuardIntegrationTests.cs
git commit -m "feat(api): expose withheld deletions and an admin override on source sync"
```

---

### Task 5: Surface it on the Sources page

**Files:**
- Modify: `src/Connapse.Web/Components/Pages/Sources.razor`

**Interfaces:**
- Consumes: `Source.WithheldDeletions` (Task 2); `SyncSourceAsync(..., applyWithheldDeletions)` (Task 3).
- Produces: nothing consumed by later tasks.

There is no Blazor test harness in this repository, so this task has no automated test. Keep the logic in the page trivial — a conditional row and a call — and rely on Tasks 1–4 for correctness.

- [ ] **Step 1: Add the pending-deletions row**

In `src/Connapse.Web/Components/Pages/Sources.razor`, after the existing `LastSyncStatus == SyncStatus.Failed` row inside the `@foreach`, add:

```razor
                        @if (source.WithheldDeletions is { } withheld && isAdmin)
                        {
                            <tr class="@(source.Enabled ? "" : "opacity-50")">
                                @* Administrators only, matching the sync-error row: the count
                                   is harmless, but the action it offers is destructive. *@
                                <td colspan="7" class="pt-0 border-top-0">
                                    <div class="alert alert-warning py-2 mb-0 small d-flex align-items-center justify-content-between">
                                        <span>
                                            <span class="bi-exclamation-triangle me-1"></span>
                                            The last sync would have deleted <strong>@withheld</strong> document(s) —
                                            more than expected, so they were kept. If the source really did lose them, apply the deletions.
                                        </span>
                                        <button class="btn btn-sm btn-outline-danger ms-3 text-nowrap"
                                                @onclick="() => SyncNow(source, applyWithheldDeletions: true)"
                                                disabled="@(syncing == source.Id)">
                                            Apply deletions
                                        </button>
                                    </div>
                                </td>
                            </tr>
                        }
```

- [ ] **Step 2: Thread the flag through the existing handler**

Change `SyncNow`'s signature to `private async Task SyncNow(Source source, bool applyWithheldDeletions = false)`, pass the flag to `SyncService.SyncSourceAsync`, and change the success message so the two cases are distinguishable:

```csharp
                await AuditLogger.LogAsync(
                    applyWithheldDeletions ? "source.deletions_applied" : "source.synced",
                    "source", source.Id.ToString(),
                    new { source.Name, result.Upserted, result.Deleted });

                Succeed(result.WithheldDeletions > 0
                    ? $"'{source.Name}' synced — {result.Upserted} queued, {result.WithheldDeletions} deletion(s) withheld."
                    : $"'{source.Name}' synced — {result.Upserted} queued, {result.Deleted} removed.");
```

- [ ] **Step 3: Verify it builds**

Run: `dotnet build src/Connapse.Web`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Manual check**

Start the stack (`docker compose up -d`), create a source, let it index, then make its listing empty — the simplest way is an S3 source pointed at a bucket you then empty. Confirm the warning row appears with the count, that "Apply deletions" removes the documents, and that the row disappears afterwards.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Web/Components/Pages/Sources.razor
git commit -m "feat(web): surface withheld deletions on the Sources page"
```

---

### Task 6: Document the guard

**Files:**
- Modify: `docs/connectors.md`

- [ ] **Step 1: Add the section**

In `docs/connectors.md`, in the **Sync** section after the bullet list:

```markdown
### Deletions are guarded

A sync reconciles by absence: anything indexed but missing from the remote listing is treated as deleted. That inference is only as good as the listing — and a listing can come back empty *and successful*, from a narrowed bucket policy returning `200 OK` with no keys, or a directory that is temporarily unmounted.

So a reconcile that would delete more than **both 10 documents and 10% of what the source has indexed** applies its additions and **withholds the deletions**, recording how many. The Sources page shows the count to administrators with an "Apply deletions" button.

Two details worth knowing:

- **Additions still apply.** A source that trips the guard keeps ingesting, because a safety check that stops a source working is an outage.
- **Approving re-runs the sync**, it does not replay the earlier list. If the remote recovered in the meantime, nothing is deleted.

The threshold is fixed and not configurable. Small sources are never blocked — losing five of five files applies immediately, since re-ingesting them is cheap.
```

- [ ] **Step 2: Commit**

```bash
git add docs/connectors.md
git commit -m "docs: describe the source deletion guard"
```

---

## Self-Review

**Spec coverage.** Every Part 1 requirement maps to a task: the rule and both bounds (Task 1); `Source.WithheldDeletions` plus its migration (Task 2); list-and-diff-only application, upserts-still-apply, and count-not-status (Task 3); the DTO field and the audited override (Task 4); the admin button (Task 5); the docs (Task 6). The spec's four Part 1 tests all appear — boundaries in Task 1, the empty-listing regression and the override and upserts-still-apply in Task 3.

**Deliberately deferred to Part 2:** everything under SFTP.

**Type consistency.** `WithheldDeletions` is the name on `Source`, `SourceEntity`, `SourceSyncResult` and `SourceResponse`; `withheld_deletions` is the column; `applyWithheldDeletions` is the parameter and query-string name throughout; `DeletionGuard.ShouldWithhold(int, int)` is called only in Task 3 with the signature Task 1 defines.

**One risk called out for the executor.** Task 2 Step 5 appends `WithheldDeletions` to the *end* of the `Source` record's parameter list. `Source` is constructed positionally in several tests; appending keeps those compiling. Do not insert it next to the other sync-state fields, however tidy that looks.
