# Design: Split managed containers from external sources

**Date:** 2026-08-13
**Status:** Approved (design), pending implementation plan

## Problem

Every container in Connapse — whether it is Connapse's own managed storage or a mirror of an
external system like S3, Azure Blob, or a local filesystem — is presented identically. All of
them appear in one grid on `Home.razor` and open the same file browser
(`FileBrowser.razor`, ~1,900 lines). Three consequences, all confirmed as motivating:

1. **It implies ownership Connapse does not have.** Rendering an S3 bucket as a browsable folder
   tree tells the user these are Connapse's files. They are someone else's, mirrored at best.
2. **It leaks permissions.** The file tree exposes every synced object to any Connapse user,
   regardless of whether that user could see the object in the source system. Browsing is a far
   wider exposure surface than search results, which already pass through `CloudScopeService`.
3. **It clutters the mental model.** Synced buckets compete with real managed containers in the
   same list, so a user looking for their own files wades past infrastructure.

Note that write access is *already* gated: `ContainerWriteGuard` blocks upload, delete, and
create-folder on S3 and Azure Blob containers, and honours per-container permission flags for
Filesystem. The gap is presentation, not mutation.

## Solution overview

Split the single `Container` concept into three:

- **Connection** — an admin-registered credential and endpoint for a provider. Lives in
  Settings. One connection backs many sources.
- **Source** — a specific scope inside a connection that Connapse ingests (a bucket prefix, a
  Drive folder, a Confluence space, a filesystem subpath). Read-only. Surfaced in a new Sources
  tab showing sync status only — never a file listing. Searchable.
- **Container** — Connapse's own managed storage. Browsable, mutable, the only thing that keeps
  a folder tree.

Authentication is deliberately separated from the source itself: an admin registers a connection
once in Settings, and sources select from a dropdown rather than re-entering credentials.

## Decisions and rationale

**Sources show config-free sync status only, no file listing.** The smallest surface that still
makes the tab useful. It removes the ownership framing and the browse-level permission leak in
one move, and leaves per-user scope enforcement concentrated on the search path where it already
exists. Accepted residual risk: an aggregate document count is a weak signal about a source's
contents. Judged an acceptable trade for the tab being useful at all.

**A separate `sources` table rather than a `Kind` discriminator on `containers`.** A discriminator
was considered first and rejected. It leaves a source carrying fields it must never use — most
visibly `Folders`, the browse tree this work exists to delete, plus upload settings overrides and
a meaningless `ConnectorType`. Worse, it forces at least five source-only columns (connection FK,
sync cursor, last-synced timestamp, sync status, sync error) onto `containers`, where they are
permanently NULL for managed rows. The decisive problem is that a discriminator lets the schema
express nonsense — `Kind=Managed, ConnectorType=S3`, or a source row with folders attached — so
every endpoint must defensively re-check what the type system should have guaranteed.

**Filesystem becomes a source and loses its write flags.** `AllowUpload`, `AllowDelete`, and
`AllowCreateFolder` are deleted along with their guard branches. The rule becomes uniform with no
exceptions: if Connapse does not own the bytes, you do not mutate them through Connapse.

**Sources are listed alongside containers in the MCP and REST contracts.** `container_list` and
`search_knowledge` continue to accept any owner id, with a `kind` field in the response
distinguishing managed from source. Existing agent prompts and the CLI keep working with no
breaking change; a source is simply a searchable knowledge scope that happens to be read-only.

This does not contradict the no-file-listing decision above. Listing a source as a searchable
*scope* — its name, kind, and summary — is not the same as enumerating the documents inside it.
No REST or MCP endpoint returns a file listing for a source, and `list_files` remains
container-only.

**Additive-then-cutover migration rather than a big bang.** The backfill is the only genuinely
irreversible step, and this is the only sequencing that lands it while the old read path still
works — so a bad backfill is a rollback rather than an outage. It also fits the project's
under-300-line PR norm along real phase boundaries instead of artificial splits.

## Data model

### `connections` (new)

| Column | Notes |
|---|---|
| `Id` | Guid PK |
| `Name` | Display name |
| `Provider` | S3 \| AzureBlob \| Filesystem, plus future providers. Reuses the existing `ConnectorType` enum minus `ManagedStorage`, which is never a connection — managed storage is Connapse's own backend, not an external system it authenticates to. The enum itself survives phase 5; only the `containers.connector_type` *column* is dropped. |
| `ConfigJson` | JSONB, non-secret settings (region, endpoint, account name) |
| `SecretProtected` | DataProtection-encrypted, protector purpose `"Connection.v1"`, matching the existing `CloudIdentity.v1` pattern in `CloudIdentityService` |
| `CreatedByUserId` | Audit |
| `CreatedAt`, `UpdatedAt` | Audit |

