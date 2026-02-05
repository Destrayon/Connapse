# Progress

Current tasks, blockers, and session history. Update at end of each work session.

---

## Current Task

**Task**: Feature #1 — Document Upload + Ingestion Pipeline + Hybrid Search

**Status**: Phase 3 (Storage Implementations) complete — ready for Phase 4

---

## Implementation Plan

### Infrastructure: PostgreSQL, MinIO, Docker

| # | Task | Project | Depends On |
|---|------|---------|------------|
| 1.1 | Create `docker-compose.yml` — Postgres 17 + pgvector, MinIO, Ollama (optional), app | root | — |
| 1.2 | Create `Dockerfile` for `AIKnowledge.Web` | root | — |
| 1.3 | Add `Npgsql` + `Npgsql.EntityFrameworkCore.PostgreSQL` + `pgvector` NuGet packages to Storage project | Storage | — |
| 1.4 | Add `AWSSDK.S3` NuGet package to Storage project (MinIO uses S3 protocol) | Storage | — |
| 1.5 | Create Postgres schema — `documents`, `chunks`, `chunk_vectors`, `settings`, `batches` tables; pgvector extension; FTS tsvector column + GIN index | Storage | 1.3 |
| 1.6 | Add DB migration strategy (EF Core migrations or raw SQL scripts in a `migrations/` folder) | Storage | 1.5 |
| 1.7 | Implement `MinioFileSystem : IKnowledgeFileSystem` using S3 SDK | Storage | 1.4 |
| 1.8 | Update `Program.cs` — register Postgres DbContext, MinIO, configure services | Web | 1.3, 1.4 |
| 1.9 | Update `appsettings.json` — add Postgres connection string, MinIO config, remove SQLite-vec reference | Web | 1.8 |

### Settings System

| # | Task | Project | Depends On |
|---|------|---------|------------|
| 2.1 | Define `ISettingsStore` interface in Core — `GetAsync<T>`, `SaveAsync<T>`, `ResetAsync` | Core | — |
| 2.2 | Define settings record types per category — `EmbeddingSettings`, `ChunkingSettings`, `SearchSettings`, `LlmSettings`, `UploadSettings`, `WebSearchSettings`, `StorageSettings` | Core | — |
| 2.3 | Implement `PostgresSettingsStore : ISettingsStore` — reads/writes `settings` table (JSONB) | Storage | 1.5, 2.1 |
| 2.4 | Create `DatabaseSettingsProvider : IConfigurationProvider` — layers DB settings on top of appsettings at startup | Storage | 2.3 |
| 2.5 | Wire up `IOptionsMonitor<T>` for live reload when settings change | Web | 2.3, 2.4 |
| 2.6 | Build Settings page — tabbed layout (Embedding, Chunking, Search, LLM, Uploads, Web Search) | Web | 2.2, 2.3 |
| 2.7 | Add "Test Connection" functionality for Ollama, MinIO, external APIs | Web | 2.6 |
| 2.8 | Add `GET /api/settings/{category}` and `PUT /api/settings/{category}` API endpoints | Web | 2.3 |

### Ingestion Pipeline

