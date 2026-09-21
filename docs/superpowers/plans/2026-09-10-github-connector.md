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
- This epic is **public repositories only, unauthenticated**. No GitHub App, no private repos, no identity linking, no permission filter, no `Connapse.Identity` changes.

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
- Modify `ConnectorFactory.cs` — build `GitHubConnector` for docs kind.
- Modify `src/Connapse.Storage/Connapse.Storage.csproj` — add `LibGit2Sharp`, `Octokit`.

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

**Detailed steps to be written once Phase 1 lands** (per project convention). Shape:

### Task 2.1: Add NuGet dependencies
- Add `LibGit2Sharp` and `Octokit` to `src/Connapse.Storage/Connapse.Storage.csproj` (pin exact versions). Build.

### Task 2.2: `GitHubConnectorConfig`
- Create the config record: `Owner`, `Repo`, `RepoId` (long, nullable until first sync), `Kind` (`Docs` | `IssuesAndPullRequests`), `Host` (default `github.com`, hard-pinned for connection-less), `IncludePatterns`/`ExcludePatterns` (docs; default markdown globs). Mirror `S3ConnectorConfig`/`SftpConnectorConfig` style. Unit-test parse from scope JSON.

### Task 2.3: `GitHubConnector` capability surface + docs `ISyncCursorConnector`
- `class GitHubConnector : ISyncCursorConnector, IDisposable`. `Type => ConnectorType.GitHub`, `SupportsLiveWatch => false`, `WatchAsync` throws.
- `GetChangesAsync(cursor)`: shallow-clone (or fetch) the repo with LibGit2Sharp; if `cursor` (a SHA) is null, emit all matching files as upserts with `NextCursor = headSha`; else diff `cursor..head` for added/modified/removed matching the globs, emit `SyncDelta(upserted, deletedPaths, headSha, RequiresFullResync: cursorShaMissing)`. `RequiresFullResync: true` when the base SHA is absent (force-push/history rewrite).
- `ListFilesAsync`: full-tree fallback (list all matching files at head).
- `ReadFileAsync(path)`: return the file content. Decide read mechanism to avoid re-clone on the pipeline's per-file read (the pipeline re-creates the connector per document): read via `raw.githubusercontent.com/{owner}/{repo}/{sha}/{path}` over `HttpClient` (off the 60/hour API budget), keyed by the source's current SHA. Record the decision in the phase summary.
- `Path` convention: repo-relative with a leading slash; `ResolveJobPath` returns the path unchanged; `ReadFileAsync` accepts exactly that path (the pipeline calls it without `ResolveJobPath` for source-owned docs).
- **Open item (resolve here):** confirm an unauthenticated `GET https://api.github.com/repos/{o}/{r}` returns 404 once private; use that as the public-to-private re-check, withholding on 404.

### Task 2.4: Wire the factory
- In `ConnectorFactory.Create(Source)`'s `GitHub` arm, build `GitHubConnector` from `source.ScopeJson` (kind `Docs` for this phase). Ensure any `HttpClient` use is singleton-safe (the factory is a singleton) via an injected `IHttpClientFactory` (register it; `Microsoft.Extensions.Http` is already referenced).

### Task 2.5: Integration test
- Following `S3ConnectorIntegrationTests`/`SourceSyncIntegrationTests`: point a docs source at a tiny public repo (or a local bare-repo fixture to avoid network flakiness in CI), run a sync cycle through `SourceSyncService` via the `IConnectorFactory` seam, assert the expected markdown files ingested and the SHA cursor advanced, and that a second cycle with an unchanged head is a no-op.

### Task 2.6: Phase-exit verification + plan update.

---

## Phase 3 — Issues-and-pull-requests source

**Goal:** A GitHub issues-and-PRs source syncs issues and pull requests (with comments assembled) as markdown records, cursor is an updated-at timestamp, with graph edges stored as metadata. Done-condition: integration test against a fixture/public repo ingests records with assembled comments and edge metadata, and the updated-at cursor advances with same-second dedup.

**Detailed steps to be written once Phase 2 lands.** Shape:

### Task 3.1: `GitHubRecordRenderer`
- Assemble one markdown document per issue/PR: field header (number, title, author, state, labels, dates), body, then comments joined `--- Comment by {login} ({date}) ---`. Unit-test the rendering against sample Octokit models. Comments included by default (the chunker experiment).

### Task 3.2: Issues/PRs branch of `GitHubConnector.GetChangesAsync`
- Page `GET /repos/{o}/{r}/issues?since=<ts>&state=all&sort=updated&direction=asc&per_page=100` via Octokit (unauthenticated); rows with a `pull_request` field are PRs, hydrated via the pulls API; review comments via the since-capable endpoint. `NextCursor` = max `updated_at` seen; re-request `>=` with an id-dedup set for same-second ties. Virtual paths `/issues/{n}`, `/pulls/{n}`. `ReadFileAsync` re-fetches and re-renders the record by number.
- Deletion reconciliation: a since-sweep cannot see hard deletes, so schedule a periodic full re-list (page all `state=all`, diff ids) — document the cadence.

