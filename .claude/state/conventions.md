# Project Conventions

Patterns and style choices specific to Connapse. Update when new patterns emerge to keep future sessions consistent.

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

### DbContext Threading Pattern (CRITICAL!)
- **DbContext is NOT thread-safe** — cannot have concurrent operations on the same context instance
- **Problem**: Parallel async operations (e.g., `Task.WhenAll`) sharing the same scoped DbContext will throw `InvalidOperationException`
- **Solution**: Use `IServiceScopeFactory` to create separate scopes for each parallel operation
- **When to use**: Any service that needs to run parallel database operations (searches, updates, etc.)
- **Pattern**:
```csharp
public class HybridSearchService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public HybridSearchService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    private async Task<List<Result>> ParallelSearchAsync(...)
    {
        // Each Task.Run gets its own scope and DbContext
        var task1 = Task.Run(async () =>
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<VectorSearchService>();
            return await service.SearchAsync(...);
        }, ct);

        var task2 = Task.Run(async () =>
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<KeywordSearchService>();
            return await service.SearchAsync(...);
        }, ct);

        await Task.WhenAll(task1, task2);
        // Combine results...
    }
}
```
- **Key points**:
  - Inject `IServiceScopeFactory` (not the concrete services directly)
  - Create a new scope for each parallel operation
  - Use `await using` for proper scope disposal
  - Each scope resolves its own scoped dependencies (including DbContext)
  - Do NOT share scoped services across Task.Run boundaries

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
- DI registration via `services.AddConnapseStorage(configuration)` in Storage project

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

### JSONB Pattern (CRITICAL!)
- **ALWAYS** use `JsonDocument` for JSONB columns, **NEVER** `Dictionary<string, object>`
- Why: Npgsql's `EnableDynamicJson()` only supports `Dictionary<string, string>`, NOT `Dictionary<string, object>`
- Reading: Use `jsonDoc.RootElement.GetRawText()` then `JsonSerializer.Deserialize<T>(json)`
- Writing: Use `JsonSerializer.Serialize(obj)` then `JsonDocument.Parse(json)`
- Example entity property: `public JsonDocument Values { get; set; } = null!;`
- This applies to ALL JSONB columns (settings, metadata, flexible schemas)

## Object Storage (MinIO / S3)

- All original uploaded files stored in MinIO (S3-compatible)
- Use `AWSSDK.S3` client — works with MinIO and real AWS S3 interchangeably
- Bucket name configurable via `StorageSettings`
- `IKnowledgeFileSystem` remains the abstraction — `MinioFileSystem` for Docker, `LocalKnowledgeFileSystem` for local dev without Docker

## Settings (Phase 2 ✅)

- Runtime-mutable settings stored in Postgres `settings` table (category + JSONB `JsonDocument` values)
- Each settings category is a record type: `EmbeddingSettings`, `ChunkingSettings`, etc.
- All properties use `{ get; set; }` (not `init`) for EditForm binding compatibility
- Services inject `IOptionsMonitor<T>` (not `IOptions<T>`) to support live reload
- Resolution order: `appsettings.json` → env vars → **database (highest priority)**
- Settings page at `/settings` with 7 tabs
- **Reload Architecture** (CRITICAL — uses DI, NOT static fields):
  - `ISettingsReloader` service (singleton) wraps `DatabaseSettingsProvider`
  - `PostgresSettingsStore` calls `settingsReloader.Reload()` after saving to DB
  - `DatabaseSettingsProvider.Reload()` calls `Load()` then `OnReload()`
  - `OnReload()` triggers change token → `IOptionsMonitor` updates
  - `DatabaseSettingsProvider` registered as singleton in Program.cs
- `DatabaseSettingsProvider` is a custom `IConfigurationProvider` that:
  - Loads settings from DB at startup
  - Flattens JSONB `JsonDocument` into config keys (e.g., `Knowledge:Embedding:Model`)
  - Triggers `IOptionsMonitor` change notifications on reload
- Implementation: `ISettingsStore` → `PostgresSettingsStore` (`JsonDocument` serialization to/from JSONB)

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
- **Uses `IServiceScopeFactory`** instead of injecting search services directly (avoids DbContext threading issues)
- Routes to appropriate search based on `SearchOptions.Mode`
- For single-mode searches (Semantic/Keyword):
  - Creates a scope to resolve the appropriate search service
  - Executes search within that scope
- For Hybrid mode:
  - Runs vector and keyword searches in **parallel with separate scopes**
  - Each `Task.Run` creates its own scope and resolves its own search service
  - Each scope gets its own DbContext instance → no concurrent access issues
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
- **Note**: HybridSearchService injects `IServiceScopeFactory` (not search services directly) for thread-safe parallel execution

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

