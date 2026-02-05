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

## Type Safety

- **NEVER use `dynamic` keyword** — always use strongly-typed DTOs
  - `dynamic` doesn't work with `System.Text.Json.JsonElement` (e.g., SignalR, API responses)
  - No compile-time checking, no IntelliSense, runtime binding overhead
  - Create proper record types instead (e.g., `IngestionProgressUpdate`)
- Use records for DTOs and immutable data structures
- Nullable reference types enabled globally — handle null explicitly

## Error Handling

- `Result<T>` pattern for expected failures (validation, not found)
- Exceptions for unexpected/programmer errors only
- Error messages must be actionable

## Configuration

- Use `IOptionsMonitor<T>` for runtime-mutable settings (not `IOptions<T>`)
- Settings categories: Embedding, Chunking, Search, LLM, Upload, WebSearch, Storage
- All settings are records with `{ get; set; }` for form binding
- Sensible defaults in `appsettings.json`
- Never hardcode connection strings or secrets
- Database overrides via `DatabaseSettingsProvider` (custom `IConfigurationProvider`)

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

### Blazor HttpClient Pattern
- **CRITICAL**: Never register scoped `HttpClient` that resolves `NavigationManager` during service configuration
- Use named `HttpClient` registration: `builder.Services.AddHttpClient("BlazorClient")` (no configuration callback)
- Components inject both `IHttpClientFactory` and `NavigationManager`
- Components create client and set `BaseAddress` lazily in property getter:
```csharp
@inject IHttpClientFactory HttpClientFactory
@inject NavigationManager Navigation

@code {
    private HttpClient? _httpClient;
    private HttpClient Http
    {
        get
        {
            if (_httpClient == null)
            {
                _httpClient = HttpClientFactory.CreateClient("BlazorClient");
                _httpClient.BaseAddress = new Uri(Navigation.BaseUri);
            }
            return _httpClient;
        }
    }
}
```
- **Why**: Background services need typed `HttpClient` registrations (e.g., `OllamaEmbeddingProvider`) that don't depend on `NavigationManager`, which only exists in HTTP request/Blazor contexts

### Blazor Threading
- **CRITICAL**: All SignalR callbacks MUST use `InvokeAsync()` to marshal state changes to UI thread
- SignalR messages arrive on background threads — direct `StateHasChanged()` calls will crash
- Pattern: `async Task Handler(T data) => await InvokeAsync(() => { /* mutate state, call StateHasChanged() */ })`
- Always make handler methods `async Task`, never `void`, when using `InvokeAsync()`
- This applies to ANY callback from background threads (timers, event handlers, etc.)

## SignalR

- Always use strongly-typed handlers: `hubConnection.On<TDto>("EventName", handler)`
- Create DTOs for all SignalR messages (e.g., `IngestionProgressUpdate`)
- Send typed objects from server: `Clients.Group(id).SendAsync("Event", new TDto(...))`
- Never use `dynamic` — `System.Text.Json` deserializes to `JsonElement` which doesn't support dynamic binding

## File System

- All file operations go through `IKnowledgeFileSystem` — never use raw `System.IO` paths for user content
- Virtual paths use forward slashes (`/folder/a/b`)
- Physical paths resolved relative to configured `RootPath` (default: `knowledge-data/`)
- DI registration via `services.AddAIKnowledgeStorage(configuration)` in Storage project

## Database (PostgreSQL)

- PostgreSQL 17 with pgvector extension for vector storage
- EF Core for schema management with code-first migrations
- **Column naming**: snake_case (PostgreSQL convention) — use `.HasColumnName("column_name")` on all properties
- Table names: lowercase with underscores (`documents`, `chunk_vectors`)
- tsvector columns for FTS, generated from chunk content via computed column
- JSONB for flexible metadata and settings storage
- UUIDs for all primary keys (`gen_random_uuid()`)
- `TIMESTAMPTZ` for all timestamps
- CASCADE deletes: deleting a document removes its chunks and vectors
- Computed columns must reference lowercase unquoted column names (e.g., `to_tsvector('english', content)`)

## Object Storage (MinIO / S3)

- All original uploaded files stored in MinIO (S3-compatible)
- Use `AWSSDK.S3` client — works with MinIO and real AWS S3 interchangeably
- Bucket name configurable via `StorageSettings`
- `IKnowledgeFileSystem` remains the abstraction — `MinioFileSystem` for Docker, `LocalKnowledgeFileSystem` for local dev without Docker

## Settings (Phase 2 ✅)

