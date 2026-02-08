# Progress

Current status and recent work. Update at end of each session. For detailed implementation plans, see [docs/architecture.md](../../docs/architecture.md).

---

## Current Status (2026-02-08)

### Feature #1: Document Upload + Ingestion + Hybrid Search
**Status**: COMPLETE

- Infrastructure (Docker, PostgreSQL+pgvector, MinIO)
- Settings system (runtime-mutable, DB-backed, live reload)
- Storage layer (document store, vector store, embeddings)
- Ingestion pipeline (parsers, chunkers, background queue)
- Hybrid search (vector + keyword FTS + RRF/CrossEncoder)
- Access surfaces (Web UI, REST API, CLI, MCP server)
- Reindexing (content-hash dedup, settings-change detection)

### Feature #2: Container-Based File Browser
**Status**: COMPLETE — All 9 phases implemented

- Phase 1: Database schema migration (containers, folders, container_id on docs/chunks/vectors)
- Phase 2: Core services (IContainerStore, IFolderStore, PathUtilities, updated all stores/search/ingestion)
- Phase 3: API endpoints (container CRUD, file ops, folder ops, search, reindex — all container-scoped)
- Phase 4: Web UI - Container List (main page /, create/delete modals, empty state)
- Phase 5: Web UI - File Browser (breadcrumbs, file/folder list, upload, create/delete)
- Phase 6: Web UI - File Details (side panel with metadata, status, actions)
- Phase 7: CLI (container CRUD, upload/search/reindex require --container, name-to-ID resolution)
- Phase 8: MCP (7 tools: container_create/list/delete, search_knowledge, list_files, upload_file, delete_file)
- Phase 9: Testing + bug fixes

### Session 5 Fixes: Semantic Search (2026-02-07)
- **Critical Fix**: `PgVectorStore.SearchAsync` — `Vector` type silently dropped by `SqlQueryRaw` positional params. Fixed with named `NpgsqlParameter` objects.
- **MinScore Tuning**: Default 0.7 was too aggressive for nomic-embed-text. Changed to configurable `MinimumScore` (default 0.5) via Settings page / API / CLI / MCP.

### Test Counts
- 78 core unit tests (PathUtilities, parsers, chunkers, rerankers)
- 53 ingestion unit tests
- 40 integration tests (containers, folders, files, search isolation, cascade deletes, ingestion, reindex)
- **171 total tests**
- All 10 projects build with 0 errors

---

## Feature #2: Container-Based File Browser

### Overview

Replace simple upload page with S3-like object storage browser. Containers provide isolated vector indexes (projects). Folders provide organizational hierarchy with path-based search filtering.

### Data Model

```
Container (top-level, required)
+-- id: UUID
+-- name: string (unique, 2-128 chars, lowercase alphanumeric + hyphens)
+-- description: string?
+-- created_at: timestamp
+-- updated_at: timestamp

Document (file in container)
+-- id: UUID
+-- container_id: UUID (required, FK -> containers)
+-- path: string (e.g., "/folder/subfolder/file.pdf")
+-- file_name: string
+-- content_hash: string (SHA-256)
+-- size_bytes: long
+-- mime_type: string
+-- status: enum (Pending, Processing, Ready, Error)
+-- error_message: string?
+-- created_at: timestamp
+-- updated_at: timestamp
+-- metadata: jsonb

Chunk (vector embeddings, cascade delete)
+-- id: UUID
+-- document_id: UUID (FK -> documents, CASCADE DELETE)
+-- container_id: UUID (denormalized for query perf)
+-- content: text
+-- chunk_index: int
+-- search_vector: tsvector (FTS)
+-- metadata: jsonb

ChunkVector (embedding vector, cascade delete)
+-- id: UUID
+-- chunk_id: UUID (FK -> chunks, CASCADE DELETE)
+-- container_id: UUID (denormalized)
+-- embedding: vector(dimensions)
+-- model_id: string
+-- metadata: jsonb

Folder (for empty folders only)
+-- id: UUID
+-- container_id: UUID (FK -> containers)
+-- path: string (e.g., "/folder/subfolder/")
+-- created_at: timestamp
```

### File Path Rules

- Full path: `/{container-name}/{folder-path}/{filename}`
- Container names: lowercase alphanumeric + hyphens, 2-128 chars, unique
- Paths normalized: no trailing slashes on files, trailing slash on folders
- Duplicate handling: `file.pdf` -> `file (1).pdf` -> `file (2).pdf`
- Path determines uniqueness, not content hash
- Content hash used only for re-index detection (same path, different content -> re-index)

### API Surface

#### Containers
```
POST   /api/containers                    Create container
GET    /api/containers                    List all containers
GET    /api/containers/{id}               Get container details
DELETE /api/containers/{id}               Delete container (must be empty)
```

#### Files (within container)
```
POST   /api/containers/{id}/files         Upload file(s) to path
GET    /api/containers/{id}/files         List files/folders at path (query: ?path=/folder/)
GET    /api/containers/{id}/files/{fileId} Get file details + indexing status
GET    /api/containers/{id}/files/{fileId}/reindex-check  Check if file needs reindex
DELETE /api/containers/{id}/files/{fileId} Delete file (cascades to chunks)
```

#### Folders (within container)
```
POST   /api/containers/{id}/folders       Create empty folder
DELETE /api/containers/{id}/folders       Delete folder (query: ?path=/folder/, cascades)
```

#### Search (scoped to container)
```
GET    /api/containers/{id}/search        Search within container (query: ?q=...&path=/folder/&minScore=0.5)
POST   /api/containers/{id}/search        Search with complex filters
```