### Task 3.3: Edge capture as metadata
- Capture `closes`/`closed_by`, `cross_referenced`, `connected`/`disconnected` (explicit link/unlink timeline events), sub-issue `parent`/`children`, `files` touched, labels, milestone into the document metadata dictionary — the full edge set from the spec's edge table. Use REST timeline and a hand-rolled GraphQL POST (installation-token-free, unauthenticated) for `closingIssuesReferences`. Assert `connected`/`disconnected` alongside the other edges in the integration test; if the unauthenticated timeline budget forces deferral (Open item 2), capture them in the later edge-backfill rather than dropping them.
- **Open item (resolve here):** confirm the unauthenticated GraphQL/timeline budget is workable for a medium repo under 60/hour; if not, defer edge capture per record and backfill later.

### Task 3.4: Integration test + Task 3.5: Phase-exit verification + plan update.

---

## Phase 4 — Record chunker

**Goal:** `ChunkingStrategy.Record` chunks a record as one chunk unless oversized, then splits into per-comment child chunks carrying the parent header. Done-condition: unit tests green; issues/PRs sources chunk with `Record`; docs sources still use `DocumentAware`.

### Task 4.1: Add the enum member
**Files:** Modify `src/Connapse.Core/Models/IngestionModels.cs:49`; Modify `src/Connapse.Core/Models/SettingsModels.cs:81-82` (doc comment).
- [ ] Add `Record` to `ChunkingStrategy` so `ChunkingStrategy.Record.ToString() == "Record"`. Add `"Record"` to the `Strategy` doc comment. Build.

### Task 4.2: `RecordChunker`
**Files:** Create `src/Connapse.Ingestion/Chunking/RecordChunker.cs`; Test `tests/Connapse.Ingestion.Tests/Chunking/RecordChunkerTests.cs`.

**Interfaces:**
- Produces: `class RecordChunker(ITokenCounter tokenCounter, RecursiveChunker recursiveChunker) : IChunkingStrategy` with `Name => "Record"`, returning `IReadOnlyList<ChunkInfo>`.

- [ ] **Step 1: Failing test** — mirror `FixedSizeChunkerTests`: a small record returns a single chunk whose `Content` is the whole record and whose metadata has `["ChunkingStrategy"]="Record"`.
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
- [ ] **Step 2: Run, verify fails.**
- [ ] **Step 3: Implement** — if `CountTokens(content) <= settings.MaxChunkSize`, return one `ChunkInfo` (whole content, metadata `ChunkingStrategy`/`ChunkIndex`). Else split on the comment delimiter into child chunks, each prefixed with the field-header lines (the text before the first delimiter), delegating any still-oversized child to `recursiveChunker`. Mirror `DocumentAwareChunker`'s `BuildChunk`/fallback pattern.
- [ ] **Step 4: Run, verify PASS** (add an oversized-record test that asserts multiple chunks each containing the header).
- [ ] **Step 5: Commit.**

### Task 4.3: Register + select
**Files:** Modify `src/Connapse.Ingestion/Extensions/ServiceCollectionExtensions.cs:37-45`.
- [ ] Register `services.AddSingleton<IChunkingStrategy, RecordChunker>();` (RecursiveChunker already double-registered at :40-41). The pipeline matches by `Name` against `ChunkingStrategy.ToString()`, so no resolver change is needed; issues/PRs sources set their per-source chunking override to `Record` (Phase 3/5). Build + run unit tests. Commit.

### Task 4.4: Phase-exit verification + plan update.

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

---

## Self-review

**Spec coverage:** docs sync (Phase 2), issues/PRs sync + comment assembly + edges (Phase 3), record chunker (Phase 4), connection-less public sources (Phase 1), public-to-private withholding (Phase 2, open item), admin page + add-repository flow (Phase 5), graph edges as metadata (Phase 3). Two-sources-per-repo is realized by Phases 1/5. The deferred App/private/permission scope is explicitly out and not planned. Covered.

**Placeholder scan:** Phases 1 and 4 are fully bite-sized with real code. Phases 2, 3, and 5 are task outlines whose detailed steps are deliberately deferred per the project's stacked-phase convention (later tests depend on the API the prior phase produces) — this is a stated convention, not a gap; each carries files, interfaces, and test intent.

**Type consistency:** `ConnectionProvider.GitHub`/`ConnectorType.GitHub` = 6 (aligned); `Source.ConnectionId` `Guid?` and `Source.Provider` `ConnectionProvider?` used consistently in store, factory overload `Create(Source)`, and endpoint DTO; chunker `Name` string `"Record"` equals `ChunkingStrategy.Record.ToString()`; `ChunkInfo` (not `Chunk`) is the chunk DTO; `SyncDelta(Upserted, DeletedPaths, NextCursor, RequiresFullResync)` matches the interface.

## Open items carried from the spec
1. Public-to-private returns 404 unauthenticated — confirm in Phase 2.
2. Unauthenticated GraphQL/timeline edge-capture budget under 60/hour — confirm in Phase 3, fall back to deferred backfill.