- Runtime-mutable settings stored in Postgres `settings` table (category + JSONB values)
- Each settings category is a record type: `EmbeddingSettings`, `ChunkingSettings`, etc.
- All properties use `{ get; set; }` (not `init`) for EditForm binding compatibility
- Services inject `IOptionsMonitor<T>` (not `IOptions<T>`) to support live reload
- Resolution order: `appsettings.json` → env vars → **database (highest priority)**
- Settings page at `/settings` with 7 tabs, save triggers `SettingsReloadService.ReloadSettings()`
- `DatabaseSettingsProvider` is a custom `IConfigurationProvider` that:
  - Loads settings from DB at startup
  - Flattens JSONB into config keys (e.g., `Knowledge:Embedding:Model`)
  - Triggers `IOptionsMonitor` change notifications on reload
- Implementation: `ISettingsStore` → `PostgresSettingsStore` (JSON serialization to/from JSONB)

## Connection Testing (Settings Feature ✅)

- Test external service connectivity BEFORE saving settings to database
- `IConnectionTester` interface with `TestConnectionAsync(object settings, TimeSpan? timeout, CancellationToken)`
- Implementations test specific services:
  - `OllamaConnectionTester`: Tests Ollama via GET /api/tags (used for Embedding + LLM settings)
  - `MinioConnectionTester`: Tests MinIO/S3 via ListBucketsAsync (used for Storage settings)
- Returns structured `ConnectionTestResult`:
  - `Success` (bool): Test passed/failed
  - `Message` (string): Human-readable result (e.g., "Connected to Ollama (3 models available)")
  - `Details` (Dictionary): Structured info (modelCount, buckets, error details)
  - `Duration` (TimeSpan): Test execution time
- Default timeout: 10 seconds (configurable per test)
- UI pattern in Settings tabs:
  - "Test Connection" button next to Save/Reset
  - Spinner animation during test (`isTestingConnection` state)
  - Success alert (green) or error alert (red) with dismissible close button
  - Tests current form values via `HttpClient.PostAsJsonAsync("/api/settings/test-connection")`
- API endpoint: `POST /api/settings/test-connection`
  - Body: `{ Category: string, Settings: JsonElement, TimeoutSeconds?: int }`
  - Returns `ConnectionTestResult` JSON
- Registered as Scoped services in Storage DI extensions
- Uses reflection to extract BaseUrl/endpoint properties from settings objects (supports multiple settings types)
- Test connection does NOT modify any database state — read-only validation

## Ingestion (Phase 4 ✅)

- Background processing via `Channel<T>` + `IHostedService` (BackgroundService) — no external message queue
- Content-hash deduplication (SHA-256) for fast reindexing — stored in `documents.content_hash`
- Parsers registered via `IDocumentParser` — matched by file extension in `SupportedExtensions`
- Chunking strategies implement `IChunkingStrategy` — selected by `Name` property, not enum
- Batch uploads return immediately with a batch ID; progress via polling or SignalR

### Document Parsers
- Each parser declares `SupportedExtensions` (e.g., `[".txt", ".md", ".csv"]`)
- Return `ParsedDocument` with content, metadata dict, and warnings list
- Extract metadata: file type, line count, document properties (title, author, creation date)
- Use `Task.Run()` to wrap synchronous parsing libraries (PdfPig, OpenXML) for cancellation support
- Always return a result — empty content + warnings on error, never throw directly

### Chunking Strategies
- Identify by `Name` string property (e.g., "FixedSize", "Recursive", "Semantic")
- Return `IReadOnlyList<ChunkInfo>` with content, index, token count, offsets, metadata
- Token counting via `TokenCounter.EstimateTokenCount()` heuristic (~0.25 tokens/char)
- Natural boundary detection for better chunk quality (paragraphs → sentences → spaces)
- SemanticChunker requires `IEmbeddingProvider` dependency — registered as Transient

### Pipeline & Queue
- `IngestionPipeline` orchestrates: parse → chunk → embed → store
- **CRITICAL**: `IngestionOptions` must include `DocumentId` when calling `IngestAsync()` to preserve pre-assigned IDs
  - Upload endpoint generates DocumentId → passes to IngestionJob → must flow to IngestionOptions
  - Pipeline uses `options.DocumentId` if provided, else generates new Guid
  - All callers (DocumentsEndpoints, ReindexService, McpServer) must pass DocumentId
- Computes SHA-256 hash of file content for deduplication
- Stores chunks in `chunks` table, embeddings in `chunk_vectors` via `IVectorStore`
- **PgVectorStore metadata requirements**: Must include lowercase "documentId" and "modelId" keys
- Updates document status: Pending → Processing → Ready | Failed
- `IngestionQueue` uses bounded `Channel<T>` (capacity: 1000)
- Job status tracked in-memory via `ConcurrentDictionary<string, IngestionJobStatus>`
- `IngestionWorker` spawns N parallel workers based on `UploadSettings.ParallelWorkers`
- Each worker dequeues jobs, processes via pipeline, updates status

