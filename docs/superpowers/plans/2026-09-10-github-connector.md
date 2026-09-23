# GitHub Connector (Public Repositories) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Index a public GitHub repository's markdown docs and its issues and pull requests for search, as two separate Connapse sources, with no credential.

**Architecture:** A single `GitHubConnector` implements the existing read interface and, for the first time in the codebase, `ISyncCursorConnector`. A repository is represented by two sources: a docs source (file-shaped, synced by git clone, cursor is a commit SHA) and an issues-and-pull-requests source (record-shaped, synced through the issues REST endpoint, cursor is an updated-at timestamp). Public repositories have no connection, which requires making `Source.ConnectionId` nullable and carrying the provider on the source. Issues and pull requests are rendered to markdown documents with a new `Record` chunker; graph edges are stored as document metadata for a later GraphRAG phase.

**Tech Stack:** .NET 10, EF Core (Npgsql), Octokit (new, for issues/PRs over the REST API, used unauthenticated), LibGit2Sharp (new, for docs clone/diff), Markdig (already present), xUnit + FluentAssertions + NSubstitute, Testcontainers (integration).

**Spec:** `docs/superpowers/specs/2026-09-09-github-connector-design.md`

**Epic:** Destrayon/Connapse#508

## Global Constraints

- .NET 10, file-scoped namespaces, nullable enabled, implicit usings. Records for DTOs/settings. Primary constructors for DI. Async all the way (never `.Result`/`.Wait()`). No `var` for primitive types. No `dynamic`. Parameterized SQL only.
- Wrap user-controlled values in `LogSanitizer.Sanitize(...)` when logging.
- Tests tagged `[Trait("Category","Unit")]` or `[Trait("Category","Integration")]`. Test naming `MethodName_Scenario_ExpectedResult`.
- Two EF histories: Storage migrations via `dotnet ef migrations add Name --project src/Connapse.Storage`. Migrations run automatically on startup, and **before** hosted services, so nothing a migration needs can be a hosted service.
- Enum values are never renumbered after removal (Azure left gaps at 2 and 4). `ConnectionProvider` and `ConnectorType` are kept value-aligned.
- `ConnectorFactory` is a **singleton**; any dependency injected into it must be singleton-safe or resolved via `IServiceScopeFactory` (captive-dependency trap; scope validation only runs in Development).
- PRs kept under ~300 lines; a phase exceeding it lands as stacked PRs (`Part of #508` on intermediate, `Closes`-the-phase-issue on the last). Per project convention, write each phase's detailed task steps only once its predecessor has landed, because later tests depend on the API the previous phase actually produced.
- Phases 1–5 were **public repositories only, unauthenticated**: no GitHub App, no private repos, no identity linking, no permission filter, no `Connapse.Identity` changes. **Superseded 2026-09-23** by the Milestone 1 revision below: a GitHub App is required for every GitHub source, and private repositories arrive in Phase 10 behind per-user permission filtering.

## File structure

**Phase 1 — model change (Core + Storage):**
- Modify `src/Connapse.Core/Models/ConnectionModels.cs` — add `ConnectionProvider.GitHub = 6`.
- Modify `src/Connapse.Core/Models/StorageModels.cs` — add `ConnectorType.GitHub = 6`.
- Modify `src/Connapse.Core/Models/SourceModels.cs` — `Source.ConnectionId` → `Guid?`; add `ConnectionProvider? Provider` to `Source` and `CreateSourceRequest`.
- Modify `src/Connapse.Storage/Data/Entities/SourceEntity.cs` — `ConnectionId` → `Guid?`; add `int? Provider`.
- Modify `src/Connapse.Storage/Data/KnowledgeDbContext.cs` (`ConfigureSources`) — `connection_id` nullable, FK optional, add `provider` column.
- Create migration `src/Connapse.Storage/Migrations/<ts>_MakeSourceConnectionOptional.cs`.
- Modify `src/Connapse.Storage/Sources/PostgresSourceStore.cs` — create/ map for nullable connection + provider.
- Modify `src/Connapse.Storage/Connectors/ConnectorFactory.cs` — add `Create(Source)` overload for connection-less sources; `GitHub` arm throws `NotSupportedException` until Phase 2.
- Modify `src/Connapse.Core/Interfaces/IConnectorFactory.cs` — declare the new overload.
- Modify `src/Connapse.Web/Endpoints/SourcesEndpoints.cs` — allow null `ConnectionId` when `Provider` is given.

**Phase 2 — connector skeleton + docs source:**
- Create `src/Connapse.Storage/Connectors/GitHubConnector.cs` (implements `ISyncCursorConnector`).
- Create `src/Connapse.Storage/Connectors/GitHubConnectorConfig.cs`.
- Create `src/Connapse.Core/Models/GitHubSourceSettings.cs` (mirror directory).
- Modify `ConnectorFactory.cs` — build `GitHubConnector` for docs kind.
- Modify `SourceSyncService.cs`, `SourcesEndpoints.cs`, `IngestionPipeline.cs` — sync and read connection-less sources.
- Modify `src/Connapse.Storage/Connapse.Storage.csproj` — add `LibGit2Sharp` (Octokit moves to Phase 3).

**Phase 3 — issues-and-pull-requests source:**
- Extend `GitHubConnector` (issues/PR kind branch), record assembly, edge capture.
- Create `src/Connapse.Storage/Connectors/GitHub/GitHubRecordRenderer.cs`.

**Phase 4 — record chunker:**
- Modify `src/Connapse.Core/Models/IngestionModels.cs` — `ChunkingStrategy.Record`.
- Create `src/Connapse.Ingestion/Chunking/RecordChunker.cs`.
- Modify `src/Connapse.Ingestion/Extensions/ServiceCollectionExtensions.cs` — register it.
- Modify `src/Connapse.Core/Models/SettingsModels.cs` — add `"Record"` to the `Strategy` doc comment.

**Phase 5 — admin UI:**
- Modify `src/Connapse.Web/Services/ProviderSetupReader.cs` — add the `github` `ProviderSetup`.
- Modify `src/Connapse.Web/Components/Pages/Providers.razor` — `@if (Key == "github")` branch.
- Modify `src/Connapse.Web/Components/Settings/SourceForm.cs` — GitHub scope + `ToScopeJson`.
- Modify `src/Connapse.Web/Components/Pages/Sources.razor` — GitHub scope branch + add-repository flow creating two sources.
- Modify `src/Connapse.Web/Services/SourceScopeSummary.cs` — GitHub scope line.

---

## Phase 1 — Source model: optional connection + provider

**Goal:** A source can exist with no connection, carrying its own provider, and the factory can build a connector from it. Done-condition: migration applies, a connection-less `CreateSourceRequest` round-trips through the store, and `dotnet build` + unit tests are green.

### Task 1.1: Add GitHub to both provider enums