| # | Task | Project | Depends On |
|---|------|---------|------------|
| 3.1 | Define `IDocumentParser` interface — `Task<ParsedDocument> ParseAsync(Stream, string fileName, CancellationToken)` | Core | — |
| 3.2 | Define `IChunkingStrategy` interface — `IReadOnlyList<Chunk> ChunkAsync(ParsedDocument, ChunkingSettings, CancellationToken)` | Core | — |
| 3.3 | Define `IIngestionQueue` interface — `EnqueueAsync`, `DequeueAsync`, `GetStatusAsync` | Core | — |
| 3.4 | Define `ISearchReranker` interface — `Task<List<SearchHit>> RerankAsync(string query, List<SearchHit> hits, CancellationToken)` | Core | — |
| 3.5 | Implement `TextParser` (.txt, .md, .csv) | Ingestion | 3.1 |
| 3.6 | Implement `PdfParser` (.pdf — via PdfPig) | Ingestion | 3.1 |
| 3.7 | Implement `OfficeParser` (.docx, .pptx — via OpenXML) | Ingestion | 3.1 |
| 3.8 | Implement `FixedSizeChunker` — token-count based with configurable overlap | Ingestion | 3.2 |
| 3.9 | Implement `RecursiveChunker` — split on paragraphs → sentences → words | Ingestion | 3.2 |
| 3.10 | Implement `SemanticChunker` — embedding-similarity-based boundary detection | Ingestion | 3.2, 4.1 |
| 3.11 | Implement `IngestionPipeline : IKnowledgeIngester` — orchestrates parse → chunk → embed → store, reports progress | Ingestion | 3.5-3.9, 4.1, 4.2, 4.3 |
| 3.12 | Implement `IngestionQueue` using `Channel<T>` | Ingestion | 3.3 |
| 3.13 | Implement `IngestionWorker : BackgroundService` — dequeues jobs, processes N in parallel | Ingestion | 3.11, 3.12 |
| 3.14 | Content-hash-based deduplication — SHA-256 hash stored per document, skip unchanged on re-ingest | Ingestion | 3.11, 4.2 |

### Storage Implementations

| # | Task | Project | Depends On |
|---|------|---------|------------|
| 4.1 | Implement `OllamaEmbeddingProvider : IEmbeddingProvider` — calls Ollama `/api/embeddings` endpoint, supports batch | Storage | 1.8 |
| 4.2 | Implement `PostgresDocumentStore : IDocumentStore` — CRUD for documents table | Storage | 1.5 |
| 4.3 | Implement `PgVectorStore : IVectorStore` — upsert/search/delete vectors via pgvector, supports `DeleteByDocumentIdAsync` for reindex | Storage | 1.5 |
| 4.4 | Implement FTS indexing — auto-update tsvector column on chunk insert/update via Postgres trigger or application code | Storage | 1.5, 4.2 |

### Search

| # | Task | Project | Depends On |
|---|------|---------|------------|
| 5.1 | Implement `VectorSearchService` — embeds query, calls `IVectorStore.SearchAsync` | Search | 4.1, 4.3 |
| 5.2 | Implement `KeywordSearchService` — builds tsquery, queries FTS index, returns ranked results | Search | 4.4 |
| 5.3 | Implement `RrfReranker : ISearchReranker` — Reciprocal Rank Fusion (k=60), merges + deduplicates results | Search | 3.4 |
| 5.4 | Implement `CrossEncoderReranker : ISearchReranker` — scores (query, chunk) pairs via LLM/model | Search | 3.4 |
| 5.5 | Implement `HybridSearchService : IKnowledgeSearch` — runs vector + keyword in parallel, fuses via configured reranker | Search | 5.1, 5.2, 5.3 |

### Access Surfaces

| # | Task | Project | Depends On |
|---|------|---------|------------|
| 6.1 | REST API: `POST /api/documents` — multipart upload, streams to MinIO, enqueues ingestion | Web | 1.7, 3.12 |
| 6.2 | REST API: `GET /api/documents`, `GET /api/documents/{id}`, `DELETE /api/documents/{id}` | Web | 4.2 |
| 6.3 | REST API: `POST /api/documents/reindex` — triggers reindex for all or filtered documents | Web | 3.14 |
| 6.4 | REST API: `GET /api/search?q=&mode=&topK=`, `POST /api/search` (complex filters) | Web | 5.5 |
| 6.5 | REST API: `GET /api/batches/{id}/status` — batch upload progress | Web | 3.12 |
| 6.6 | SignalR hub for real-time ingestion progress | Web | 3.13 |
| 6.7 | Upload page — drag-drop zone, batch file select, destination folder, chunking strategy, progress table | Web | 6.1, 6.6 |
| 6.8 | Search page — query input, mode selector (Semantic/Keyword/Hybrid), results with scores + source info | Web | 6.4 |
| 6.9 | CLI `aikp ingest <path>` — enqueue files, show progress bar, wait for completion | CLI | 3.12, 6.1 |
| 6.10 | CLI `aikp search "<query>"` — search with configurable mode, display results | CLI | 6.4 |
| 6.11 | CLI `aikp reindex` — trigger reindex from command line | CLI | 6.3 |
| 6.12 | MCP server — expose `search_knowledge`, `ingest_document`, `list_documents` tools with JSON Schema | Web | 6.1, 6.4 |