### Registration
- `services.AddDocumentIngestion()` in Ingestion project's ServiceCollectionExtensions
- Parsers: Singleton (stateless)
- Chunkers: FixedSize/Recursive = Singleton, Semantic = Transient (has IEmbeddingProvider dependency)
- Queue: Singleton (shared state)
- Pipeline: Scoped (per-request or per-job)
- Worker: AddHostedService (runs in background)

## Search (Phase 5 ✅)

- Hybrid search: vector (pgvector) + keyword (Postgres FTS) run in parallel
- Results fused via `ISearchReranker` — default `RrfReranker`, optional `CrossEncoderReranker`
- Three search modes: `Semantic` (vector only), `Keyword` (FTS only), `Hybrid` (both + reranking)

### Vector Search
- `VectorSearchService` embeds query via `IEmbeddingProvider`, searches `IVectorStore`
- Supports filters: `collectionId`, `documentId` (merged from SearchOptions.Filters)
- Converts `VectorSearchResult` → `SearchHit` format
- Applies `MinScore` threshold before returning

### Keyword Search
- `KeywordSearchService` queries Postgres FTS using `tsvector` and `plainto_tsquery`
- Uses `ts_rank()` for TF-IDF-like relevance scoring
- Normalizes ranks to 0-1 range (handles edge case where all ranks identical)
- Query sanitization: removes tsquery special characters, collapses spaces
- Raw SQL via `Database.SqlQueryRaw<T>()` for full FTS control

### Reranking
- `RrfReranker`: Reciprocal Rank Fusion using formula `score = sum(1 / (k + rank))`
  - Groups hits by "source" metadata ("vector" or "keyword")
  - Builds ranked lists per source (1-indexed)
  - Accumulates RRF scores for chunks appearing in multiple lists
  - Normalizes final scores to 0-1 range
  - Configurable k-value via `SearchSettings.RrfK` (default: 60)
- `CrossEncoderReranker`: LLM-based reranking
  - Scores each (query, chunk) pair via Ollama API
  - Prompt asks LLM to rate relevance 0-10
  - Low temperature (0.1) for consistent scoring
  - Configured via `SearchSettings.CrossEncoderModel`
  - Fallback to original scores on error
  - Registered with `AddHttpClient<T>()` for proper HttpClient lifecycle

### Hybrid Search Orchestration
- `HybridSearchService` implements `IKnowledgeSearch` (main entry point)
- Routes to appropriate search based on `SearchOptions.Mode`
- For Hybrid mode:
  - Runs vector and keyword searches in parallel via `Task.WhenAll()`
  - Tags results with "source" metadata before merging
  - Passes combined list to configured reranker
- Applies final score threshold and topK limit after reranking
- `SearchStreamAsync` currently returns batch results (can enhance for true streaming)
- Reports timing via `Stopwatch` in `SearchResult.Duration`

### Registration
- `services.AddKnowledgeSearch()` in Search project's ServiceCollectionExtensions
- VectorSearchService: Scoped (depends on IVectorStore, IEmbeddingProvider)
- KeywordSearchService: Scoped (depends on DbContext)
- RrfReranker: Scoped, implements ISearchReranker
- CrossEncoderReranker: registered via AddHttpClient<ISearchReranker, T> (scoped with typed HttpClient)
- HybridSearchService: Scoped, registered as IKnowledgeSearch implementation
- All rerankers injected as `IEnumerable<ISearchReranker>` for runtime selection by name

## Storage Implementations (Phase 3 ✅)

### Embedding Provider
- Use `AddHttpClient<T>()` for typed clients calling external APIs (e.g., Ollama)
- Inject `IOptionsMonitor<EmbeddingSettings>` for configuration
- Batch processing: divide requests into configurable batch sizes (default: 32)
- Validate dimensions match settings, log warnings on mismatch
- Throw `InvalidOperationException` with helpful messages on connection failures

### Vector Store (pgvector)
- Use **raw SQL with parameterized queries** to access pgvector's native `<=>` operator for cosine distance
- EF Core LINQ doesn't support pgvector operators directly yet
- Query pattern: `SELECT ... FROM chunk_vectors WHERE ... ORDER BY embedding <=> $1 LIMIT $2`
- Convert **distance → similarity**: `similarity = 1.0 - distance` (distance 0-2, similarity 0-1)
- Always include chunk content in results via JOIN on chunks table
- Filter support: `documentId`, `collectionId` via WHERE clauses
- Use `Database.SqlQueryRaw<T>()` with DTO record for result mapping

### Document Store
- All IDs are GUIDs — parse and validate before queries
- Use `.AsNoTracking()` for read-only queries
- Return `null` for not found (don't throw)
- Log warnings for invalid IDs, log info for successful operations
- Cascade deletes handled by DB schema — just delete document entity

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