#### Reindex (scoped to container)
```
POST   /api/containers/{id}/reindex       Reindex documents in container
```

### UI Flow

**Navigation**: Files (home `/`) | Search (`/search`) | Settings (`/settings`)
- The file system IS the main page -- no separate Home or Upload pages
- Upload page removed, replaced by file browser drag-drop upload

1. **Container List** (`/` -- main page)
   - Grid/list of containers as cards
   - Create container button + modal
   - Click container -> navigate to file browser

2. **File Browser** (`/containers/{id}`)
   - Breadcrumb navigation (Container > Folder > Subfolder)
   - Folder/file list view (table or grid)
   - Drag-drop zone for uploads
   - Create folder button
   - Search bar (scoped to current container, optionally current folder subtree)
   - Multi-select bulk delete

3. **File Details Panel** (side panel)
   - Basic info: name, size, MIME type, upload date, full path
   - Status: indexing progress, ready/error state
   - Technical: chunk count, embedding model, content hash
   - Actions: delete, re-index

4. **Upload Progress**
   - Real-time status via SignalR
   - Show pending -> processing -> ready/error

### CLI Commands

```bash
# Container management
aikp container create <name> [--description "..."]
aikp container list
aikp container delete <name>

# File operations (scoped to container)
aikp upload <path> --container <name> [--destination /folder/] [--strategy Semantic]

# Search (scoped to container)
aikp search "<query>" --container <name> [--mode Hybrid] [--top 10] [--path /folder/] [--min-score 0.5]

# Reindex (scoped to container)
aikp reindex --container <name> [--force] [--no-detect-changes]
```

### MCP Tools

```
container_create     Create a new container
container_list       List all containers
container_delete     Delete an empty container
upload_file          Upload file to container
list_files           List files in container/folder
delete_file          Delete file from container
search_knowledge     Search within container (containerId required, optional path + minScore)
```

### Implementation Phases (All Complete)

#### Phase 1: Database Schema Migration
- [x] Create `containers` table (ContainerEntity, unique name index)
- [x] Add `container_id` to documents, chunks, chunk_vectors tables
- [x] Create `folders` table (FolderEntity, unique container+path index)
- [x] Remove `CollectionId` from documents (replaced with `ContainerId` Guid)
- [x] Rename `VirtualPath` -> `Path` on DocumentEntity

#### Phase 2: Core Services
- [x] `IContainerStore` + `PostgresContainerStore` (CRUD, name validation, document count)
- [x] `IFolderStore` + `PostgresFolderStore` (create, list, delete with cascade)
- [x] `PathUtilities` (path normalization, container name validation, duplicate naming)
- [x] Update `IDocumentStore`, `PgVectorStore`, search services, ingestion, reindex
- [x] New `IIngestionQueue.CancelJobForDocumentAsync` for job cancellation

#### Phase 3: API Endpoints
- [x] Container CRUD endpoints under `/api/containers`
- [x] File ops under `/api/containers/{id}/files`
- [x] Folder endpoints under `/api/containers/{id}/folders`
- [x] Search/reindex scoped to container
- [x] Reindex-check moved to `/api/containers/{id}/files/{fileId}/reindex-check`

#### Phase 4-6: Web UI
- [x] Container list page at `/` (replaces Home + Upload pages)
- [x] File browser at `/containers/{id}` with breadcrumbs, drag-drop upload
- [x] File details side panel with indexing status, SignalR progress
- [x] Removed Counter.razor, Weather.razor, Upload.razor

#### Phase 7: CLI Updates
- [x] Container management commands (container create/list/delete)
- [x] All commands require --container (name-to-ID resolution)
- [x] `--min-score` option on search

#### Phase 8: MCP Updates
- [x] Container tools (container_create, container_list, container_delete)
- [x] New tools (list_files, upload_file, delete_file)
- [x] Updated search_knowledge (containerId required, path + minScore filtering)

#### Phase 9: Testing
- [x] Unit tests: PathUtilities (25+ tests)
- [x] Integration tests: Container CRUD, folder ops, file browse, isolation, cascades
- [x] Updated IngestionIntegrationTests and ReindexIntegrationTests for container-scoped API
- [x] 6 bugs found and fixed during testing (see issues.md)

---

## Known Issues

See [issues.md](issues.md) for detailed tracking of bugs and tech debt.

---

## Session History

### 2026-02-07 (Session 6) -- Semantic Search Bug Fix & MinScore Tuning
- Fixed critical pgvector parameter binding (named NpgsqlParameter objects)
- Made minScore configurable across all surfaces (Settings, API, CLI, MCP)
- 129 unit tests passing; integration tests blocked by VS file locks

### 2026-02-06 (Session 5) -- Feature #2 Phase 9: Testing (COMPLETE)
- 78 core + 53 ingestion + 40 integration = 171 tests, all passing
- 6 bugs found and fixed during testing

### 2026-02-06 (Session 4) -- Feature #2 Phases 1-8
- Complete implementation of container-based file browser
- Schema migration, core services, API endpoints, Web UI, CLI, MCP

### 2026-02-05 (Session 3) -- Critical Bug Fixes
- Fixed JSONB deserialization (Dictionary -> JsonDocument)
- Fixed DbContext threading (IServiceScopeFactory for parallel operations)
- Fixed settings reload architecture (instance-based DI, no static fields)

### 2026-02-05 (Session 2) -- Integration Test Fixes
- 11/14 tests passing (settings reload tests still failing in test env)

### 2026-02-05 (Session 1) -- Initial Integration Tests
- 6/14 tests passing, fixed multiple startup and ingestion bugs