**Files:**
- Modify: `src/Connapse.Core/Models/ConnectionModels.cs:9`
- Modify: `src/Connapse.Core/Models/StorageModels.cs:3`
- Test: `tests/Connapse.Core.Tests/Connectors/ConnectorCapabilityTests.cs`

**Interfaces:**
- Produces: `ConnectionProvider.GitHub = 6`, `ConnectorType.GitHub = 6` (value-aligned).

- [ ] **Step 1: Write the failing test**

In `ConnectorCapabilityTests.cs`:
```csharp
[Trait("Category", "Unit")]
public class GitHubEnumTests
{
    [Fact]
    public void GitHub_ProviderAndConnectorType_ShareValue()
    {
        ((int)ConnectionProvider.GitHub).Should().Be(6);
        ((int)ConnectorType.GitHub).Should().Be((int)ConnectionProvider.GitHub);
    }
}
```

- [ ] **Step 2: Run it, verify it fails to compile** (`GitHub` undefined).

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~GitHubEnumTests"`
Expected: build error, `GitHub` does not exist.

- [ ] **Step 3: Add the members**

```csharp
// ConnectionModels.cs
public enum ConnectionProvider { Filesystem = 1, S3 = 3, Sftp = 5, GitHub = 6 }
// StorageModels.cs
public enum ConnectorType { ManagedStorage = 0, Filesystem = 1, S3 = 3, Sftp = 5, GitHub = 6 }
```

- [ ] **Step 4: Run the test, verify PASS.**

- [ ] **Step 5: Commit**
```bash
git add src/Connapse.Core/Models/ConnectionModels.cs src/Connapse.Core/Models/StorageModels.cs tests/Connapse.Core.Tests/Connectors/ConnectorCapabilityTests.cs
git commit -m "feat(core): add GitHub to ConnectionProvider and ConnectorType

Part of #508"
```

### Task 1.2: Make `Source.ConnectionId` nullable and add `Provider`

**Files:**
- Modify: `src/Connapse.Core/Models/SourceModels.cs:5-40`
- Test: `tests/Connapse.Core.Tests/Sources/SourceModelTests.cs` (create)

**Interfaces:**
- Produces: `Source.ConnectionId` is `Guid?`; `Source.Provider` is `ConnectionProvider?` (appended — positional construction); `CreateSourceRequest.ConnectionId` is `Guid?`; `CreateSourceRequest.Provider` is `ConnectionProvider?` (appended). Invariant: exactly one of (`ConnectionId`, `Provider`) identifies the connector — a connection-bound source has `ConnectionId` set and derives its provider from the connection; a connection-less source has `ConnectionId == null` and `Provider` set.

- [ ] **Step 1: Write the failing test**
```csharp
[Trait("Category", "Unit")]
public class SourceModelTests
{
    [Fact]
    public void Source_ConnectionLess_CarriesProvider()
    {
        var s = new Source(Guid.NewGuid(), "repo-docs", null, ConnectionId: null,
            ScopeJson: "{}", DateTime.UtcNow, DateTime.UtcNow) { };
        s.ConnectionId.Should().BeNull();
    }
}
```
(Provider is appended after the existing optional params; set it via the positional tail once added.)

- [ ] **Step 2: Run, verify fails** (ConnectionId not nullable; Provider undefined).

- [ ] **Step 3: Edit the records**

In `SourceModels.cs`, change `Guid ConnectionId` to `Guid? ConnectionId`, and **append** `ConnectionProvider? Provider = null` to the end of `Source` (after `FailedDocumentCount`) and to `CreateSourceRequest` (after `SyncIntervalSeconds`). Appending preserves positional construction used elsewhere.

- [ ] **Step 4: Run, verify PASS.**

- [ ] **Step 5: Build the whole solution** to surface every positional `Source(...)` / `CreateSourceRequest(...)` construction that now needs the connection argument as nullable.

Run: `dotnet build`
Expected: compiles (nullable widening is source-compatible); note any call sites the compiler flags and fix them in this task.

- [ ] **Step 6: Commit**
```bash
git add src/Connapse.Core/Models/SourceModels.cs tests/Connapse.Core.Tests/Sources/SourceModelTests.cs
git commit -m "feat(core): Source.ConnectionId nullable, add Source.Provider

Part of #508"
```

### Task 1.3: Entity + DbContext mapping for optional connection

**Files:**
- Modify: `src/Connapse.Storage/Data/Entities/SourceEntity.cs`
- Modify: `src/Connapse.Storage/Data/KnowledgeDbContext.cs` (`ConfigureSources`, ~:537-620)

**Interfaces:**
- Consumes: `SourceEntity`.
- Produces: `SourceEntity.ConnectionId` is `Guid?`; `SourceEntity.Provider` is `int?`; `connection_id` column nullable; new `provider` int column; FK optional with `OnDelete(Restrict)`.

- [ ] **Step 1: Edit the entity** — `Guid? ConnectionId`, add `int? Provider`, make the `Connection` navigation nullable (`ConnectionEntity? Connection`).

- [ ] **Step 2: Edit `ConfigureSources`**
```csharp
entity.Property(e => e.ConnectionId).HasColumnName("connection_id"); // drop .IsRequired()
entity.Property(e => e.Provider).HasColumnName("provider");          // new, nullable int
entity.HasOne(e => e.Connection)
      .WithMany(c => c.Sources)
      .HasForeignKey(e => e.ConnectionId)
      .OnDelete(DeleteBehavior.Restrict);   // optional FK now (nullable column)
```

- [ ] **Step 3: Build**, verify the model compiles. (No test here; the migration task verifies behavior.)

- [ ] **Step 4: Commit**
```bash
git add src/Connapse.Storage/Data/Entities/SourceEntity.cs src/Connapse.Storage/Data/KnowledgeDbContext.cs
git commit -m "feat(storage): map optional source connection and provider column

Part of #508"
```

### Task 1.4: Migration

**Files:**
- Create: `src/Connapse.Storage/Migrations/<ts>_MakeSourceConnectionOptional.cs` (+ `.Designer.cs`, snapshot)

- [ ] **Step 1: Generate the migration**

Run: `dotnet ef migrations add MakeSourceConnectionOptional --project src/Connapse.Storage`
Expected: a migration altering `connection_id` to nullable and adding `provider`.

- [ ] **Step 2: Read the generated `Up`/`Down`** and confirm it drops the NOT NULL on `connection_id`, re-creates the FK as optional, and adds `provider integer NULL`. Adjust if the scaffolder dropped/recreated the FK awkwardly; keep `onDelete: ReferentialAction.Restrict`.

- [ ] **Step 3: Apply against a scratch database**

Run: `dotnet ef database update --project src/Connapse.Storage` (against a local dev Postgres from the compose file)
Expected: applies cleanly.

- [ ] **Step 4: Commit**
```bash
git add src/Connapse.Storage/Migrations/
git commit -m "feat(storage): migration making source connection optional

Part of #508"
```

### Task 1.5: Store create/map for connection-less sources

