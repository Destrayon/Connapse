# Architectural Decisions

Record significant decisions with context and rationale. Future sessions should check this before making architectural changes.

---

### 2026-02-04 — Project Structure: src/ Layout

**Context**: The initial VS template created a flat `AIKnowledgePlatform/` directory. The CLAUDE.md and init.md specify a `src/` based layout with separate projects per domain.

**Decision**: Restructured to `src/{ProjectName}/` layout with 7 source projects and 3 test projects.

**Alternatives**:
- Option A: Keep flat layout with single project — simpler but no separation of concerns
- Option B: Use `src/` layout with domain-separated projects — matches CLAUDE.md architecture

**Rationale**: Domain separation enables swappable implementations (e.g., different vector stores, embedding providers) via DI without coupling. Each project has a clear responsibility.

**Consequences**: More projects to manage, but cleaner boundaries and testability.

---

### 2026-02-04 — Core Models in Root Namespace

**Context**: Needed to decide where to place shared record types (IngestionResult, SearchHit, etc.) used across multiple projects.

**Decision**: Model records live in `AIKnowledge.Core` namespace (files in `Models/` folder) so they can be used without additional `using` statements when the Core project is referenced.

**Alternatives**:
- Option A: `AIKnowledge.Core.Models` namespace — requires extra using
- Option B: `AIKnowledge.Core` namespace — available immediately with project reference

**Rationale**: These are fundamental domain types used everywhere. Keeping them in the root namespace reduces boilerplate.

**Consequences**: Root namespace has more types, but they're all core domain concepts.

---

### 2026-02-04 — Local-First Default Configuration

**Context**: Need sensible defaults for `appsettings.json` that work without cloud services.

**Decision**: Default to Ollama (embeddings + LLM), SQLite-vec (vector store), file system (uploads), no web search.

**Alternatives**:
- Option A: Cloud-first (OpenAI, Pinecone) — requires API keys to run
- Option B: Local-first (Ollama, SQLite-vec) — runs without any external accounts

**Rationale**: Matches the "local-first design" principle in CLAUDE.md. Cloud services swap in via config changes, no code changes needed.

**Consequences**: Users need Ollama installed locally for full functionality, but the app starts and builds without it.

---

### 2026-02-04 — Virtual File System with Physical Root Mapping

**Context**: Need a file system service where virtual paths like "/folder/a/b" map to physical paths under a configurable root directory, used consistently by both Web UI and CLI.

**Decision**: Created `IKnowledgeFileSystem` interface in Core with `LocalKnowledgeFileSystem` implementation in Storage. Virtual paths are normalized, combined with a configurable `RootPath`, and validated to prevent path traversal. Default root is `knowledge-data/` relative to the working directory.

**Alternatives**:
- Option A: Use raw file paths throughout — no abstraction, path security issues
- Option B: Virtual file system service — consistent mapping, path traversal protection, testable via interface
- Option C: Database-backed file metadata with blob storage — adds complexity, not needed for local-first

**Rationale**: Option B gives a clean API for both UI and CLI, prevents path traversal attacks, and keeps the local-first philosophy (files on disk, no database needed for file management).

**Consequences**: All file operations go through `IKnowledgeFileSystem`. The root directory is created on startup. Cloud deployments could swap in a blob-storage-backed implementation.

---

### 2026-02-04 — Dark Mode Theme with Purple Accents

**Context**: The default Blazor template has a light theme. The project needs a distinct visual identity.

**Decision**: Dark mode by default using Bootstrap 5's `data-bs-theme="dark"` on the `<html>` tag, with purple (#8b5cf6) as the accent color. All theme values are CSS custom properties for easy customization.

**Alternatives**:
- Option A: Light theme (Bootstrap default) — generic, no identity
- Option B: Dark theme with purple accents — distinctive, modern, reduces eye strain
- Option C: Theme toggle (light/dark) — more complex, not needed yet

**Rationale**: Dark mode with purple gives the app a clear identity. Using CSS custom properties makes it straightforward to add a theme toggle later without changing component code.

**Consequences**: All new UI components should use the CSS custom properties (e.g., `var(--surface)`, `var(--accent)`) rather than hardcoding colors.

---

### 2026-02-04 — PostgreSQL + pgvector over SQLite

**Context**: Original plan used SQLite + sqlite-vec for local-first simplicity. With the decision to dockerize, we need concurrent write support, proper full-text search, and vector search in one engine.

**Decision**: Use PostgreSQL 17 with the pgvector extension as the single database for documents, chunks, full-text search (tsvector), and vector storage.

**Alternatives**:
- Option A: SQLite + sqlite-vec — single file, zero config, but single-writer lock bottleneck during batch ingestion
- Option B: PostgreSQL + pgvector — concurrent writes, built-in FTS, pgvector for similarity search, all in one engine
- Option C: PostgreSQL + separate vector DB (Qdrant) — better vector performance but more infrastructure