Filesystem is a degenerate connection with no secret. It is kept in the table anyway because it
gives a place to declare which host roots are mountable at all, so a source may only select a
subpath beneath an approved root. This is a security improvement over the current design, where
a root path is free text per container.

### `sources` (new)

| Column | Notes |
|---|---|
| `Id` | Guid PK |
| `Name`, `Description` | Display |
| `ConnectionId` | FK to `connections`, RESTRICT on delete |
| `ScopeJson` | JSONB selector: bucket prefix, Drive folder, space key, root subpath |
| `SettingsOverridesJson` | Chunking, embedding, search only — no upload overrides |
| `SyncCursor` | Nullable text; durable delta token |
| `LastSyncedAt` | Nullable |
| `LastSyncStatus` | Never \| Running \| Succeeded \| Failed |
| `LastSyncError` | Nullable text |
| `SyncIntervalSeconds` | Poll cadence |
| `Summary`, `SummaryGeneratedAt`, `SummaryDocSetHash` | Agent routing, same as containers |
| `CreatedAt`, `UpdatedAt` | Audit |

### `containers` (existing)

Shape is unchanged through phases 1–4. In phase 5, `ConnectorType` and `ConnectorConfig` are
dropped: every remaining row is managed storage, so the columns no longer carry information.
Folders remain, and remain exclusive to containers.

### `documents` — bridging both owners

Add a nullable `source_id` FK alongside the existing `container_id`, with a CHECK constraint that
exactly one is set, preserving referential integrity in both directions. Then add a generated
column so the search path never has to classify its owner:

```sql
owner_id uuid GENERATED ALWAYS AS (COALESCE(container_id, source_id)) STORED
```

Index `owner_id`. Search filters on it. Ingestion writes whichever of the two real columns
applies.

### `chunks` and `chunk_vectors` — rename, do not add

Both `ChunkEntity` and `ChunkVectorEntity` carry a **denormalized `ContainerId`** alongside
`DocumentId`, so they cannot be left untouched. They must not get the same two-column treatment
as `documents`, however: `chunk_vectors` is the largest table in the system and carries the
IVFFlat partial indexes, so adding a column and backfilling it would mean a table rewrite and an
index rebuild.

Instead, rename `container_id` to `owner_id` on both tables and drop the foreign key to
`containers`, widening the column's meaning to "the container or source that owns this chunk."
In PostgreSQL, `ALTER TABLE ... RENAME COLUMN` is a catalog-only change and dropping a constraint
is cheap, so this is effectively instant regardless of row count and requires no vector reindex.

The cost is that `chunks` and `chunk_vectors` lose database-level referential integrity to their
owner. That is acceptable because they already cascade from `documents`, which retains full
integrity via the CHECK constraint above — an orphaned chunk would require an orphaned document
first.

## Connector contract changes

**`IConnector` sheds its write methods.** `WriteFileAsync` and `DeleteFileAsync` move to a new
`IWritableConnector : IConnector`, implemented only by `MinioConnector`. A source becomes
incapable of being written to, rather than being told not to at runtime.

**`ContainerWriteGuard` is deleted** (~120 lines plus `ContainerWriteGuardTests`). It exists only
because managed containers and external connectors share one set of endpoints, forcing every
write to ask "what kind am I?" at runtime. Once containers and sources have separate endpoints,
routing answers that question.

**Cursor-based sync is added to the contract:**

```csharp
public interface ISyncCursorConnector : IConnector
{
    Task<SyncDelta> GetChangesAsync(string? cursor, CancellationToken ct);
}

public record SyncDelta(
    IReadOnlyList<ConnectorFile> Upserted,
    IReadOnlyList<string> DeletedPaths,
    string? NextCursor,
    bool RequiresFullResync);
```

Connectors that cannot do deltas simply do not implement it. `RequiresFullResync` exists because
the strongest provider APIs demand explicit resync handling — Microsoft Graph returns `HTTP 410
Gone` and Dropbox returns a `409 reset` when a cursor goes stale — and ignoring that produces a
corpus that silently drifts out of sync.

**`ConnectorWatcherService` becomes `SourceSyncService`**, re-keyed from containers to sources.
Its existing list-everything-and-diff-a-snapshot logic is retained as the explicit fallback for
connectors without cursor support, rather than being the only path. `SyncCursor` on the source row
makes the delta path durable across restarts, which the in-memory snapshot never was. S3 and
Azure Blob remain on the fallback path; neither offers a real delta API.