**Files:**
- Modify: `src/Connapse.Storage/Sources/PostgresSourceStore.cs` (`CreateAsync` ~:23-63, `MapToModel` ~:292-315)
- Test: `tests/Connapse.Integration.Tests/` (a source-store test; follow the `[Collection("Integration Tests")]` + `SharedWebAppFixture` pattern)

**Interfaces:**
- Consumes: `CreateSourceRequest` with nullable `ConnectionId` + `Provider`.
- Produces: `CreateAsync` persists a connection-less source when `ConnectionId == null && Provider != null`; validates the connection exists only when `ConnectionId != null`; rejects (`ArgumentException`) when both are null.

- [ ] **Step 1: Write the failing integration test**
```csharp
[Fact]
public async Task CreateAsync_ConnectionLessGitHub_PersistsProvider()
{
    var store = /* resolve ISourceStore from the fixture */;
    var created = await store.CreateAsync(new CreateSourceRequest(
        Name: $"docs-{Guid.NewGuid():N}", ConnectionId: null, ScopeJson: "{\"owner\":\"octocat\",\"repo\":\"Hello-World\",\"kind\":\"Docs\"}",
        Provider: ConnectionProvider.GitHub));
    created.ConnectionId.Should().BeNull();
    created.Provider.Should().Be(ConnectionProvider.GitHub);
}
```

- [ ] **Step 2: Run, verify fails.**

- [ ] **Step 3: Edit `CreateAsync`** — guard: if `ConnectionId is null && Provider is null` throw `ArgumentException("a source needs a connection or a provider")`; run the existing `Connections.AnyAsync` existence check only when `ConnectionId is not null`; persist `Provider = (int?)request.Provider`. Edit `MapToModel` to read `ConnectionId` (nullable) and `Provider = (ConnectionProvider?)entity.Provider`.

- [ ] **Step 4: Run, verify PASS.**

- [ ] **Step 5: Commit**
```bash
git add src/Connapse.Storage/Sources/PostgresSourceStore.cs tests/Connapse.Integration.Tests/
git commit -m "feat(storage): persist connection-less sources with provider

Part of #508"
```

### Task 1.6: Factory overload for connection-less sources

**Files:**
- Modify: `src/Connapse.Core/Interfaces/IConnectorFactory.cs`
- Modify: `src/Connapse.Storage/Connectors/ConnectorFactory.cs` (:35-119)
- Test: `tests/Connapse.Core.Tests/Connectors/SourceConnectorFactoryTests.cs`

**Interfaces:**
- Produces: `IConnector Create(Source source)` — builds a connector for a connection-less source using `source.Provider`. The `ConnectionProvider.GitHub` arm throws `NotSupportedException("GitHub connector arrives in phase 2")` for now; `default` throws `NotSupportedException`. The existing `Create(Source, Connection, secret)` is unchanged.

- [ ] **Step 1: Write the failing test**
```csharp
[Fact]
public void Create_ConnectionLessGitHub_NotYetImplemented()
{
    var factory = BuildFactory(); // existing helper pattern in this test file
    var source = new Source(Guid.NewGuid(), "docs", null, null, "{}", DateTime.UtcNow, DateTime.UtcNow)
        { /* Provider = ConnectionProvider.GitHub via positional tail */ };
    Action act = () => factory.Create(source);
    act.Should().Throw<NotSupportedException>();
}
```

- [ ] **Step 2: Run, verify fails** (overload undefined).

- [ ] **Step 3: Add the overload** to the interface and implement it: parse `source.ScopeJson`, switch on `source.Provider`, `GitHub => throw new NotSupportedException(...)`, `_ => throw new NotSupportedException(...)`. Throw `ArgumentException` if `source.Provider is null` (a connection-less source must have one).

- [ ] **Step 4: Run, verify PASS.**

- [ ] **Step 5: Commit**
```bash
git add src/Connapse.Core/Interfaces/IConnectorFactory.cs src/Connapse.Storage/Connectors/ConnectorFactory.cs tests/Connapse.Core.Tests/Connectors/SourceConnectorFactoryTests.cs
git commit -m "feat(storage): factory overload for connection-less sources

Part of #508"
```

### Task 1.7: REST endpoint accepts connection-less create

**Files:**
- Modify: `src/Connapse.Web/Endpoints/SourcesEndpoints.cs` (:83-134, DTO :257)
- Test: `tests/Connapse.Integration.Tests/` (endpoint test following existing source-endpoint tests)

**Interfaces:**
- Produces: `CreateSourceApiRequest` gains `ConnectionProvider? Provider`. The create route skips the connection lookup when `ConnectionId is null`, requires `Provider` in that case (400 if neither), and still validates `ScopeJson` is well-formed JSON. Stays `RequireAuthorization("RequireAdmin")`.

- [ ] **Step 1: Write the failing test** — POST `/api/sources` with `ConnectionId: null, Provider: GitHub` returns 201 and a source with no connection.

- [ ] **Step 2: Run, verify fails** (current code 400s without a resolvable connection).

- [ ] **Step 3: Edit the route** — branch: if `request.ConnectionId is Guid cid` do the existing `connectionStore.GetAsync(cid)` 400-check; else require `request.Provider is not null` (400 otherwise); pass both through to `CreateSourceRequest`.

- [ ] **Step 4: Run, verify PASS.**

- [ ] **Step 5: Commit**
```bash
git add src/Connapse.Web/Endpoints/SourcesEndpoints.cs tests/Connapse.Integration.Tests/
git commit -m "feat(api): allow connection-less source creation with a provider

Part of #508"
```

### Task 1.8: Phase-exit verification

- [ ] Run `dotnet build` (0 errors), `dotnet test --filter "Category=Unit"`, and the Storage integration tests. Confirm a full-host-start integration test still passes (proves the migration + DI wiring, which a clean container start alone would not). Append the phase summary to this plan file: what changed, files touched, what is verified, next action.

### Phase 1 status — COMPLETE (2026-09-14)
- Done: `GitHub=6` in both enums; `Source`/`CreateSourceRequest` nullable `ConnectionId` + `Provider`; `SourceEntity`/`KnowledgeDbContext` nullable `connection_id` + `provider` column; migration `MakeSourceConnectionOptional` with a `ck_sources_connection_xor_provider` CHECK (mirrors `ck_documents_single_owner`); `PostgresSourceStore` XOR-guarded connection-less create + map; `IConnectorFactory.Create(Source)` overload (GitHub arm throws, Phase 2); REST create endpoint accepts connection-less with 400s for both-null and both-set; sync loop distinguishes connection-less from dangling.
- Files: `ConnectionModels.cs`, `StorageModels.cs`, `SourceModels.cs`, `SourceEntity.cs`, `KnowledgeDbContext.cs`, `Migrations/*MakeSourceConnectionOptional*`, `PostgresSourceStore.cs`, `IConnectorFactory.cs`, `ConnectorFactory.cs`, `SourcesEndpoints.cs`, `SourceSyncService.cs`, + tests.
- Verified: clean build; unit suite green; 45 host/schema/sync + 33 store/endpoint integration tests green vs real Postgres; CHECK constraint verified to reject a contradictory row on a fresh DB.
- Deferred (in ledger): endpoint/razor "missing connection" wording for connection-less sources (Phase 5 UX); undefined-enum-int for Provider (fail-closed downstream); `SourceResponse` lacks `Provider` (Phase 5). Carry-forward to Phase 2: `SourceSyncService` and the sync route must route a connection-less source through `IConnectorFactory.Create(Source)`.
- Next action: expand Phase 2's detailed steps (per the expand-on-land convention) and implement the docs source.

