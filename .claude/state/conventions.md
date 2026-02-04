# Project Conventions

Patterns and style choices specific to AIKnowledgePlatform. Update when new patterns emerge to keep future sessions consistent.

---

## Naming

- **Services**: `{Domain}Service` → `IngestionService`, `SearchService`
- **Interfaces**: `I{Name}` → `IKnowledgeIngester`, `IVectorStore`
- **DTOs**: `{Name}Dto` (external API), `{Name}Model` (internal)
- **Options**: `{Feature}Options` → `IngestionOptions`, `SearchOptions`
- **Results**: `{Operation}Result` → `IngestionResult`, `SearchResult`

## File Organization

- One public type per file
- Group by feature, not by type (Controllers/, Services/, etc.)
- Tests mirror source structure

## Async

- All I/O is async
- `Async` suffix on async methods
- Use `ValueTask<T>` for hot paths that often complete synchronously
- Prefer `await foreach` over `.ToListAsync()`

## Error Handling

- `Result<T>` pattern for expected failures (validation, not found)
- Exceptions for unexpected/programmer errors only
- Error messages must be actionable

## Configuration

- `IOptions<T>` everywhere
- Validate at startup with `ValidateOnStart()`
- Sensible defaults in `appsettings.json`
- Never hardcode connection strings or secrets

## Dependency Injection

- Register in feature-specific extension methods: `services.AddIngestion()`
- Prefer interfaces over concrete types
- Scoped for per-request, Singleton for stateless services

## Blazor UI

- Dark theme with purple accents — use CSS custom properties (`var(--surface)`, `var(--accent)`, etc.)
- Scoped CSS per component for component-specific styles (`.razor.css`)
- Global theme tokens in `wwwroot/app.css`
- Use inline SVGs or data-URI SVGs for icons (no external icon library dependency)
- `@rendermode InteractiveServer` on pages that need interactivity

## File System

- All file operations go through `IKnowledgeFileSystem` — never use raw `System.IO` paths for user content
- Virtual paths use forward slashes (`/folder/a/b`)
- Physical paths resolved relative to configured `RootPath` (default: `knowledge-data/`)
- DI registration via `services.AddAIKnowledgeStorage(configuration)` in Storage project

## Database (PostgreSQL)

- PostgreSQL 17 with pgvector extension for vector storage
- Use raw SQL or EF Core — decision deferred to implementation
- tsvector columns for FTS, generated from chunk content
- JSONB for flexible metadata and settings storage
- UUIDs for all primary keys (`gen_random_uuid()`)
- `TIMESTAMPTZ` for all timestamps
- CASCADE deletes: deleting a document removes its chunks and vectors

## Object Storage (MinIO / S3)

- All original uploaded files stored in MinIO (S3-compatible)
- Use `AWSSDK.S3` client — works with MinIO and real AWS S3 interchangeably
- Bucket name configurable via `StorageSettings`
- `IKnowledgeFileSystem` remains the abstraction — `MinioFileSystem` for Docker, `LocalKnowledgeFileSystem` for local dev without Docker

## Settings

- Runtime-mutable settings stored in Postgres `settings` table (JSONB per category)
- Each settings category has a `{Name}Settings` class with a `const string Category` field
- Services use `IOptionsMonitor<T>` (not `IOptions<T>`) to support live reload
- Resolution order: `appsettings.json` → env vars → database (highest priority)
- API keys masked in UI, overridable via environment variables for production

## Ingestion

- Background processing via `Channel<T>` + `IHostedService` — no external message queue
- Content-hash deduplication (SHA-256) for fast reindexing
- Parsers registered via `IDocumentParser` — matched by file extension / content type
- Chunking strategies implement `IChunkingStrategy` — selected per settings or per-upload override
- Batch uploads return immediately with a batch ID; progress via polling or SignalR

## Search

- Hybrid search: vector (pgvector) + keyword (Postgres FTS) run in parallel
- Results fused via `ISearchReranker` — default `RrfReranker`, optional `CrossEncoderReranker`
- Over-fetch by 2x from each source before fusion (e.g., topK=10 → fetch 20 from each)

## API Endpoints

- Minimal API in `Endpoints/` folder, not controllers
- Group endpoints by resource: `DocumentEndpoints`, `SearchEndpoints`, `SettingsEndpoints`
- Return `Results.Ok()` / `Results.NotFound()` / `Results.Problem()` — standard problem details for errors

## Docker

- `docker-compose.yml` at repo root with: postgres (pgvector), minio, ollama (optional profile), web app
- Dockerfile uses multi-stage build (restore → build → publish → runtime)
- Secrets via environment variables, never baked into images
- Ollama is optional via `--profile with-ollama`

---

<!-- Add new conventions as they emerge -->
