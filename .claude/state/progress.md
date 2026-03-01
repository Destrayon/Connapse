# Progress

Current status and recent work. Update at end of each session.

---

## Current Status (2026-02-28) — v0.3.0 Session B complete

**Branch:** `feature/0.3.0` | **Last shipped:** v0.2.2

### v0.3.0 Plan — ready to implement

Full plan at [docs/v0.3.0-plan.md](../../docs/v0.3.0-plan.md). Key decisions in [decisions.md](decisions.md).

| Session | Focus | Status |
|---------|-------|--------|
| A | IConnector abstraction + schema migration + IContainerSettingsResolver | **COMPLETE** |
| B | MinIO as IConnector + Filesystem connector + InMemory connector + ConnectorWatcherService | **COMPLETE** |
| C | S3 connector (DefaultAWSCredentials / IAM) | Pending |
| D | AzureBlob connector (DefaultAzureCredential) | Pending |
| E | Cloud RBAC — AWS OIDC federation + Azure OAuth2 identity linking | Pending |
| F | OpenAI + Azure OpenAI embedding providers; ILlmProvider formalization | Pending |
| G | Agentic search (SearchMode.Agentic, iterative LLM-driven retrieval) | Pending |
| H | Testing + docs | Pending |

---

## Shipped Versions

| Version | Key feature | Sessions |
|---------|-------------|----------|
| v0.2.2 | CLI self-update (`connapse update`, passive notification, Windows bat swap) | 19 |
| v0.2.0 | Security & auth: Identity project, cookie+PAT+JWT, RBAC, audit logging, agent entities, CLI auth, 256 tests | 8–18 |
| v0.1.0 | Container file browser, hybrid search, ingestion pipeline, MCP server | 1–7 |

**Test baseline (v0.2.2):** 256 tests across 12 projects, all passing.
**After Session B:** 95 unit tests pass (19 Core + 25 Identity + 51 Ingestion). Build: 0 warnings, 0 errors.

---

## Session C Bug Fix (2026-02-28) — Filesystem connector UI

**Problem**: Filesystem containers showed empty in the file browser, uploads went to the wrong location, and creating folders didn't create directories on disk.

**Root causes & fixes**:
1. `FetchBrowseEntries` read from `FolderStore`/`DocumentStore`, but Filesystem connector stores documents with **absolute OS paths** — path comparison against virtual `/` always failed. **Fix**: For Filesystem containers, `FetchBrowseEntries` now enumerates the actual disk directory via the connector and overlays document status from the DB.
2. Upload endpoint used global `IKnowledgeFileSystem` (MinIO/local root) instead of the container's connector rootPath. **Fix**: Detect `ConnectorType.Filesystem`, use `FilesystemConnector.WriteFileAsync`; the ingestion job stores the absolute path so `IngestionWorker` can read it via the connector.
3. `CreateFolder` only created a DB `Folder` record. **Fix**: For Filesystem containers, `Directory.CreateDirectory` is called instead.
4. `DeleteFileEntry`/`DeleteFolderEntry` called `IKnowledgeFileSystem.DeleteAsync` with a virtual path which resolves wrong. **Fix**: For Filesystem containers, use the connector's `DeleteFileAsync` / `Directory.Delete`.
5. `ConnectorWatcherService.HandleFileEventAsync` for Created/Changed events could create duplicate ingestion jobs when a file was just uploaded via the UI. **Fix**: Check `GetByPathAsync` — skip if status is Pending/Queued/Processing; reuse existing doc ID otherwise.

---

## Session C2 (2026-02-28) — Real-time file list + ingestion status in FileBrowser

**Feature**: Filesystem container file list now updates in real time without polling.

**Changes made**:
1. `IngestionJobStatus` + `IngestionProgressUpdate` — added `DocumentId` and `ContainerId` fields so progress events carry enough context to route updates to the right UI entry regardless of origin.
2. `IngestionQueue.EnqueueAsync` — passes `job.DocumentId` and `job.Options.ContainerId` into the status record.
3. `IngestionProgressBroadcaster` — forwards the new fields through to the SignalR/in-process broadcast.
4. **New**: `FileBrowserChangeNotifier` singleton (event bus) — `ConnectorWatcherService` calls it on every file add/delete event after enqueue/delete.
5. `ConnectorWatcherService` — caches root paths in `_rootPaths` dict, fires `NotifyAdded`/`NotifyDeleted` after each watcher event.
6. `Program.cs` — registers `FileBrowserChangeNotifier` as singleton.
7. `FileBrowser.razor` — subscribes to `FileChangeNotifier.FileChanged`; `OnFileChanged` inserts/removes/updates entries in place. `HandleIngestionProgress` now falls back to `progress.DocumentId` for watcher-originated jobs (not only UI-upload tracked ones).