---

## Phase 2 — GitHub connector skeleton + docs source

**Goal:** A connection-less GitHub docs source syncs a public repo's markdown via git clone, emitting `ConnectorFile`s and a commit-SHA cursor; `ReadFileAsync` returns each doc's content. Done-condition: an integration test against a real small public repo (or a fixture clone) lists and reads the expected markdown files and advances the SHA cursor.

Expanded on Phase 1 landing. Two decisions changed the outline, recorded here so Phase 3 builds on what exists:

- **Persistent bare mirror, not a clone per sync.** Each docs source keeps a bare mirror at `{Sources:GitHub:MirrorDirectory}/{sourceId:N}` (default `appdata/github-mirrors`, on the appdata volume). A sync fetches `+HEAD:refs/connapse/head` (default branch only, no tags) and tree-diffs the stored SHA against the new head. The mirror retains every object it fetched, so a force-push upstream still diffs cleanly; only a lost mirror (cursor unknown locally) returns `RequiresFullResync`. Shallow fetch was spiked and dropped: LibGit2Sharp 0.32's `FetchOptions.Depth` fetched full history anyway, so relying on it would make production behavior differ from what is tested.
- **Reads come from the mirror, not `raw.githubusercontent.com`.** The pipeline builds a fresh connector per document; reading the mirror costs no network and no rate budget, and gives exactly the content the sync diffed. Only `GetChangesAsync` fetches, so the mirror has a single writer, serialized by the per-source sync gate.

### Task 2.1: Add NuGet dependency
- [x] `LibGit2Sharp` 0.32.0 in `Connapse.Storage.csproj`. Octokit is deferred to Phase 3, where it is first used.

### Task 2.2: Config + settings
- [x] `src/Connapse.Storage/Connectors/GitHubConnectorConfig.cs`: `GitHubContentKind { Docs, IssuesAndPullRequests }`; `Owner`, `Repo`, `RepoId`, `Kind`, `Host` (pinned `github.com`), `IncludePatterns` (default `*.md`, `*.markdown` — what the text parser reads), `ExcludePatterns`, `MirrorPath`, `RemoteUrl` (test-only override; the factory never sets it). `IsValidOwner`/`IsValidRepo` follow GitHub's naming rules (Phase 5's form reuses them).
- [x] `src/Connapse.Core/Models/GitHubSourceSettings.cs` (`Sources:GitHub:MirrorDirectory`), bound in `AddStorage`, configuration-only like `SourceSecuritySettings`; default added to `appsettings.json`.

### Task 2.3: `GitHubConnector` (docs kind)
- [x] `GitHubConnector : ISyncCursorConnector`. `GetChangesAsync(null)` lists every matching regular file at head; `(head)` is an empty delta; `(older)` is a tree diff (added/modified → upsert, deleted → delete, rename → delete old + upsert new, a doc replaced by a symlink → delete). Symlinks and submodules are never docs. Globs match file names case-insensitively, compiled once with a regex timeout (mirrors `SftpConnector`).
- [x] `ReadFileAsync`/`ExistsAsync`/`ListFilesAsync` read the mirrored head; before the first sync they fail with `FileNotFoundException` (or `false`) rather than fetching. `ResolveJobPath` is identity; paths are repo-relative with a leading slash; `ResourceUri` is `https://github.com/{o}/{r}/blob/HEAD/{path}` (stable across commits).
- [x] **Open item 1 resolved differently:** the public-to-private re-check is the fetch itself. GitHub answers an anonymous smart-HTTP request for any non-public repository (private, deleted, or renamed away) with 401 — confirmed 2026-09-22 (`/info/refs?service=git-upload-pack`: 200 for a public repo, 401 for a missing one) — which libgit2 raises as `GIT_EAUTH`. That becomes `GitHubRepositoryUnavailableException`, so the cycle fails with a clear message and keeps its cursor, at zero REST cost. The REST `GET /repos/{o}/{r}` probe is not needed; it would spend 12 of the 60 hourly anonymous requests per source at the 5-minute interval.

### Task 2.4: Wire the factory and the connection-less paths
- [x] `ConnectorFactory.Create(Source)`'s GitHub arm builds the connector from `ScopeJson` (`owner`, `repo`, `repoId`, `kind`, `includePatterns`, `excludePatterns`); invalid owner/repo, unknown kind, or any `host` other than `github.com` throws. No `HttpClient` needed in this phase.
- [x] Carry-forward from Phase 1: `SourceSyncService.SyncAllAsync` and the sync-now route now sync connection-less sources through `Create(Source)` (`SyncSourceAsync` takes a nullable `Connection`); `IngestionPipeline.IngestSourceDocumentAsync` reads them the same way.

### Task 2.5: Tests
- [x] Unit: `GitHubConnectorTests` (local upstream repo via LibGit2Sharp: initial listing, no-op, add/modify/delete, rename, force-push, unknown/malformed cursor, excludes, reads, exists, prefix listing, auth-refusal classification), `GitHubConnectorConfigTests`, GitHub cases in `SourceConnectorFactoryTests`, updated `SourceSyncServiceLoggingTests`.
- [x] Integration: `GitHubSourceSyncIntegrationTests` — a connection-less GitHub source synced through `SourceSyncService` against real PostgreSQL and a local upstream: docs enqueued and SHA cursor stored; unchanged head is a no-op; an upstream deletion removes the document; `SyncAllAsync` reaches connection-less sources.

### Task 2.6: Phase-exit verification + plan update.

