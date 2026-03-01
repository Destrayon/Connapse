# Progress

Current status and recent work. Update at end of each session.

---

## Current Status (2026-03-01) — v0.3.0 Session E complete

**Branch:** `feature/0.3.0` | **Last shipped:** v0.2.2

### v0.3.0 Plan — ready to implement

Full plan at [docs/v0.3.0-plan.md](../../docs/v0.3.0-plan.md). Key decisions in [decisions.md](decisions.md).

| Session | Focus | Status |
|---------|-------|--------|
| A | IConnector abstraction + schema migration + IContainerSettingsResolver | **COMPLETE** |
| B | MinIO as IConnector + Filesystem connector + InMemory connector + ConnectorWatcherService | **COMPLETE** |
| C | S3 + AzureBlob connectors, sync endpoint, connection testers, UI | **COMPLETE** |
| D | User cloud identities — Azure OAuth2 + AWS OIDC gate + Profile page | **COMPLETE** |
| E | Cloud scope discovery + query-time enforcement | **COMPLETE** |
| F | RS256 + JWKS endpoint + AWS OIDC federation | Pending |
| G | OpenAI + Azure OpenAI embedding providers | Pending |
| H | ILlmProvider + Agentic search | Pending |
| I | Testing + docs | Pending |

---

## Shipped Versions

| Version | Key feature | Sessions |
|---------|-------------|----------|
| v0.2.2 | CLI self-update (`connapse update`, passive notification, Windows bat swap) | 19 |
| v0.2.0 | Security & auth: Identity project, cookie+PAT+JWT, RBAC, audit logging, agent entities, CLI auth, 256 tests | 8–18 |
| v0.1.0 | Container file browser, hybrid search, ingestion pipeline, MCP server | 1–7 |

**Test baseline (v0.2.2):** 256 tests across 12 projects, all passing.
**After Session B:** 95 unit tests pass (19 Core + 25 Identity + 51 Ingestion). Build: 0 warnings, 0 errors.
**After Session C6:** 116 unit tests pass (40 Core + 25 Identity + 51 Ingestion). Build: 0 warnings, 0 errors.
**After Session D:** 134 unit tests pass (40 Core + 43 Identity + 51 Ingestion). Build: 0 warnings, 0 errors.
**After Session E:** 159 unit tests pass (65 Core + 43 Identity + 51 Ingestion). Build: 0 warnings, 0 errors.

---

## Session E (2026-03-01) — Cloud Scope Discovery + Query-Time Enforcement

**Feature**: Cloud containers (S3, AzureBlob) now enforce per-user access scopes based on linked cloud identities. Users without a linked identity for the container's provider get a 403 with an actionable error message. Scope results are cached with a 15-min TTL (5 min for denials).

**New files created**:
1. `src/Connapse.Core/Models/CloudScopeModels.cs` — `CloudScopeResult` record with `Deny`, `Allow`, `FullAccess` factories and `IsPathAllowed` helper
2. `src/Connapse.Core/Interfaces/ICloudIdentityProvider.cs` — scope discovery interface per cloud provider
3. `src/Connapse.Core/Interfaces/IConnectorScopeCache.cs` — cache interface
4. `src/Connapse.Core/Interfaces/ICloudScopeService.cs` — orchestrator interface (returns null for non-cloud containers)
5. `src/Connapse.Storage/CloudScope/ConnectorScopeCache.cs` — IMemoryCache-backed singleton cache
6. `src/Connapse.Storage/CloudScope/AwsIdentityProvider.cs` — returns Deny when PrincipalArn is null (Session F), FullAccess when populated
7. `src/Connapse.Storage/CloudScope/AzureIdentityProvider.cs` — verifies service connectivity, grants access to configured prefix
8. `src/Connapse.Web/Services/CloudScopeService.cs` — orchestrates cache → identity → provider → cache; lives in Web to avoid circular project ref
9. `tests/Connapse.Core.Tests/CloudScope/CloudScopeServiceTests.cs` — 8 unit tests
10. `tests/Connapse.Core.Tests/CloudScope/ConnectorScopeCacheTests.cs` — 4 unit tests
11. `tests/Connapse.Core.Tests/CloudScope/AwsIdentityProviderTests.cs` — 3 unit tests
12. `tests/Connapse.Core.Tests/CloudScope/AzureIdentityProviderTests.cs` — 4 unit tests
13. `tests/Connapse.Core.Tests/CloudScope/CloudScopeResultTests.cs` — 5 unit tests (IsPathAllowed logic)