**Result**: Drop a file into the watched folder → it appears in the UI as "Queued" within ~750 ms. Edit a file → status flips to "Processing", then back to "Ready". Delete a file → entry disappears immediately.

---

## Session C3 (2026-02-28) — Filesystem watcher re-index failure

**Bug**: When a file in a watched Filesystem container was changed, the job went Queued → Processing → Failed.

**Root causes**:
1. **`Renamed` event handler** didn't check for an existing document at the new path. Editors that use atomic saves (VS Code, Notepad++, Word, etc.) rename a temp file over the target, generating a `Renamed` event. The handler called `EnqueueIngestionAsync` without an `existingDocumentId`, so the pipeline tried to INSERT a new row with the same `(ContainerId, Path)` — hitting the unique constraint → `"Ingestion failed: 23505: duplicate key value violates unique constraint"`.
2. **`IngestionPipeline` reindex path** added new chunks without deleting old ones — every re-index doubled the chunk count, polluting search results.

**Fixes**:
1. `ConnectorWatcherService.HandleFileEventAsync` — `Renamed` case now calls `GetByPathAsync` and reuses the existing document ID (same logic as `Created/Changed`). Also guards against in-flight jobs.
2. `IngestionPipeline.IngestAsync` — after updating the existing document entity and before adding new chunks, calls `ExecuteDeleteAsync` to bulk-delete stale chunks (cascade-deletes vectors via FK).

---

## Session C4 (2026-02-28) — Filesystem UI permission toggles

**Feature**: Per-container setting for Filesystem connectors to disable delete, upload, or create folder actions in the UI.

**Changes made**:
1. `FilesystemConnectorConfig` — added `AllowDelete`, `AllowUpload`, `AllowCreateFolder` (all default `true`).
2. `IContainerStore` — added `UpdateConnectorConfigAsync(Guid id, string? connectorConfig, ct)`.
3. `PostgresContainerStore` — implemented the new method.
4. `FileBrowser.razor`:
   - Parses `FilesystemConnectorConfig` from `container.ConnectorConfig` after loading (`ParseFilesystemConfig` helper).
   - Computed properties: `AllowUpload`, `AllowDelete`, `AllowCreateFolder`.
   - Upload button, New Folder button, delete row actions, bulk-delete toolbar, detail-panel delete, and all related modals now gated on the respective permission flag.
   - Drag-and-drop JS init also gated on `AllowUpload`.
   - Settings tab → "UI Permissions" section (only shown for Filesystem containers): three toggle switches.
   - `LoadSettings` populates toggles from current config; `SaveSettings` persists changes via `UpdateConnectorConfigAsync`.

**Backward compat**: All three flags default to `true` — existing Filesystem containers with no permission fields in their JSON are unaffected (all actions remain enabled).

---

## Session C5 (2026-03-01) — Filesystem path bug + timing investigation

**Bugs fixed**:
1. **`ContainerEntity.ConnectorConfig` 22P02** — changed from `string?` to `JsonDocument?` (Npgsql 10 + `EnableDynamicJson()` treats string→jsonb as JSON serialization; Windows paths with `\U` are invalid JSON escapes). Updated `PostgresContainerStore`, `ContainerSettingsResolver`, `KnowledgeDbContextModelSnapshot`, added JSON validation in `ContainersEndpoints`.
2. **Filesystem connector absolute path storage** — `ConnectorWatcherService.EnqueueIngestionAsync` was passing the absolute path (`C:\...\file.md`) as `IngestionOptions.Path`, which `IngestionPipeline` stored in the DB. The file browser API filtered by virtual paths and returned `[]`. Fixed: `EnqueueIngestionAsync` now requires an explicit `virtualPath`; `InitialSyncAsync` computes it from `connector.RootPath`; `HandleFileEventAsync` uses new `ComputeVirtualPath()` helper.

**Timing test results** (6 .md files, local Ollama, 4 parallel workers):
- Container created: ~770ms
- First doc visible (API): ~2.4s (sync fires within ~1.6s)
- First Ready: ~21s | All 6 done: ~54s | ~9s/doc with 4-way parallelism
- No "Queued" state visible — docs enter DB directly as "Processing" when worker picks them up

---

## Known Issues

See [issues.md](issues.md).