**Rationale**: PostgreSQL handles all three storage needs (relational, FTS, vector) in one service. Batch uploads of 100-200 files require concurrent writes. Docker Compose makes Postgres trivial to run locally. The `IVectorStore` and `IDocumentStore` interfaces keep the door open for Option C later.

**Consequences**: Requires Docker (or local Postgres install) to run. Migrations via EF Core or raw SQL. Connection string via environment variable.

---

### 2026-02-04 — MinIO for Object/File Storage

**Context**: Need to store original uploaded files. Options: database BLOBs, local filesystem, or S3-compatible object storage. Project will be dockerized.

**Decision**: Use MinIO as S3-compatible object storage for original uploaded files. The existing `IKnowledgeFileSystem` interface gets a new `MinioFileSystem` implementation using the AWS S3 SDK.

**Alternatives**:
- Option A: Docker volume + local filesystem — simplest, already have `LocalKnowledgeFileSystem`, but no S3 compatibility
- Option B: MinIO (S3-compatible) — one extra container, cloud migration trivial (swap endpoint to real S3/Azure Blob/GCS)
- Option C: Postgres large objects / bytea — single service but DB bloat, backup size explosion

**Rationale**: MinIO is a single binary, easy Docker container. S3-wire-compatible means the same code works against AWS S3, Azure Blob (via gateway), or GCS with only an endpoint change. Files don't belong in a database.

**Consequences**: Extra container in docker-compose. `IKnowledgeFileSystem` stays as the abstraction. MinIO web UI available for debugging. Original `LocalKnowledgeFileSystem` remains for non-Docker development.

---

### 2026-02-04 — Hybrid Search with RRF Fusion + Optional Cross-Encoder Reranking

**Context**: Need to combine semantic (vector) and keyword (FTS) search results into a single ranked list. Must decide on fusion/reranking strategy.

**Decision**: Default to Reciprocal Rank Fusion (RRF, k=60) for combining semantic and keyword results. Cross-encoder reranking available as a configurable option for higher quality at the cost of latency.

**Alternatives**:
- Option A: RRF only — no model needed, fast, mathematically combines rank positions
- Option B: Cross-encoder reranking only — most accurate, but requires model and adds latency per result
- Option C: RRF default + optional cross-encoder — best of both, user chooses quality vs speed

**Rationale**: RRF is proven effective in production RAG systems and requires no additional model. Making cross-encoder optional respects the local-first principle (works without extra models) while giving power users a quality upgrade. Both strategies implement `ISearchReranker`.

**Consequences**: New `ISearchReranker` interface. `RrfReranker` ships as default. `CrossEncoderReranker` requires a compatible model in Ollama or an API-based provider. Strategy selectable via Settings page.

---

### 2026-02-04 — Runtime-Mutable Settings via Database

**Context**: Settings are currently in `appsettings.json` only. Users need to change chunking strategies, embedding models, search modes, etc. from the UI without restarting the app.

**Decision**: Runtime-mutable settings stored in Postgres (`settings` table, JSONB per category). Layered on top of the existing .NET configuration hierarchy. `IOptionsMonitor<T>` triggers live reload when settings change.

**Resolution order** (lowest to highest priority):
```
appsettings.json → appsettings.{Env}.json → Environment vars → Database (Settings page)
```

**Alternatives**:
- Option A: Config files only — requires restart, no UI
- Option B: Database-backed settings with IOptionsMonitor — live reload, UI-editable, still respects env vars for deployment
- Option C: Separate settings microservice — overengineered

**Rationale**: Option B gives users a Settings page while preserving the standard .NET config pipeline for deployment overrides (env vars for connection strings, secrets).

**Consequences**: New `ISettingsStore` interface. Settings page in Blazor with tabs per category. Services must use `IOptionsMonitor<T>` (not `IOptions<T>`) to pick up changes. Sensitive values (API keys) still overridable via env vars.

---

### 2026-02-04 — Background Ingestion with Queue for Batch Uploads

**Context**: Need to support uploading 100-200 files at once. Synchronous processing would block the request and time out.

**Decision**: Upload endpoint streams files to MinIO and enqueues ingestion jobs. A background `IHostedService` worker processes jobs with configurable parallelism (default: 4 concurrent files). Clients track progress via polling or SignalR.

**Alternatives**:
- Option A: Synchronous inline processing — blocks request, times out on large batches
- Option B: In-process background queue (`Channel<T>`) + `IHostedService` — simple, no external deps
- Option C: External message queue (RabbitMQ, Redis Streams) — more resilient but adds infrastructure

**Rationale**: Option B is sufficient for a single-instance app. The `Channel<T>` provides backpressure. If scaling to multiple instances is needed later, swapping to Option C behind `IIngestionQueue` is straightforward.