**Files modified**:
1. `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs` — registered AwsIdentityProvider, AzureIdentityProvider, ConnectorScopeCache
2. `src/Connapse.Web/Program.cs` — AddMemoryCache(), registered ICloudScopeService
3. `src/Connapse.Web/Endpoints/DocumentsEndpoints.cs` — cloud scope enforcement on all 4 endpoints (upload, list, get, delete)
4. `src/Connapse.Web/Endpoints/SearchEndpoints.cs` — cloud scope enforcement + path prefix filter injection for both GET and POST
5. `src/Connapse.Web/Endpoints/FoldersEndpoints.cs` — cloud scope enforcement on create and delete
6. `src/Connapse.Web/Endpoints/ContainersEndpoints.cs` — cloud scope enforcement on sync endpoint
7. `src/Connapse.Web/Endpoints/CloudIdentityEndpoints.cs` — cache eviction on identity disconnect
8. `tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj` — added project refs to Identity and Web for CloudScopeService tests

**Key design decisions**:
- `CloudScopeService` in Web (not Storage) to avoid circular project reference between Storage and Identity
- Non-cloud containers (MinIO, Filesystem, InMemory) return null from `GetScopesAsync` — endpoints skip enforcement
- Deny results cached with shorter TTL (5 min) so users see changes quickly after linking identity
- `IsPathAllowed` helper on `CloudScopeResult` centralizes path-prefix matching logic
- Search enforcement injects first allowed prefix as `pathPrefix` filter; multi-prefix OR-clause deferred
- AWS provider returns Deny until Session F (RS256 + OIDC); Azure provider verifies service connectivity and grants container-prefix-scoped access

**Known limitations**:
- Multi-prefix search: only first allowed prefix used as filter
- AWS prefix-level simulation: deferred to Session F (SimulatePrincipalPolicy)
- Azure RBAC granularity: access at container-config-prefix level, not per-folder Azure RBAC

---

## Session D (2026-03-01) — User Cloud Identities + Auth Flows

**Feature**: Users can link cloud provider identities (AWS, Azure) to their profile. Identity data is encrypted via Data Protection API. Azure OAuth2 flow fully implemented. AWS OIDC gated on RS256 (Session F).

**New files created**:
1. `src/Connapse.Core/Models/CloudProvider.cs` — enum: AWS, Azure
2. `src/Connapse.Identity/Data/Entities/UserCloudIdentityEntity.cs` — entity with encrypted identity JSON
3. `src/Connapse.Identity/Stores/ICloudIdentityStore.cs` — CRUD interface
4. `src/Connapse.Identity/Stores/PostgresCloudIdentityStore.cs` — Postgres implementation
5. `src/Connapse.Identity/Services/AzureAdSettings.cs` — Azure AD OAuth2 settings model
6. `src/Connapse.Identity/Services/ICloudIdentityService.cs` — service interface
7. `src/Connapse.Identity/Services/CloudIdentityService.cs` — encryption, Azure OAuth2 token exchange, AWS RS256 gate
8. `src/Connapse.Web/Endpoints/CloudIdentityEndpoints.cs` — 5 endpoints (list, azure connect/callback, aws connect, disconnect)
9. `src/Connapse.Web/Components/Pages/Profile.razor` — user profile page with Cloud Identities section
10. EF migration `AddUserCloudIdentities` — `user_cloud_identities` table
11. `tests/Connapse.Identity.Tests/CloudIdentityServiceTests.cs` — 18 unit tests