### Reindexing

| # | Task | Project | Depends On |
|---|------|---------|------------|
| 7.1 | `ReindexService` — walks documents, compares content hashes against files in MinIO, re-processes changed ones | Ingestion | 3.14, 4.2, 1.7 |
| 7.2 | Support collection-scoped reindex and force-reindex (ignore hash) | Ingestion | 7.1 |
| 7.3 | Strategy-change reindex — detect when chunking/embedding settings changed, mark affected documents for re-processing | Ingestion | 7.1, 2.3 |

### Testing

| # | Task | Project | Depends On |
|---|------|---------|------------|
| 8.1 | Unit tests: document parsers (text, PDF, docx with sample files) | Ingestion.Tests | 3.5-3.7 |
| 8.2 | Unit tests: chunking strategies (fixed size, recursive — verify chunk sizes, overlap, boundaries) | Ingestion.Tests | 3.8-3.9 |
| 8.3 | Unit tests: RRF reranker (verify fusion math, deduplication) | Core.Tests | 5.3 |
| 8.4 | Unit tests: settings store (get, save, reset, change notification) | Core.Tests | 2.3 |
| 8.5 | Unit tests: content hash deduplication (skip unchanged, reprocess changed) | Ingestion.Tests | 3.14 |
| 8.6 | Integration test: upload → ingest → search round-trip using `WebApplicationFactory` + test Postgres (Testcontainers) | Integration.Tests | 6.1, 5.5 |
| 8.7 | Integration test: reindex detects changed files | Integration.Tests | 7.1 |
| 8.8 | Integration test: settings change triggers live reload | Integration.Tests | 2.5 |

---

## Recommended Implementation Order

Build in vertical slices so the system is runnable at each phase:

### Phase 1 — Infrastructure + Skeleton (tasks 1.x)
Docker Compose, Postgres schema, MinIO adapter, app wiring. At the end of this phase: `docker compose up` starts all services and the app connects to both.

### Phase 2 — Settings System (tasks 2.x)
Settings store, settings page UI, API endpoints. At the end: users can view/edit all configuration categories from the Settings page.

### Phase 3 — Storage Implementations (tasks 4.x)
Postgres document store, pgvector store, FTS indexing, Ollama embedding provider. At the end: the storage layer can persist and retrieve documents, chunks, vectors.

### Phase 4 — Ingestion Pipeline (tasks 3.x)
Parsers, chunkers, pipeline orchestrator, background worker, queue. At the end: files can be uploaded and processed into chunks + vectors in the background.

### Phase 5 — Search (tasks 5.x)
Vector search, keyword search, RRF fusion, hybrid search service. At the end: queries return ranked results combining semantic and keyword matches.

### Phase 6 — Access Surfaces (tasks 6.x)
REST API, Upload page, Search page, CLI commands, MCP tools. At the end: all four access surfaces work end-to-end.

### Phase 7 — Reindexing + Polish (tasks 7.x, 8.x)
Reindex service, content hash comparison, tests. At the end: changed files are detected and re-processed automatically.

---

## Postgres Schema (Reference)