**Consequences**: New `IIngestionQueue` interface. Batch tracking table in Postgres. SignalR hub for real-time progress. CLI can wait synchronously or run in background.

---

### 2026-02-05 — Testing Strategy: Unit Tests + Testcontainers Integration Tests

**Context**: Need comprehensive test coverage for production readiness. Core components (parsers, chunkers, search fusion) need unit tests. End-to-end workflows (upload → ingest → search, reindex, settings reload) need integration tests with real services.

**Decision**: Two-tier testing approach:
1. **Unit tests** (xUnit + FluentAssertions + NSubstitute) for isolated component testing
2. **Integration tests** (Testcontainers + WebApplicationFactory) for end-to-end workflows with real PostgreSQL and MinIO

**Alternatives**:
- Option A: Unit tests only with mocks — fast but misses integration bugs, doesn't test real DB/storage behavior
- Option B: Unit tests + Testcontainers integration tests — comprehensive, catches real-world issues, requires Docker
- Option C: Manual testing only — error-prone, not repeatable, no regression protection

**Rationale**: Option B provides best balance of speed (unit tests run in milliseconds) and confidence (integration tests catch real bugs). Testcontainers automatically manages container lifecycle, making tests self-contained and reproducible. TDD approach caught 4 production bugs (IndexOutOfRangeException, ArgumentOutOfRangeException, exception handling) before deployment.

**Consequences**:
- Unit tests: 65 tests for parsers (29), chunkers (27), RRF reranker (11)
- Integration tests: 10 tests for ingestion (2), reindex (3), settings (4) — require Docker
- Build: 0 warnings, 0 errors, 100% pass rate
- All tests follow `MethodName_Scenario_ExpectedResult` naming convention
- Integration tests use `IAsyncLifetime` for proper container cleanup

---

### 2026-02-06 — Container-Based File Browser with Vector Index Isolation (IMPLEMENTED)

**Context**: The upload page was a simple file upload interface. Users needed a full object storage browser (like S3/MinIO) where they can organize files into projects with folder hierarchies. Each project should have isolated search - searching one project should never return results from another.

**Decision**: Replaced the upload page with a container-based file browser. Containers are top-level isolated units (representing projects). Each container has its own logical vector space. Folders provide organizational hierarchy within containers. Full path (`/{container}/{folder-path}/{filename}`) determines uniqueness.

**Status**: Fully implemented across all 9 phases (schema, core services, API, UI, CLI, MCP, tests). 171 tests passing.

**Key Design Decisions**:

1. **Container Isolation**: Single `chunks` table with `container_id` column, always filtered by container. No cross-container search allowed. Containers represent isolated projects.

2. **Folder Hierarchy**: Folders are organizational units within containers. Path-based filtering for search (e.g., search in `/docs/2026/` only searches that subtree recursively). Empty folders are explicitly supported.

3. **File Uniqueness**: Full path including container is the unique identifier. Same filename in same folder → gets `file (1).pdf` pattern. Same file content in different containers → completely independent, no cross-container deduplication.

4. **Chunk Lifecycle**: Chunks are tied to file lifecycle via `CASCADE DELETE`. File deleted → chunks deleted. File re-uploaded with same path and different hash → re-index. Same hash → skip.

5. **File Editing**: Delete + re-upload only (no in-place editing). Most document types can't be edited in-browser anyway, and edits require full re-chunking/re-embedding.

6. **Folder Deletion**: Confirmation required, then cascade delete all nested files/folders and their chunks.

7. **Container Deletion**: Must be empty first (fail if not empty). User must delete all contents before deleting container.

8. **Cross-Container Operations**: Moving files between containers is prohibited. Must delete from source and re-upload to destination.

9. **CollectionId Removal**: The existing `CollectionId` field is replaced by containers. Containers serve the same purpose but are required, structured, and first-class.

**Alternatives**:
- Option A: Keep simple upload page + CollectionId tags — no visual organization, soft filtering only
- Option B: Container-based file browser with hard isolation — full object storage UX, true project isolation
- Option C: Virtual folders without isolation — organizational but no search separation

**Rationale**: Option B provides the project isolation users need (different knowledge bases shouldn't mix), gives a familiar S3-like UX for organizing files, and makes the folder structure meaningful for search filtering. The container concept maps naturally to "projects" or "workspaces".

**Consequences**:
- New `containers` table in database
- `documents` table gets required `container_id` (replaces optional `CollectionId`)
- `chunks` table gets denormalized `container_id` for query performance
- New `folders` table for empty folder support
- All API endpoints scoped under `/api/containers/{id}/...`
- Search API requires container ID
- UI becomes a file browser with container list → folder navigation → file details
- All access surfaces (Web UI, REST API, CLI, MCP) must support full file management

---

<!-- Add new decisions above this line, newest first -->