**Files modified**:
1. `src/Connapse.Identity/Data/Entities/ConnapseUser.cs` — added CloudIdentities navigation property
2. `src/Connapse.Identity/Data/ConnapseIdentityDbContext.cs` — DbSet + ConfigureUserCloudIdentities
3. `src/Connapse.Identity/Services/JwtSettings.cs` — added SigningAlgorithm property (default HS256)
4. `src/Connapse.Identity/IdentityServiceExtensions.cs` — registered store, service, AzureAdSettings
5. `src/Connapse.Web/Program.cs` — mapped CloudIdentityEndpoints
6. `src/Connapse.Web/Components/Layout/NavMenu.razor` — username now links to /profile
7. `src/Connapse.Web/appsettings.json` — added Identity:AzureAd section
8. `src/Connapse.Core/Models/AuthModels.cs` — added CloudIdentityDto, CloudIdentityData, AzureConnectResult

**Key design decisions**:
- Identity data encrypted with `IDataProtectionProvider.CreateProtector("CloudIdentity.v1")` — gracefully degrades if keys rotate
- Azure OAuth2 CSRF protection via `__connapse_az_state` cookie (HttpOnly, Secure, SameSite=Lax, 10-min expiry)
- Azure callback decodes ID token without signature validation (received directly from Microsoft over HTTPS)
- AWS connect always returns error until RS256 is implemented in Session F — no AWS SDK dependency in Identity project
- Profile page accessible via clickable username in nav sidebar bottom — not a separate nav link
- Upsert pattern for cloud identities: delete existing + create new (avoids unique constraint issues)

---

## Session C6 (2026-03-01) — S3 + Azure Blob connectors

**Feature**: All 5 connector types now functional. S3 and Azure Blob containers can be created, configured, connection-tested, and synced on demand.

**New files created**:
1. `src/Connapse.Storage/Connectors/S3ConnectorConfig.cs` — config record: bucketName, region, prefix, roleArn
2. `src/Connapse.Storage/Connectors/S3Connector.cs` — IConnector backed by AWS S3 via default credential chain; optional STS AssumeRole for cross-account
3. `src/Connapse.Storage/Connectors/AzureBlobConnectorConfig.cs` — config record: storageAccountName, containerName, prefix, managedIdentityClientId
4. `src/Connapse.Storage/Connectors/AzureBlobConnector.cs` — IConnector backed by Azure Blob Storage via DefaultAzureCredential
5. `src/Connapse.Storage/ConnectionTesters/S3ConnectionTester.cs` — tests S3 bucket access, handles STS AssumeRole
6. `src/Connapse.Storage/ConnectionTesters/AzureBlobConnectionTester.cs` — tests Azure Blob container access
7. `tests/Connapse.Core.Tests/Connectors/ConnectorFactoryTests.cs` — 13 unit tests for factory wiring + error cases
8. `tests/Connapse.Core.Tests/Connectors/ConnectorConfigTests.cs` — 8 unit tests for config deserialization + round-trips

**Files modified**:
1. `src/Connapse.Storage/Connapse.Storage.csproj` — added AWSSDK.SecurityToken, Azure.Storage.Blobs, Azure.Identity
2. `src/Connapse.Storage/Connectors/ConnectorFactory.cs` — replaced NotImplementedException with CreateS3Connector/CreateAzureBlobConnector
3. `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs` — registered S3ConnectionTester + AzureBlobConnectionTester
4. `src/Connapse.Web/Endpoints/ContainersEndpoints.cs` — added connector config validation for S3/AzureBlob in create, new `POST /api/containers/test-connection` and `POST /api/containers/{id}/sync` endpoints
5. `src/Connapse.Web/Components/Pages/Home.razor` — S3 and AzureBlob config fields in create modal, Test Connection button

**Key design decisions**:
- S3Connector creates its own AmazonS3Client (separate from global MinIO client) — uses RegionEndpoint, no ForcePathStyle
- Both connectors support optional prefix filtering — only index files/blobs under a configured prefix
- Sync endpoint (`POST /api/containers/{id}/sync`) mirrors ConnectorWatcherService.InitialSyncAsync pattern: list remote files, compare to DB, enqueue new/changed, skip Ready/Failed
- Returns 400 for Filesystem (live watch) and InMemory (no remote source)
- Connection testers accept config as JSON string and return ConnectionTestResult with detailed error messages
- Used `new AmazonS3Client(region)` instead of deprecated `FallbackCredentialsFactory.GetCredentials()` for v4 SDK compatibility

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