### Phase 2 status — COMPLETE (2026-09-22)
- Done: `GitHubConnector` (docs kind) over a per-source bare mirror; `GitHubConnectorConfig` + `GitHubSourceSettings`; factory GitHub arm; connection-less sources synced by `SyncAllAsync`/sync-now and read by the ingestion pipeline; public-to-private re-check via the anonymous fetch's 401.
- Files: `GitHubConnector.cs`, `GitHubConnectorConfig.cs`, `GitHubSourceSettings.cs`, `ConnectorFactory.cs`, `ServiceCollectionExtensions.cs`, `SourceSyncService.cs`, `SourcesEndpoints.cs`, `IngestionPipeline.cs`, `appsettings.json`, `Connapse.Storage.csproj`, + tests.
- Verified: clean build; full unit suite green; `GitHubSourceSyncIntegrationTests` + the sync/endpoint/store/delete-guard integration classes green vs real PostgreSQL (the two `SourceIngestionOwnershipTests` failures reproduce identically on the Phase 1 base — they need an embedding backend). Anonymous 401 for non-public repos and `+HEAD:` fetch confirmed against github.com.
- Deferred (carry forward):
  1. **Search withholding on public-to-private.** The spec wants a now-non-public repo's documents withheld from search. Phase 2 stops syncing and records why; already-indexed content stays searchable. Needs a search-side filter keyed on source state — file as its own task before Phase 5 ships the add-repository flow.
  2. **Null-cursor delta does not reconcile deletions.** `SyncViaDeltaAsync` treats the initial set as upserts only, so after a `RequiresFullResync` (lost mirror) files deleted upstream in the gap stay indexed. Phase 3's periodic issues re-list needs the same "initial set is authoritative, diff against indexed paths under the deletion guard" engine support — build it there and both kinds get it.
  3. **Mirror cleanup.** Deleting a source leaves its mirror directory; add removal to the source-delete path (or a sweep of directories with no matching source).
  4. **First fetch is full default-branch history** (shallow not honored by LibGit2Sharp 0.32). Fine for docs repos; revisit if a very large repository is a target.
- Next action: expand Phase 3's detailed steps and implement the issues-and-pull-requests source (add Octokit there).

---

## Phase 3 — Issues-and-pull-requests source

**Goal:** A GitHub issues-and-PRs source syncs issues and pull requests (with comments assembled) as markdown records, cursor is an updated-at timestamp, with graph edges stored as metadata. Done-condition: integration test against a fixture/public repo ingests records with assembled comments and edge metadata, and the updated-at cursor advances with same-second dedup.