```sql
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE documents (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    file_name       TEXT NOT NULL,
    content_type    TEXT,
    collection_id   TEXT,
    virtual_path    TEXT NOT NULL,
    content_hash    TEXT NOT NULL,           -- SHA-256 for reindex dedup
    size_bytes      BIGINT NOT NULL,
    chunk_count     INT NOT NULL DEFAULT 0,
    status          TEXT NOT NULL DEFAULT 'Pending',  -- Pending | Processing | Ready | Failed
    error_message   TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_indexed_at TIMESTAMPTZ,
    metadata        JSONB NOT NULL DEFAULT '{}'
);

CREATE TABLE chunks (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    document_id     UUID NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    content         TEXT NOT NULL,
    chunk_index     INT NOT NULL,
    token_count     INT NOT NULL,
    start_offset    INT NOT NULL,
    end_offset      INT NOT NULL,
    metadata        JSONB NOT NULL DEFAULT '{}',
    search_vector   tsvector GENERATED ALWAYS AS (to_tsvector('english', content)) STORED
);

CREATE INDEX idx_chunks_document_id ON chunks(document_id);
CREATE INDEX idx_chunks_fts ON chunks USING GIN(search_vector);

CREATE TABLE chunk_vectors (
    chunk_id        UUID PRIMARY KEY REFERENCES chunks(id) ON DELETE CASCADE,
    document_id     UUID NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    embedding       vector(768) NOT NULL,   -- dimension matches model config
    model_id        TEXT NOT NULL            -- track which model generated this
);

CREATE INDEX idx_chunk_vectors_document_id ON chunk_vectors(document_id);
CREATE INDEX idx_chunk_vectors_embedding ON chunk_vectors USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);

CREATE TABLE settings (
    category        TEXT PRIMARY KEY,
    values          JSONB NOT NULL DEFAULT '{}',
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE batches (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    total_files     INT NOT NULL,
    completed       INT NOT NULL DEFAULT 0,
    failed          INT NOT NULL DEFAULT 0,
    status          TEXT NOT NULL DEFAULT 'Processing',  -- Processing | Completed | PartialFailure
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at    TIMESTAMPTZ
);

CREATE TABLE batch_documents (
    batch_id        UUID NOT NULL REFERENCES batches(id) ON DELETE CASCADE,
    document_id     UUID NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    PRIMARY KEY (batch_id, document_id)
);
```

---

## Docker Compose (Reference)

```yaml
services:
  postgres:
    image: pgvector/pgvector:pg17
    environment:
      POSTGRES_DB: aikp
      POSTGRES_USER: aikp
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-aikp_dev}
    ports:
      - "5432:5432"
    volumes:
      - pgdata:/var/lib/postgresql/data

  minio:
    image: minio/minio
    command: server /data --console-address ":9001"
    environment:
      MINIO_ROOT_USER: ${MINIO_ROOT_USER:-aikp_dev}
      MINIO_ROOT_PASSWORD: ${MINIO_ROOT_PASSWORD:-aikp_dev_secret}
    ports:
      - "9000:9000"
      - "9001:9001"
    volumes:
      - miniodata:/data

  ollama:
    image: ollama/ollama
    ports:
      - "11434:11434"
    volumes:
      - ollamadata:/root/.ollama
    profiles:
      - with-ollama   # optional: docker compose --profile with-ollama up

  web:
    build: .
    ports:
      - "5001:8080"
    environment:
      ConnectionStrings__DefaultConnection: "Host=postgres;Database=aikp;Username=aikp;Password=${POSTGRES_PASSWORD:-aikp_dev}"
      Knowledge__Storage__MinIO__Endpoint: "minio:9000"
      Knowledge__Storage__MinIO__AccessKey: "${MINIO_ROOT_USER:-aikp_dev}"
      Knowledge__Storage__MinIO__SecretKey: "${MINIO_ROOT_PASSWORD:-aikp_dev_secret}"
      Knowledge__Embedding__BaseUrl: "http://ollama:11434"
    depends_on:
      - postgres
      - minio

volumes:
  pgdata:
  miniodata:
  ollamadata:
```

---

## Session Log

### 2026-02-04 — Architecture Planning Session

**Worked on**: Full architecture design for Feature #1

**Completed**:
- Designed end-to-end ingestion pipeline (upload → parse → chunk → embed → store)
- Decided on PostgreSQL + pgvector over SQLite (see decisions.md)
- Decided on MinIO for object storage (see decisions.md)
- Designed hybrid search: vector (pgvector) + keyword (Postgres FTS) + RRF fusion + optional cross-encoder reranking
- Designed batch upload support (100-200 files) with background queue + progress tracking
- Designed runtime-mutable settings system with database-backed IOptionsMonitor
- Designed Settings page UI with tabs per category (Embedding, Chunking, Search, LLM, Uploads, Web Search)
- Designed multi-surface access: Blazor UI, REST API, CLI, MCP — all calling same core services
- Designed content-hash-based reindexing for fast incremental updates
- Formalized 7-phase implementation plan with 50+ tasks
- Drafted Postgres schema and Docker Compose config