### JSON Serialization
- **CRITICAL**: ASP.NET Core uses camelCase for JSON by default, but `JsonSerializer.Deserialize` uses case-sensitive matching by default
- Always use `JsonSerializerOptions` with `PropertyNameCaseInsensitive = true` when deserializing from API requests
- Example pattern (SettingsEndpoints.cs):
```csharp
private static readonly JsonSerializerOptions JsonOptions = new()
{
    PropertyNameCaseInsensitive = true
};

// Use in deserialization
var settings = JsonSerializer.Deserialize<ChunkingSettings>(json, JsonOptions);
```
- **Why**: Tests and clients send `{ "baseUrl": "..." }` (camelCase), but C# properties are `BaseUrl` (PascalCase)
- Without case-insensitive options, properties deserialize as null, causing subtle bugs

## Testing

### Integration Tests
- Use `WebApplicationFactory<Program>` for full-stack testing with real services
- Use `Testcontainers` for PostgreSQL and MinIO — real databases, not mocks
- Test helpers must match actual API behavior (form field names, response DTOs)
- **Wait for completion properly**: Use status endpoints to verify operations completed, don't just check existence
  - Example: Use `/api/documents/{id}/reindex-check` to verify Status="Ready" before proceeding
  - Wait for `NeedsReindex=false`, not just document existence
- **Form data patterns**:
  - Upload endpoint expects `destinationPath` and `collectionId` as separate form fields
  - Don't conflate virtualPath (internal storage location) with collectionId (logical grouping)
- **Null checks**: Always add null-conditional operators for properties that can be null (e.g., `response.S3Objects?.Count`)

### Test Naming
- `MethodName_Scenario_ExpectedResult`
- Examples: `IngestAsync_ValidTextFile_CreatesChunks`, `Search_WithFilters_ReturnsFilteredResults`

## Docker

- `docker-compose.yml` at repo root with: postgres (pgvector), minio, ollama (optional profile), web app
- Dockerfile uses multi-stage build (restore → build → publish → runtime)
- Secrets via environment variables, never baked into images
- Ollama is optional via `--profile with-ollama`

## Containers & File Paths (Feature #2 — Implemented)

### Container Model
- Containers are top-level isolation units representing projects
- Each container has its own logical vector space (search isolation)
- Container names: lowercase alphanumeric + hyphens, 2-128 chars, unique across system
- Containers cannot be deleted if they contain files or folders
- `IContainerStore` interface: `CreateAsync`, `GetAsync`, `GetByNameAsync`, `ListAsync`, `DeleteAsync`, `ExistsAsync`
- Implementation: `PostgresContainerStore`

### Path Structure
- Full path format: `/{container-name}/{folder-path}/{filename}`
- Paths stored normalized: no trailing slashes on files, trailing slash on folders
- Forward slashes only (even on Windows)
- Path determines uniqueness, not content hash
- `PathUtilities` static class handles validation and normalization

### Duplicate Handling
- Same filename in same folder -> `file (1).pdf`, `file (2).pdf`, etc.
- Pattern: `{basename} ({n}){extension}`
- Check for conflicts before upload, increment N until unique
- Same file content in different containers = completely separate files

### Container Operations
- Create: generates UUID, validates name uniqueness
- Delete: fails if not empty (must delete contents first, returns 400)
- No cross-container file moves (delete + re-upload required)
- No cross-container search

### Folder Operations
- Empty folders explicitly supported via `folders` table
- `IFolderStore` interface: `CreateAsync`, `ListAsync`, `DeleteAsync`, `ExistsAsync`
- Implementation: `PostgresFolderStore`
- Folder deletion: cascade delete all nested files/folders/chunks
- `ListAsync` returns only immediate children (filters out nested subfolders)

### Search Scoping
- Search requires container ID (no global search)
- Optional path filter: restricts to subtree (recursive LIKE query)
- Optional minScore filter: configurable per request or via SearchSettings
- Path filter example: `/docs/2026/` searches all files under that folder

### Ingestion Job Cancellation
- `IIngestionQueue.CancelJobForDocumentAsync(documentId)` cancels in-progress jobs
- `IngestionWorker` creates per-job `CancellationTokenSource` linked to app shutdown token
- Queue tracks job-to-document mapping for cancellation lookup

### Database Schema
- `containers` table: id, name (unique), description, created_at, updated_at
- `documents.container_id`: required FK to containers (cascade delete)
- `chunks.container_id`: denormalized for query performance
- `chunk_vectors.container_id`: denormalized for query performance
- `folders` table: id, container_id, path (unique per container), created_at
- `CollectionId` removed entirely (replaced by containers)
- Indexes: `idx_documents_container_id`, `idx_documents_container_path` (unique composite), `idx_chunks_container_id`, `idx_chunk_vectors_container_id`

---

<!-- Add new conventions as they emerge -->