Expanded on Phase 2 landing (#510). Probes against api.github.com on 2026-09-22 changed the outline:

- **Anonymous GraphQL is refused (403)**, so `closingIssuesReferences` cannot be fetched. The issues list payload already carries `closed_by` (the closing user), `pull_request.merged_at`, labels, milestone, and `sub_issues_summary`, and `GET /repos/{o}/{r}/issues/comments?since=` / `pulls/comments?since=` return every comment in the repository in one paginated sweep. So a sync costs pages, not calls per record.
- **Open item 2 resolved: per-record edges are deferred to the backfill.** Timeline (`cross_referenced`, `connected`/`disconnected`) and `pulls/{n}/files` are one call per record — a 1,000-record repo would take ~17 hours of anonymous budget for those alone. Captured now, at no per-record cost: labels, milestone, state, closing user, merge time, `closes` (closing keywords parsed from PR bodies — GitHub's own linking rule), `references` (`#N` mentions in bodies and comments), and sub-issue `parent`/`children` (one call per issue that has sub-issues).
- **No Octokit.** Two list endpoints plus Link-header paging over `IHttpClientFactory`; Octokit's models also lag the newer fields (`sub_issues_summary`).
- **Records are kept locally, like the docs mirror.** Sweeps merge into a per-source record store (`{MirrorDirectory}/{sourceId}/records/{n}.json`); `ReadFileAsync` renders from it. The pipeline builds a connector per document, and a network read per record would spend the budget the sweeps just saved.
- **Paths are `/issues/{n}.md` and `/pulls/{n}.md`**, not the spec's extension-less form: the pipeline picks a parser by file extension.

### Task 3.1: `ConnectorFile.Metadata`
- [x] Append `IReadOnlyDictionary<string,string>? Metadata = null` to `ConnectorFile`; `SourceSyncService.EnqueueAllAsync` copies it into the job's metadata (sync keys win). Test in `SourceSyncService` unit tests.

### Task 3.2: API client + record store + renderer
- [x] `Connectors/GitHub/GitHubApiClient.cs`: anonymous GET with `User-Agent`, API version header, Link `rel="next"` paging. 403/429 with `x-ratelimit-remaining: 0` (or `retry-after`) → `GitHubRateLimitedException`; 401/404 → `GitHubRepositoryUnavailableException`.
- [x] `Connectors/GitHub/GitHubRecordStore.cs`: one JSON file per record (issue fields, comments by id, parent, children) plus `state.json`; atomic writes (temp + move) because the pipeline reads while a sync writes.
- [x] `Connectors/GitHub/GitHubRecordRenderer.cs`: header (`# {number}: {title}`, type/author/state/labels/milestone/dates, edge line), body, `--- Comment by {login} ({date}) ---` blocks in time order; minimized comments dropped. Returns the markdown and the edge metadata (`github:*` keys). Unit tests.

### Task 3.3: Issues kind of `GitHubConnector`
- [x] Cursor is a small JSON object: one `updated_at` high-water mark per sweep (issues, comments, review comments) plus an emission sequence number. Every sweep re-requests `since` inclusively; re-processing the boundary item is idempotent (same content, same signature → the engine skips it), which is the same-second tie handling without an id set.
- [x] Order per cycle: issues sweep; the comment sweeps run only when the issues sweep saw a change or a comment sweep has not caught up (an idle cycle costs one request). Records touched are marked dirty and emitted only once every sweep of the cycle completed, so a first sync embeds each record once, with its comments.
- [x] Rate limit mid-cycle: keep what was merged, advance each sweep's mark to what it finished, emit nothing, return normally — the next cycle resumes. A large first sync converges over hours instead of failing.
- [x] Emitted-but-unacknowledged records: dirty numbers are cleared only when the next cycle arrives with the cursor that emitted them (the engine stored it), so an engine failure after emission re-emits rather than loses them.
- [x] Deletion reconciliation: every 24 h, after a completed cycle, page all issues (`state=all`) and delete stored records that are gone. Applied only when the full listing completed.
- [x] Null cursor resets the record store; a cursor without a store returns `RequiresFullResync`.

### Task 3.4: Factory + DI
- [x] Named `HttpClient` "GitHub" registered in `AddStorage`; `ConnectorFactory` takes `IHttpClientFactory` (singleton-safe) and hands the connector a client for the issues kind.

### Task 3.5: Tests + phase exit
- [x] Unit: renderer, API client (stub handler: paging, rate limit, 404), connector issues kind (first sync, incremental, comments merged, sub-issues, rate-limited resume, re-emit on unacknowledged cursor, relist deletion).
- [x] Integration: `GitHubIssuesSyncIntegrationTests` — an issues source through `SourceSyncService` against real PostgreSQL with a stub GitHub handler: records enqueued with edge metadata, cursor stored, idle cycle is a no-op.

### Phase 3 status — COMPLETE (2026-09-22)
- Done: issues-and-pull-requests kind (`GitHubRecordSource`) over a local record store; anonymous REST client with Link paging and rate-limit/404 classification; renderer with comment assembly and free edges; `ConnectorFile.Metadata` carried into the ingestion job; factory hands the issues kind a named `HttpClient`. All Task 3.1–3.5 boxes above are done.
- Files: `Connectors/GitHub/{GitHubApiClient,GitHubRecordStore,GitHubRecordRenderer,GitHubRecordSource}.cs`, `GitHubConnector.cs`, `GitHubConnectorConfig.cs`, `ConnectorFactory.cs`, `ServiceCollectionExtensions.cs`, `StorageModels.cs`, `SourceSyncService.cs`, + tests (`FakeGitHubApi` shared with the integration project).
- Verified: 0-warning build; unit suite green (1,241 in Core.Tests incl. 22 new record tests); `GitHubIssuesSyncIntegrationTests` + source integration classes green vs real PostgreSQL (same single pre-existing embedding-backend failure as Phase 2). Live run against `octocat/git-consortium`: 54 records, comments assembled, edges in metadata, idle second cycle emitted nothing.
- Deferred (carry forward): timeline/PR-files edges (backfill once an App raises the limit); a comment edit that does not move its issue's `updated_at` is picked up only when the issue next changes; issue label/state filters from the spec's scope list; per-source default sync interval for issues sources (set in Phase 5's add-repository flow — at 5 minutes an idle source spends 12 of the 60 hourly requests).
- Next action: Phase 4 — the `Record` chunker; it must beat the resolver's `.md` → DocumentAware auto-route for record paths.

---

## Phase 4 — Record chunker

**Goal:** `ChunkingStrategy.Record` chunks a record as one chunk unless oversized, then splits into per-comment child chunks carrying the parent header. Done-condition: unit tests green; issues/PRs sources chunk with `Record`; docs sources still use `DocumentAware`.

### Task 4.1: Add the enum member
**Files:** Modify `src/Connapse.Core/Models/IngestionModels.cs:49`; Modify `src/Connapse.Core/Models/SettingsModels.cs:81-82` (doc comment).
- [x] Add `Record` to `ChunkingStrategy` so `ChunkingStrategy.Record.ToString() == "Record"`. Add `"Record"` to the `Strategy` doc comment. Build.

### Task 4.2: `RecordChunker`
**Files:** Create `src/Connapse.Ingestion/Chunking/RecordChunker.cs`; Test `tests/Connapse.Ingestion.Tests/Chunking/RecordChunkerTests.cs`.

**Interfaces:**
- Produces: `class RecordChunker(ITokenCounter tokenCounter, RecursiveChunker recursiveChunker) : IChunkingStrategy` with `Name => "Record"`, returning `IReadOnlyList<ChunkInfo>`.

- [x] **Step 1: Failing test** — mirror `FixedSizeChunkerTests`: a small record returns a single chunk whose `Content` is the whole record and whose metadata has `["ChunkingStrategy"]="Record"`.
```csharp
[Trait("Category","Unit")]
public class RecordChunkerTests
{
    private readonly RecordChunker _chunker = new(new TiktokenTokenCounter(), new RecursiveChunker(new TiktokenTokenCounter()));
    [Fact] public void Name_ReturnsRecord() => _chunker.Name.Should().Be("Record");
    [Fact]
    public async Task ChunkAsync_SmallRecord_ReturnsSingleChunk()
    {
        var doc = new ParsedDocument("# 12: Title\n\nbody", new Dictionary<string,string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 512, MinChunkSize = 10, Overlap = 0 };
        var result = await _chunker.ChunkAsync(doc, settings);
        result.Should().ContainSingle();
        result[0].Metadata["ChunkingStrategy"].Should().Be("Record");
    }
}
```
- [x] **Step 2: Run, verify fails.**
- [x] **Step 3: Implement** — if `CountTokens(content) <= settings.MaxChunkSize`, return one `ChunkInfo` (whole content, metadata `ChunkingStrategy`/`ChunkIndex`). Else split on the comment delimiter into child chunks, each prefixed with the field-header lines (the text before the first delimiter), delegating any still-oversized child to `recursiveChunker`. Mirror `DocumentAwareChunker`'s `BuildChunk`/fallback pattern.
- [x] **Step 4: Run, verify PASS** (add an oversized-record test that asserts multiple chunks each containing the header).
- [x] **Step 5: Commit.**

### Task 4.3: Register + select
**Files:** Modify `src/Connapse.Ingestion/Extensions/ServiceCollectionExtensions.cs:37-45`.
- [x] Register `services.AddSingleton<IChunkingStrategy, RecordChunker>();` (RecursiveChunker already double-registered at :40-41). The pipeline matches by `Name` against `ChunkingStrategy.ToString()`, so no resolver change is needed; issues/PRs sources set their per-source chunking override to `Record` (Phase 3/5). Build + run unit tests. Commit.

### Task 4.4: Phase-exit verification + plan update.

### Phase 4 status — COMPLETE (2026-09-22)
- Done: `ChunkingStrategy.Record` + `RecordChunker` (whole record when it fits; else split at `--- … ---` comment lines, adjacent parts packed to the budget, header on every chunk, oversized parts sub-split by `RecursiveChunker` with a lowered minimum). Registered in `AddIngestion`.
- **Selection changed from the outline:** there is no per-source chunking override to set, so the connector reports the strategy per file (`ConnectorFile.Strategy`, set to `Record` by the issues kind) and the sync engine puts it on the job. `IngestionPipelineStrategyResolver` lets `Record` outrank the `.md` → DocumentAware route, and `ReindexService` treats a stored `Record` as content-pinned — kept on re-enqueue and never flagged stale when the configured strategy changes.
- Files: `IngestionModels.cs`, `SettingsModels.cs` (doc comment), `StorageModels.cs`, `RecordChunker.cs`, `IngestionPipeline.cs` (resolver), `ReindexService.cs`, `Ingestion/Extensions/ServiceCollectionExtensions.cs`, `SourceSyncService.cs`, `GitHubRecordSource.cs`, + tests.
- Verified: clean build; unit suite green (233 in Ingestion.Tests incl. 7 chunker + 7 routing cases); GitHub/sync integration green. The 5 reindex/ownership integration failures reproduce identically on the Phase 3 branch (embedding backend).
- Next action: Phase 5 — provider page and add-repository flow.

---

## Phase 5 — Admin provider page + add-repository flow

**Goal:** An admin can add a public GitHub repository from the Providers UI, creating a docs source and an issues-and-PRs source, both connection-less. Done-condition: the GitHub provider appears on `/admin/providers`, the add-repository flow creates two sources from a repo URL, and the sources sync.

**Detailed steps to be written once Phase 4 lands.** Shape:

### Task 5.1: Surface the provider
- `ProviderSetupReader.ReadAsync` adds a `ProviderSetup("github", "GitHub", [...requirements...], InUse: providers.Contains(ConnectionProvider.GitHub))`. For public-only there is no credential requirement; the card explains "public repositories, no setup required," following the provider-step-card convention (Easy setup + Manual values per the provider-page rule even when minimal).

### Task 5.2: Provider page branch
- Add `@if (Key == "github")` in `Providers.razor` with a `ProviderStepCard` describing the public-repo flow and a link to add a repository.

### Task 5.3: Scope form + add-repository flow
- `SourceForm.ToScopeJson` gains a `ConnectionProvider.GitHub` case emitting `{"owner","repo","kind","host"}` (must not throw on GitHub). `Sources.razor` gains a GitHub scope branch (repo URL → owner/repo parse, doc path globs). An "add repository" action creates **two** sources (Docs and IssuesAndPullRequests) in one submit via `SourceStore.CreateAsync` with `ConnectionId: null, Provider: GitHub`. `SourceScopeSummary.Describe` renders a GitHub scope line.

### Task 5.4: UI/e2e verification + plan update. The project's `release-gatekeeper`/provider-page rubric applies; audit the page against the config-page rubric as prior providers did.

### Phase 5 status — CODE COMPLETE, browser check outstanding (2026-09-22)
- Done: `github` entry in `ProviderSetupReader` (one "Public repositories" requirement; in use once any repository has a source, counted by owner/repo); `Providers.razor` GitHub branch with one `ProviderStepCard` (Easy = paste an address, Manual = docs/issues/comments/patterns choices); `GitHubRepositoryForm` parses `owner/repo` or any github.com URL and builds the docs and issues `CreateSourceRequest`s; `SourceScopeSummary` shows `owner/repo · docs` / `· issues and pull requests`; `Sources.razor` names a connection-less source's provider and lets Sync now run it.
- **Added to the phase:** the sync loop now honours `SyncIntervalSeconds` (`SourceSyncService.IsDue`). It was stored and editable but ignored, so every source synced every 5 minutes; the add-repository flow gives issues sources 15 minutes, which keeps an idle source at 4 of the 60 hourly anonymous requests.
- `SourceForm.ToScopeJson` is unchanged: the generic add-source form requires a connection, so GitHub sources are created only from the provider page and never reach it.
- Verified: clean build; unit suite green (incl. 17 form cases, 4 interval cases, 2 provider-reader cases, card count now 6); `GitHubRepositoryFlowIntegrationTests` pushes the form's requests through the real store and factory, syncs both sources in one cycle, and resolves the reader from the host to confirm DI supplies the store.
- **Not verified:** the page in a browser. Signing in needs a password, which the agent may not enter; an admin should load `/admin/providers/github`, add a repository, and audit the page against the config-page rubric.
- Deferred: editing a GitHub source's scope from the Sources page; `SourceResponse` still lacks `Provider` (REST).

### Phase 5 revision — the add-repository UI is withdrawn (2026-09-23)
Two decisions superseded the Phase 5 UI before #513 merged:
- **Providers is for identity and permission setup only.** A public repository is a source, so the GitHub provider page, its `ProviderSetupReader` entry, and the step card were removed.
- **A GitHub App is required for every GitHub source, public ones included** (Milestone 1 revision below). The interim "No connection needed → Public GitHub repository" option in New source, `GitHubRepositoryForm`, and the `SourceOrigin` where-from model were therefore removed too; the parser and form live in `c8ebe95` if the App phase wants them back.

What #513 keeps: `SourceSyncService.IsDue` (per-source sync intervals are honoured), the Sources page naming a connection-less source's provider and letting Sync now run it, and the GitHub line in `SourceScopeSummary`.

---

## Milestone 1 revision — GitHub App required (2026-09-23)

**Why.** Anonymous public sync is capped at 60 REST requests an hour per IP (about 10 repositories), GitHub tightened unauthenticated limits in May 2025, and no comparable product syncs issues anonymously. A GitHub App installation gets its own budget — 5,000 an hour plus 50 per repository and per org user past 20, capped at 12,500 (15,000 on Enterprise Cloud) — and an authenticated 304 costs nothing, so idle repositories become free. Pooling user-supplied tokens was rejected as a hassle. Evidence: `docs/research/github-connector-scaling-2026-09-23.md`.

**Rules that follow.**
- Every GitHub source belongs to a GitHub App connection. Connection-less GitHub sources are removed (the optional `ConnectionId` from Phase 1 stays, for future public sources such as a web crawl).
- **Until Phase 7 lands, the anonymous path still exists on the epic branch:** the REST create endpoint accepts a connection-less GitHub source and the factory builds an anonymous connector. That is transitional, not supported — no UI creates one — and the epic does not merge to main before Phase 7 removes it and Phase 9 settles existing rows.
- **No private repository is indexed before search can enforce GitHub permissions.** Private-source creation belongs to Phase 10, behind a verified per-user filter; Phases 6–9 are public-only. Pinning ingestion to the right installation does not restrict who can *search* the content.
- **Public repositories** may be read through any healthy installation in the pool: pick the one with the most remaining budget, fail over on exhaustion or a revoked installation, wait for the earliest reset when all are spent.
- **Private repositories** are read only through the installation that covers them — never pooled, per the per-object permission principle. A failed installation hides its sources' documents, fail closed.
- The App's private key is stored encrypted on the host (as the AWS Roles Anywhere key is). Key Vault / KMS remote signing is an optional later hardening, not a requirement.

**Phases** (detailed steps written as each predecessor lands, per the plan's convention):

6. **GitHub App connection.** Goal: an admin creates and installs a Connapse GitHub App from the Providers page. Done when: `ConnectionProvider.GitHub` connections are created through the manifest flow (App id, private key, secrets stored encrypted), installations are listed, a JWT → installation-token client exists with token caching, and a live test records whether an installation token reads *uninstalled* public repositories at the installation rate and whether a zero-repository install is possible. The Providers page gets a GitHub step card for this — real identity setup.
   **Phase 6 detail (written 2026-09-23).** The App mirrors the AWS split: one provider-level identity, and connections that narrow from it.
   - *Storage.* The App is the `github` row in `provider_credentials`: new nullable `config_json` (App id, slug, client id, owner, html URL) and `secret_protected` (client secret) columns, with the private key in the existing `private_key_protected`. All secrets use the `ProviderCredential.v1` protector. New store methods `SaveGitHubAppAsync` / `GetGitHubAppAsync` / `GetGitHubAppMaterialAsync`; the key never appears on the read model.
   - *Tokens.* `ConnapseGitHubApp` (a singleton, reaching the scoped store through `IServiceScopeFactory`, like `ConnapseAwsCredentials`) builds the App JWT (RS256 via `RSA.SignData`, no new package, `iat` 60 s back, 9-minute expiry), lists installations, and mints installation tokens, cached per installation until 5 minutes before expiry. `ClearCache()` runs after a save or reset.
   - *Manifest flow.* The Providers page renders a native form that POSTs the manifest to github.com (personal account, or an organisation the admin names), with a random `state` held 60 minutes in memory against the admin who started it. GitHub redirects to `GET /api/v1/providers/github/manifest/callback`, which requires an admin, checks that `state` belongs to the same admin, exchanges the code at `POST /app-manifests/{code}/conversions`, saves the App, and redirects back to the page. The manifest asks for read-only metadata, contents, issues, and pull requests, with webhooks off.
   - *Manual values.* An App id plus a private key PEM, for an App registered by hand; validated by calling `GET /app` before saving.
   - *Providers page.* A `github` card built on `ProviderStepCard` (Easy = create through GitHub; Manual = paste values), showing the App, an install link, and its installations; `ProviderSetupReader` reports it.
   - *Connections.* A GitHub connection is one installation: config `{ "installationId", "account" }`, no secret. The Connections page offers GitHub, lists installations to pick from, and tests by minting a token.
   - *Live checks deferred to the user:* whether an installation token reads public repositories outside the installation at the installation rate, and whether a zero-repository install is possible. Both need a real App.
   - **Status — CODE COMPLETE (2026-09-23).** Built as described: `ConnapseGitHubApp` (JWT, installations, cached tokens, manifest conversion), `provider_credentials` `config_json`/`secret_protected` columns with the shape CHECK widened to "exactly one complete Roles Anywhere *or* GitHub App row" (migration `AddGitHubAppProviderCredential`), the manifest callback (`GitHubAppEndpoints`, admin-only, state bound to the starting admin, outcomes as `github_created` / `github_error` so Azure's `error` bounce is untouched, the one-time code never logged), the Providers card, `ProviderSetupReader` GitHub entry, and GitHub installation connections. Verified: unit suite green (+8 App client, +9 manifest/state/form, +4 reader); integration green for the store round-trip, the CHECK refusing partial rows, the callback refusing a planted state and anonymous callers, and DI. **Not verified:** the manifest flow against real github.com and both live checks above — they need the user to create an App.

7. **Connector on installation tokens.** Goal: the GitHub connector never reads anonymously. Done when: API calls and git clones (`x-access-token`) use installation tokens; a server-wide credential pool and budget reads `X-RateLimit-*` from every response, serialises requests, and backs off in GitHub's documented order; public reads fail over across installations, private reads stay pinned; anonymous mode is deleted.
   **Phase 7 status — CODE COMPLETE (2026-09-23).** `GitHubCredentialPool` (singleton) tracks each installation's budget from `x-ratelimit-*` / `retry-after`, prefers the source's own installation, otherwise the one with the most left; a spent one sits out until its reset, a refused one (401) for five minutes; all spent → `GitHubRateLimitedException` with the earliest reset, which the issues sync already treats as "keep progress, resume". `GitHubAuth` (pool + `GitHubAccess.Public`/`Pinned`) flows into `GitHubApiClient`, which retries a rate-limited or refused request on the next installation, and into the docs fetch as `x-access-token`. **`GitHubRepositoryGuard`** makes every authenticated sync confirm the repository is still *public* (`visibility == "public"`; private and internal refused) before reading — a token can read private repositories, and until Phase 10 anything indexed is visible to everyone; a refusal hides the source (Phase 2's revoked-access path). The factory builds GitHub connectors only on a GitHub connection (`installationId` from its config, `RequirePublic` on) and refuses connection-less GitHub sources with a message; the REST create endpoint refuses them with 400. Serialising requests server-wide was not added: `SyncAllAsync` already runs sources one at a time and each source pages sequentially. Verified: unit suite green (+11 pool/auth/guard tests, factory tests reworked for connections); integration 70/71 (the known embedding-backend failure).

8. **Conditional-request probes.** Goal: an idle repository costs no primary budget. Done when: fixed-parameter probes on issues, issue comments, and review comments gate the `since` fetch on an ETag change, with tests showing an idle cycle spends only 304s.
9. **Add public repositories on an App connection.** Goal: an admin adds a public repository from New source by choosing a GitHub App connection. Done when: the dialog accepts a public repository address, refuses a private one with a message pointing at Phase 10, creates the docs and issues sources on that connection, and existing connection-less GitHub sources have a documented migration or are refused with a clear message.
10. **Private repositories with per-user permission filtering.** Goal: private repositories can be added, and search shows their documents only to users GitHub says can read them. Done when: the deferred design from the 2026-09-09 research is built — user-to-server identity linking, live per-repository permission checks at query time, fail-closed — and verified by tests *before* the dialog offers private repositories; the open `metadata:read` vs push question for the collaborator-permission endpoint is settled by a live test.
   **Requirement (user, 2026-09-23): access is per user, per repository — never per org.** An org member typically sees only some of the org's repositories (teams, direct grants, base permission), so org membership must never stand in for repository access. Signing in through the App only establishes *which GitHub account* a Connapse user is; whether they see a repository's documents is GitHub's answer for that user on that repository, which already folds in team, direct, and base-permission grants. A member in 3 of 200 repositories sees results from those 3 only.
   **Consequence for App ownership (research 2026-09-23).** A private App can be authorized only by members of its owning account, so a private App on a personal account lets nobody but its owner sign in — Phase 10 would be unusable. A private org-owned App covers members of that org (outside collaborators probably cannot sign in — unverified); a public App covers everyone. Phase 6's Easy flow must therefore check that a named org exists before posting the manifest, and make the App public when no org is named. Sourcegraph (the closest analogue) offers optional org + public/private, private by default.

---

## Self-review

**Spec coverage:** docs sync (Phase 2), issues/PRs sync + comment assembly + edges (Phase 3), record chunker (Phase 4), connection-less public sources (Phase 1), public-to-private withholding (Phase 2, open item), admin page + add-repository flow (Phase 5), graph edges as metadata (Phase 3). Two-sources-per-repo is realized by Phases 1/5. The deferred App/private/permission scope is explicitly out and not planned. Covered.

**Placeholder scan:** Phases 1 and 4 are fully bite-sized with real code. Phases 2, 3, and 5 are task outlines whose detailed steps are deliberately deferred per the project's stacked-phase convention (later tests depend on the API the prior phase produces) — this is a stated convention, not a gap; each carries files, interfaces, and test intent.

**Type consistency:** `ConnectionProvider.GitHub`/`ConnectorType.GitHub` = 6 (aligned); `Source.ConnectionId` `Guid?` and `Source.Provider` `ConnectionProvider?` used consistently in store, factory overload `Create(Source)`, and endpoint DTO; chunker `Name` string `"Record"` equals `ChunkingStrategy.Record.ToString()`; `ChunkInfo` (not `Chunk`) is the chunk DTO; `SyncDelta(Upserted, DeletedPaths, NextCursor, RequiresFullResync)` matches the interface.

## Open items carried from the spec
1. ~~Public-to-private returns 404 unauthenticated — confirm in Phase 2.~~ Resolved in Phase 2: detected at the git transport (anonymous 401), no REST call.
2. ~~Unauthenticated GraphQL/timeline edge-capture budget under 60/hour — confirm in Phase 3, fall back to deferred backfill.~~ Resolved in Phase 3: GraphQL refuses anonymous callers; timeline and PR-files edges go to the backfill.