## UI surface

- **Nav** gains a "Sources" entry beside "Files". Settings gains a "Connections" tab.
- **`Home.razor` gets simpler.** The create-container modal stops asking which connector to use
  and drops its S3/Azure/Filesystem config fields and Test Connection button, because creating a
  container now always means managed storage. That configuration moves to the Connections tab,
  reusing the existing `S3ConnectionTester` and `AzureBlobConnectionTester` unchanged.
- **New `Sources.razor`** lists sources with name, connection, scope, last sync time, status,
  document count, and errors. Actions: sync now, edit scope, enable/disable, delete.
- **`FileBrowser.razor`** stays but serves only managed containers, allowing some of its read-only
  branching to be removed.
- **`Search.razor`** gains an owner-kind badge on results and a source facet for scoping queries.

## Phases

Each phase is independently shippable with a stated done-condition.

1. **Schema and stores.** Create `connections` and `sources`; add `documents.source_id`, the CHECK
   constraint, and the generated `owner_id`; add `IConnectionStore` and `ISourceStore`. Nothing
   reads them yet. *Done when:* migrations apply cleanly and store unit tests pass.
2. **Backfill and compat read.** Migrate every non-managed container into a connection plus source
   pair, repoint its documents to `source_id`, and delete its folder rows. Container endpoints
   keep serving them through a compatibility read during the window. *Done when:* an install with
   pre-existing S3, Azure Blob, and Filesystem containers returns identical search results before
   and after.
3. **Contract and sync engine.** Introduce `IWritableConnector` and `ISyncCursorConnector`, build
   `SourceSyncService`, delete `ContainerWriteGuard`, add `/api/sources`, add the `kind`
   discriminator to the MCP and REST responses. *Done when:* sync runs off source rows and cursors
   survive a process restart.
4. **UI.** Sources tab, Connections settings tab, `Home.razor` simplification, Search facet.
   *Done when:* no external connector is reachable through the file browser.
5. **Cleanup.** Remove the compatibility shims; drop `containers.connector_type` and
   `containers.connector_config`. *Done when:* the compat read path no longer exists.

Phase 5 must be assigned to a specific milestone during planning rather than left open-ended. The
compat window in phases 2–4 is the one genuinely awkward state in this design, and it should span
one release, not several.

## Failure handling

- **Sync failures** are recorded on the source row (`LastSyncStatus`, `LastSyncError`) and rendered
  in the Sources tab. They never surface as a UI exception. Repeated failures back off.
- **Stale cursor** sets `RequiresFullResync`, which resets `SyncCursor` and falls back to a full
  list for that cycle.
- **Deleting a referenced connection** is blocked by FK RESTRICT, with a message naming how many
  sources depend on it.
- **Backfill failure** in phase 2 is transactional per container and resumable, so a partial run
  is not a corrupt state.
- **Secret decryption failure** after DataProtection key rotation puts the source in a failed state
  with a reconnect prompt, rather than a crash loop.

## Testing

- **Unit:** store CRUD for connections and sources; scope-selector validation; `SyncDelta`
  application covering upserts, deletes, and the resync branch; connection secret encrypt/decrypt
  round-trip; generated `owner_id` correctness.
- **Migration:** one integration test that seeds an old-shape database, runs the phase-2 backfill,
  and asserts document counts, vector counts, and search parity. This is the test that makes
  phase 2 safe to ship.
- **Integration:** via the existing `SharedWebAppFixture`, assert that sources expose no write
  route at all, and that after phase 5 container endpoints no longer return sources.
- **Deleted:** `ContainerWriteGuardTests`, alongside the guard itself.

## Out of scope

- Building any new connector (Google Drive, Microsoft Graph, Confluence). This design makes them
  cheaper to add; it does not add them. See
  `docs/research/highest-value-connectors-2026-08-13.md` for the prioritisation.
- Per-user ACL modelling for source content beyond the existing `CloudScopeService` search-path
  enforcement.
- Changes to chunking, embedding, or ranking.
- Federated or pass-through search that does not persist content locally.

## Resolved during planning

- **`chunk_vectors` does need a schema change.** Both `chunks` and `chunk_vectors` denormalize
  `container_id`. Resolved by renaming the column to `owner_id` and dropping its FK rather than
  adding a parallel column — see the data model section.
- **Phase 5 lands in v0.4.0**, the same milestone as phases 1–4, so the compatibility window spans
  a single release as intended.
- **`SyncIntervalSeconds` is nullable per-source and falls back to a connection-level default.**
  Most installs will set a cadence once per connection; a noisy or expensive source can override
  it without a second configuration concept.