**Remaining**:
- Implement all phases (see Implementation Plan above)

**Notes**:
- Settings resolution order: appsettings.json → env vars → database (highest priority)
- Ollama is optional in docker-compose (use `--profile with-ollama`)
- IVF index on pgvector needs rebuilding after large bulk inserts (or use HNSW)

### 2026-02-05 — Phase 1: Infrastructure Complete

**Worked on**: Docker, PostgreSQL, MinIO, EF Core schema

**Completed**:
- Created `docker-compose.yml` — Postgres 17 + pgvector, MinIO, Ollama (optional profile), web app with health checks
- Created `Dockerfile` — multi-stage build (SDK restore → publish, ASP.NET runtime)
- Created `.dockerignore` — excludes build artifacts, IDE files, tests, data directories
- Added NuGet packages to Storage: `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.0, `Pgvector.EntityFrameworkCore` 0.3.0, `AWSSDK.S3` 4.0.18.2, `Microsoft.EntityFrameworkCore.Design` 10.0.0
- Created 6 EF Core entity classes: DocumentEntity, ChunkEntity (tsvector FTS), ChunkVectorEntity (pgvector), SettingEntity (JSONB), BatchEntity, BatchDocumentEntity
- Created `KnowledgeDbContext` with Fluent API: all tables, indexes (GIN for FTS, IVFFlat for vectors), cascade deletes, JSONB columns, generated tsvector column
- Created `DesignTimeDbContextFactory` for `dotnet ef` tooling
- Generated `InitialCreate` migration matching the reference schema
- Created `MinioOptions` config class
- Implemented `MinioFileSystem : IKnowledgeFileSystem` — S3-compatible file operations (list, save, open, delete, exists) with path traversal protection and bucket auto-creation
- Updated `ServiceCollectionExtensions` — registers DbContext (Postgres+pgvector), IAmazonS3 client (ForcePathStyle for MinIO), MinIO/Local file system selection
- Updated `Program.cs` — auto-applies migrations on startup, ensures MinIO bucket exists
- Updated `appsettings.json` — Postgres connection string, MinIO config, switched VectorStore to PgVector, added EF Core log filtering
- Build: 0 warnings, 0 errors

**Remaining**:
- Phase 2: Settings system
- Phase 3: Storage implementations (document store, vector store, embedding provider)
- Phase 4+: Ingestion, search, access surfaces, reindexing

**Notes**:
- `docker compose up` starts Postgres+MinIO; app auto-migrates on startup
- Ollama optional: `docker compose --profile with-ollama up`
- MinIO creds default to aikp_dev/aikp_dev_secret, overridable via env vars
- When MinIO AccessKey is configured, `IKnowledgeFileSystem` resolves to MinioFileSystem; otherwise falls back to LocalKnowledgeFileSystem

### 2026-02-05 — Database Schema Fix (snake_case column names)

**Worked on**: Fixed PostgreSQL schema column naming issue

**Completed**:
- Fixed Npgsql.PostgresException: '42703: column "content" does not exist' error
- Updated all entity configurations in KnowledgeDbContext to use explicit snake_case column names (HasColumnName())
- Applied convention across all tables: documents, chunks, chunk_vectors, settings, batches, batch_documents
- Removed old migration and generated new InitialCreate migration with proper column names
- Verified database schema created correctly with all tables, indexes (GIN for FTS, IVFFlat for pgvector), and foreign keys
- Confirmed pgvector extension v0.8.1 installed and working
- Confirmed tsvector generated column working: `to_tsvector('english', content)` now references correct column name

**Remaining**:
- Phase 2: Settings system
- Phase 3+: Storage implementations, ingestion, search, access surfaces

**Notes**:
- PostgreSQL convention is snake_case for column names; EF Core defaults to PascalCase
- Computed columns in PostgreSQL use unquoted identifiers (lowercase), so column names must match
- All 6 entity classes now have explicit column name mappings for consistency

### 2026-02-04 — Dark Theme, Upload Page, File System Management

**Worked on**: UI foundation and file system service

**Completed**:
- Dark mode theme with purple accents (#8b5cf6) using Bootstrap 5 `data-bs-theme="dark"` + CSS custom properties
- Rewrote app.css with full dark theme tokens (surfaces, borders, accents, form controls, scrollbars)
- Updated MainLayout: removed top-row, dark sidebar with border
- Updated NavMenu: rebranded to "AIKnowledge", added Upload + Search nav items, purple active states
- Created Home page with feature cards (Upload, Search, Agent-Ready)
- Created Upload page placeholder with drag-drop zone, destination folder input, chunking strategy selector, coming-soon notice
- Scoped CSS for Home.razor and Upload.razor
- Created `IKnowledgeFileSystem` interface in Core (ResolvePath, ListAsync, SaveFileAsync, OpenFileAsync, DeleteAsync, etc.)
- Added `FileSystemEntry` record and `KnowledgeFileSystemOptions` to Core models
- Implemented `LocalKnowledgeFileSystem` in Storage — maps virtual paths like "/folder/a/b" to physical "{RootPath}/folder/a/b" with path traversal protection
- Created Storage DI extension `AddAIKnowledgeStorage()` with options binding
- Wired up file system service in Program.cs
- Added `Knowledge:FileSystem:RootPath` config (default: "knowledge-data")
- Build: 0 warnings, 0 errors; all tests pass

**Remaining**:
- Connect Upload page to ingestion pipeline
- Implement document parsers, chunkers, embedding, vector storage
- Build search UI and functionality
- Implement agent orchestration

**Notes**:
- File system root defaults to `knowledge-data/` relative to working directory
- Virtual path "/uploads/doc.pdf" → physical "{cwd}/knowledge-data/uploads/doc.pdf"
- Path traversal attacks blocked (throws UnauthorizedAccessException if resolved path escapes root)

### 2026-02-04 — Project Initialization

**Worked on**: Full project initialization per `.claude/commands/init.md`

**Completed**:
- Created solution with 7 source projects (Web, Core, Ingestion, Search, Agents, Storage, CLI)
- Created 3 test projects (Core.Tests, Ingestion.Tests, Integration.Tests)
- Established all project references per architecture (Core referenced by all, Web/CLI reference feature projects, cross-feature refs)
- Created all feature subdirectories (Parsers, Chunking, Pipeline, Vector, Hybrid, Web, Tools, Memory, Orchestration, etc.)
- Defined all core interfaces: IKnowledgeIngester, IKnowledgeSearch, IEmbeddingProvider, IVectorStore, IWebSearchProvider, IAgentTool, IAgentMemory, IDocumentStore, IFileStore
- Created model records: IngestionOptions/Result/Progress, SearchOptions/Result/Hit, ToolResult/Context, Note/NoteOptions, Document, Chunk, VectorSearchResult, WebSearchResult/Hit/Options, Result<T>
- Added ServiceCollectionExtensions pattern for DI registration
- Updated .gitignore to match init spec
- Configured appsettings.json with all configurable strategy defaults
- Added FluentAssertions and Microsoft.AspNetCore.Mvc.Testing to test projects
- Build succeeds: 0 warnings, 0 errors; all 3 test projects pass

**Remaining**:
- Implement basic Blazor shell with file upload UI
- Create ingestion pipeline (parsers, chunkers, embedding)
- Implement vector storage (SQLite-vec adapter)
- Build search functionality
- Implement agent orchestration

**Notes**:
- Restructured from default VS template (flat `AIKnowledgePlatform/`) to proper `src/` layout
- Using .NET 10 SDK 10.0.102
- Local-first defaults: Ollama for embeddings/LLM, SQLite-vec for vectors

### 2026-02-04 — Phase 2: Settings System Complete

**Worked on**: Runtime-mutable settings with database backing and live reload

**Completed**:
- Created `ISettingsStore` interface in Core (GetAsync, SaveAsync, ResetAsync, GetCategoriesAsync)
- Defined 7 settings record types in `SettingsModels.cs`: `EmbeddingSettings`, `ChunkingSettings`, `SearchSettings`, `LlmSettings`, `UploadSettings`, `WebSearchSettings`, `StorageSettings`
- Implemented `PostgresSettingsStore : ISettingsStore` — uses JSONB column in `settings` table, JSON serialization for flexible schema
- Created `DatabaseSettingsProvider : IConfigurationProvider` — custom configuration provider that loads settings from database at startup
- Created `DatabaseSettingsSource : IConfigurationSource` — wires up the provider
- Created `SettingsReloadService` — triggers `IOptionsMonitor` change notifications when settings are updated
- Added `ConfigurationBuilderExtensions.AddDatabaseSettings()` extension method
- Updated `Program.cs` — adds database configuration source, configures `IOptionsMonitor<T>` for all 7 settings categories
- Registered `PostgresSettingsStore` and `SettingsReloadService` in DI
- Created Settings page (`/settings`) with 7-tab layout: Embedding, Chunking, Search, LLM, Upload, Web Search, Storage
- Created 7 tab components with EditForm binding: `EmbeddingSettingsTab`, `ChunkingSettingsTab`, `SearchSettingsTab`, `LlmSettingsTab`, `UploadSettingsTab`, `WebSearchSettingsTab`, `StorageSettingsTab`
- Updated NavMenu: added Settings link with gear icon
- Fixed settings records: changed `init` to `set` for form binding compatibility
- Build: 0 warnings, 0 errors

**Remaining**:
- Task 2.7: Test Connection functionality (deferred — requires actual service implementations)
- Task 2.8: REST API endpoints for settings (deferred — optional for now)
- Phase 3: Storage implementations (Ollama embedding provider, Postgres document store, PgVector store, FTS indexing)
- Phase 4+: Ingestion, search, access surfaces, reindexing

**Notes**:
- Settings resolution order: `appsettings.json` → env vars → **database (highest priority)**
- When settings are saved via UI: writes to DB → calls `SettingsReloadService.ReloadSettings()` → triggers `IOptionsMonitor` → all consumers see new values without restart
- Settings stored as JSONB in `settings` table, flattened to configuration keys like `Knowledge:Embedding:Model`
- All 7 settings categories fully editable through Settings page UI

### 2026-02-04 — Phase 3: Storage Implementations Complete

**Worked on**: Core storage layer implementations

**Completed**:
- **Task 4.1**: Implemented `OllamaEmbeddingProvider : IEmbeddingProvider` — calls Ollama `/api/embeddings` endpoint with batch support, configurable timeout, dimension validation
- **Task 4.2**: Implemented `PostgresDocumentStore : IDocumentStore` — full CRUD operations for documents, collection filtering, proper GUID handling
- **Task 4.3**: Implemented `PgVectorStore : IVectorStore` — pgvector-backed vector storage with cosine similarity search using raw SQL to leverage `<=>` operator, supports filters (documentId, collectionId), batch deletion
- **Task 4.4**: FTS indexing already implemented via computed `tsvector` column in database schema (auto-updates on chunk insert/update)
- Added `Microsoft.Extensions.Http` package to Storage project for HttpClient DI
- Registered all storage implementations in `ServiceCollectionExtensions`
- Build: 0 warnings, 0 errors

**Remaining**:
- Phase 4: Ingestion Pipeline (parsers, chunkers, pipeline orchestrator, background worker, queue)
- Phase 5: Search (vector search, keyword search, RRF fusion, hybrid search service)
- Phase 6: Access Surfaces (REST API, Upload page, Search page, CLI commands, MCP tools)
- Phase 7: Reindexing + Polish (reindex service, content hash comparison, tests)

**Notes**:
- OllamaEmbeddingProvider uses HttpClient with typed client pattern for proper lifecycle management
- PgVectorStore uses raw SQL with parameterized queries to access pgvector's native `<=>` cosine distance operator
- Cosine distance (0-2) is converted to similarity score (0-1) via `1 - distance` formula
- FTS search_vector column is automatically maintained by PostgreSQL as a stored generated column
- All implementations use async/await throughout with proper cancellation token support

---
