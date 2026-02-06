# API Surface

Public interfaces and contracts. Track breaking changes here.

---

## Core Interfaces

### IKnowledgeIngester

```csharp
public interface IKnowledgeIngester
{
    Task<IngestionResult> IngestAsync(Stream content, IngestionOptions options, CancellationToken ct = default);
    Task<IngestionResult> IngestFromPathAsync(string path, IngestionOptions options, CancellationToken ct = default);
    IAsyncEnumerable<IngestionProgress> IngestWithProgressAsync(Stream content, IngestionOptions options, CancellationToken ct = default);
}

public record IngestionOptions(
    string? DocumentId = null,
    string? FileName = null,
    string? ContentType = null,
    string? CollectionId = null,
    ChunkingStrategy Strategy = ChunkingStrategy.Semantic,
    Dictionary<string, string>? Metadata = null);

public record IngestionResult(string DocumentId, int ChunkCount, TimeSpan Duration, List<string> Warnings);
public record IngestionProgress(IngestionPhase Phase, double PercentComplete, string? Message);
public enum IngestionPhase { Parsing, Chunking, Embedding, Storing, Complete }
public enum ChunkingStrategy { Semantic, FixedSize, Recursive, DocumentAware }
```

### IKnowledgeSearch

```csharp
public interface IKnowledgeSearch
{
    Task<SearchResult> SearchAsync(string query, SearchOptions options, CancellationToken ct = default);
    IAsyncEnumerable<SearchHit> SearchStreamAsync(string query, SearchOptions options, CancellationToken ct = default);
}

public record SearchOptions(
    int TopK = 10,
    float MinScore = 0.7f,
    string? CollectionId = null,
    SearchMode Mode = SearchMode.Hybrid,
    Dictionary<string, string>? Filters = null);

public record SearchResult(List<SearchHit> Hits, int TotalMatches, TimeSpan Duration);
public record SearchHit(string ChunkId, string DocumentId, string Content, float Score, Dictionary<string, string> Metadata);
public enum SearchMode { Semantic, Keyword, Hybrid }
```

### IEmbeddingProvider

```csharp
public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default);
    int Dimensions { get; }
    string ModelId { get; }
}

// Phase 3 Implementation ✅
// - OllamaEmbeddingProvider: calls Ollama POST /api/embeddings
//   - HttpClient with typed client pattern
//   - Batch processing (sends multiple parallel requests)
//   - Dimension validation with warnings
//   - Configurable timeout (default: 30s)
```

### IVectorStore

```csharp
public interface IVectorStore
{
    Task UpsertAsync(string id, float[] vector, Dictionary<string, string> metadata, CancellationToken ct = default);
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(float[] queryVector, int topK, Dictionary<string, string>? filters = null, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task DeleteByDocumentIdAsync(string documentId, CancellationToken ct = default);
}

// Phase 3 Implementation ✅
// - PgVectorStore: PostgreSQL + pgvector extension
//   - Raw SQL with parameterized queries for <=> operator
//   - Cosine distance → similarity conversion (1 - distance)
//   - Filters: documentId, collectionId via WHERE clauses
//   - JOINs chunks + documents for complete result metadata
//   - Batch deletion by document ID for reindexing
```

### IDocumentStore

```csharp
public interface IDocumentStore
{
    Task<string> StoreAsync(Document document, CancellationToken ct = default);
    Task<Document?> GetAsync(string documentId, CancellationToken ct = default);
    Task<IReadOnlyList<Document>> ListAsync(string? collectionId = null, CancellationToken ct = default);
    Task DeleteAsync(string documentId, CancellationToken ct = default);
}

// Phase 3 Implementation ✅
// - PostgresDocumentStore: EF Core with KnowledgeDbContext
//   - CRUD operations on documents table
//   - GUID validation for all IDs
//   - AsNoTracking for read queries
//   - Collection filtering in ListAsync
//   - Cascade deletes to chunks + vectors (DB-level)
```

### IDocumentParser (Phase 4 ✅)

```csharp
public interface IDocumentParser
{
    IReadOnlySet<string> SupportedExtensions { get; }
    Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken = default);
}

public record ParsedDocument(
    string Content,
    Dictionary<string, string> Metadata,
    List<string> Warnings);

// Phase 4 Implementations ✅
// - TextParser: .txt, .md, .csv, .json, .xml, .yaml — detects file type, counts lines, CSV delimiter detection
// - PdfParser: .pdf via PdfPig — extracts text + metadata (title, author, creator, creation date), page markers, handles scanned PDFs
// - OfficeParser: .docx, .pptx via OpenXML — extracts paragraphs, tables (Word), slides (PowerPoint), document properties
```

### IChunkingStrategy (Phase 4 ✅)

```csharp
public interface IChunkingStrategy
{
    string Name { get; }
    Task<IReadOnlyList<ChunkInfo>> ChunkAsync(
        ParsedDocument parsedDocument,
        ChunkingSettings settings,
        CancellationToken cancellationToken = default);
}

public record ChunkInfo(
    string Content,
    int ChunkIndex,
    int TokenCount,
    int StartOffset,
    int EndOffset,
    Dictionary<string, string> Metadata);

// Phase 4 Implementations ✅
// - FixedSizeChunker: Token-based with configurable overlap, natural boundary detection (newlines → sentences → spaces)
// - RecursiveChunker: Hierarchical splitting using configurable separators, preserves document structure
// - SemanticChunker: Embedding-based boundaries using cosine similarity, splits where similarity < threshold
```

### ISearchReranker (Phase 5 ✅)

```csharp
public interface ISearchReranker
{
    string Name { get; }
    Task<List<SearchHit>> RerankAsync(
        string query,
        List<SearchHit> hits,
        CancellationToken cancellationToken = default);
}

// Phase 5 Implementations ✅
// - RrfReranker (Name = "RRF"): Reciprocal Rank Fusion
//   - Formula: score = sum(1 / (k + rank)) across all lists
//   - Expects hits tagged with "source" metadata ("vector" or "keyword")
//   - Groups by source, builds ranked lists (1-indexed)
//   - Accumulates scores for duplicates across lists
//   - Normalizes final scores to 0-1 range
//   - Configured via SearchSettings.RrfK (default: 60)
//
// - CrossEncoderReranker (Name = "CrossEncoder"): LLM-based scoring
//   - Scores each (query, chunk) pair via Ollama POST /api/generate
//   - Prompt: "Rate relevance 0-10, respond with number only"
//   - Low temperature (0.1) for consistency
//   - Normalizes scores to 0-1 after all pairs scored
//   - Configured via SearchSettings.CrossEncoderModel (falls back to LlmSettings.Model)
//   - HttpClient injected via AddHttpClient<ISearchReranker, CrossEncoderReranker>()
//   - Fallback to original score on parse errors or API failures
```

### IReindexService (Phase 7 ✅)

```csharp
public interface IReindexService
{
    Task<ReindexResult> ReindexAsync(ReindexOptions options, CancellationToken ct = default);
    Task<ReindexCheck> CheckDocumentAsync(string documentId, CancellationToken ct = default);
}

public record ReindexOptions
{
    public string? CollectionId { get; init; }
    public IReadOnlyList<string>? DocumentIds { get; init; }
    public bool Force { get; init; } = false;
    public bool DetectSettingsChanges { get; init; } = true;
    public ChunkingStrategy? Strategy { get; init; }
}

public record ReindexResult
{
    public required string BatchId { get; init; }
    public int TotalDocuments { get; init; }
    public int EnqueuedCount { get; init; }
    public int SkippedCount { get; init; }
    public int FailedCount { get; init; }
    public IReadOnlyDictionary<ReindexReason, int> ReasonCounts { get; init; }
    public IReadOnlyList<ReindexDocumentResult> Documents { get; init; }
}

public record ReindexDocumentResult(
    string DocumentId, string FileName, ReindexAction Action, ReindexReason Reason,
    string? JobId = null, string? ErrorMessage = null);

public record ReindexCheck(
    string DocumentId, bool NeedsReindex, ReindexReason Reason,
    string? CurrentHash = null, string? StoredHash = null,
    string? CurrentChunkingStrategy = null, string? StoredChunkingStrategy = null,
    string? CurrentEmbeddingModel = null, string? StoredEmbeddingModel = null);

public enum ReindexAction { Enqueued, Skipped, Failed }
public enum ReindexReason {
    Unchanged, ContentChanged, ChunkingSettingsChanged, EmbeddingSettingsChanged,
    Forced, FileNotFound, NeverIndexed, Error
}

// Phase 7 Implementation ✅
// - ReindexService: compares SHA-256 content hashes, detects settings changes
// - Settings metadata keys stored in document during ingestion:
//   - IndexedWith:ChunkingStrategy, IndexedWith:ChunkingMaxSize, IndexedWith:ChunkingOverlap
//   - IndexedWith:EmbeddingProvider, IndexedWith:EmbeddingModel, IndexedWith:EmbeddingDimensions
// - Clears existing chunks/vectors before re-ingestion
// - Collection-scoped, document-filtered, or force reindex modes
```

### IIngestionQueue (Phase 4 ✅)

```csharp
public interface IIngestionQueue
{
    Task EnqueueAsync(IngestionJob job, CancellationToken cancellationToken = default);
    Task<IngestionJob?> DequeueAsync(CancellationToken cancellationToken = default);
    Task<IngestionJobStatus?> GetStatusAsync(string jobId);
    int QueueDepth { get; }
}

public record IngestionJob(
    string JobId,
    string DocumentId,
    string VirtualPath,
    IngestionOptions Options,
    string? BatchId = null);

public record IngestionJobStatus(
    string JobId,
    IngestionJobState State,
    IngestionPhase? CurrentPhase,
    double PercentComplete,
    string? ErrorMessage,
    DateTime? StartedAt,
    DateTime? CompletedAt);

public enum IngestionJobState { Queued, Processing, Completed, Failed }

// Phase 4 Implementation ✅
// - IngestionQueue: Channel-based (capacity: 1000), concurrent job status tracking (ConcurrentDictionary)
// - UpdateJobStatus, CleanupOldStatuses methods for status management
// - CompleteQueue() for graceful shutdown
```

### ISettingsStore (Phase 2 ✅)

```csharp
public interface ISettingsStore
{
    Task<T?> GetAsync<T>(string category, CancellationToken ct = default) where T : class;
    Task SaveAsync<T>(string category, T settings, CancellationToken ct = default) where T : class;
    Task ResetAsync(string category, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default);
}

// Implementation: PostgresSettingsStore
// - Stores settings as JSONB in `settings` table
// - JSON serialization for flexible schema
// - Integrated with IOptionsMonitor for live reload
```

### IConnectionTester (Connection Testing Feature ✅)

```csharp
public interface IConnectionTester
{
    Task<ConnectionTestResult> TestConnectionAsync(
        object settings,
        TimeSpan? timeout = null,
        CancellationToken ct = default);
}

public record ConnectionTestResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public Dictionary<string, object>? Details { get; init; }
    public TimeSpan? Duration { get; init; }

    public static ConnectionTestResult CreateSuccess(string message, Dictionary<string, object>? details = null, TimeSpan? duration = null);
    public static ConnectionTestResult CreateFailure(string message, Dictionary<string, object>? details = null, TimeSpan? duration = null);
}

// Implementations:
// - OllamaConnectionTester: Tests Ollama endpoints (Embedding & LLM settings)
//   - Calls GET /api/tags to list available models
//   - Returns model count and version info in details
//   - Default timeout: 10 seconds
//
// - MinioConnectionTester: Tests MinIO/S3 connectivity (Storage settings)
//   - Calls ListBucketsAsync() to validate credentials
//   - Checks bucket existence
//   - Returns bucket list and connection details
//   - Default timeout: 10 seconds
//
// Usage: Test connection before saving settings in UI
```

### IAgentTool

```csharp
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    JsonElement ParameterSchema { get; }
    Task<ToolResult> ExecuteAsync(JsonElement parameters, ToolContext context, CancellationToken ct = default);
}

public record ToolResult(bool Success, JsonElement? Data = null, string? Error = null, string? HumanReadable = null);
public record ToolContext(string? UserId, string? ConversationId, IServiceProvider Services);
```

### IAgentMemory

```csharp
public interface IAgentMemory
{
    Task SaveNoteAsync(string key, string content, NoteOptions? options = null, CancellationToken ct = default);
    Task<string?> GetNoteAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<Note>> SearchNotesAsync(string query, int topK = 5, CancellationToken ct = default);
    Task DeleteNoteAsync(string key, CancellationToken ct = default);
}

public record Note(string Key, string Content, DateTime Created, DateTime Modified, Dictionary<string, string> Metadata);
public record NoteOptions(string? Category = null, Dictionary<string, string>? Metadata = null, TimeSpan? Expiry = null);
```

### IKnowledgeFileSystem

```csharp
public interface IKnowledgeFileSystem
{
    string RootPath { get; }
    string ResolvePath(string virtualPath);
    Task EnsureDirectoryExistsAsync(string virtualPath, CancellationToken ct = default);
    Task<IReadOnlyList<FileSystemEntry>> ListAsync(string virtualPath = "/", CancellationToken ct = default);
    Task<bool> ExistsAsync(string virtualPath, CancellationToken ct = default);
    Task SaveFileAsync(string virtualPath, Stream content, CancellationToken ct = default);
    Task<Stream> OpenFileAsync(string virtualPath, CancellationToken ct = default);
    Task DeleteAsync(string virtualPath, CancellationToken ct = default);
}

public record FileSystemEntry(string Name, string VirtualPath, bool IsDirectory, long SizeBytes, DateTime LastModifiedUtc);

public class KnowledgeFileSystemOptions
{
    public const string SectionName = "Knowledge:FileSystem";
    public string RootPath { get; set; } = "knowledge-data";
}
```

### IFileStore

```csharp
public interface IFileStore
{
    Task<string> SaveAsync(Stream content, string fileName, string? contentType = null, CancellationToken ct = default);
    Task<Stream> GetAsync(string fileId, CancellationToken ct = default);
    Task DeleteAsync(string fileId, CancellationToken ct = default);
    Task<bool> ExistsAsync(string fileId, CancellationToken ct = default);
}
```

### IWebSearchProvider

```csharp
public interface IWebSearchProvider
{
    Task<WebSearchResult> SearchAsync(string query, WebSearchOptions? options = null, CancellationToken ct = default);
}
```

---

## Internal Search Services (Phase 5 ✅)

These services are not exposed as interfaces but are key components of the search system.

### VectorSearchService

```csharp
public class VectorSearchService
{
    Task<List<SearchHit>> SearchAsync(string query, SearchOptions options, CancellationToken ct = default);
}
```

- Registered as Scoped (not interface-based)
- Embeds query text using `IEmbeddingProvider.EmbedAsync()`
- Builds filters dict from `SearchOptions.CollectionId` + `SearchOptions.Filters`
- Calls `IVectorStore.SearchAsync()` with query vector, topK, and filters
- Converts `VectorSearchResult` → `SearchHit` (extracts metadata: documentId, content, fileName, etc.)
- Applies `MinScore` threshold
- Used by `HybridSearchService` for semantic search component

### KeywordSearchService

```csharp
public class KeywordSearchService
{
    Task<List<SearchHit>> SearchAsync(string query, SearchOptions options, CancellationToken ct = default);
}
```

- Registered as Scoped, depends on `KnowledgeDbContext`
- Sanitizes query: removes tsquery special chars (`&|!():<>*`), collapses spaces
- Uses raw SQL with `plainto_tsquery('english', query)` for FTS matching
- Uses `ts_rank(search_vector, query)` for relevance scoring
- JOINs chunks + documents for complete metadata
- Normalizes ts_rank scores to 0-1 range: `(rank - min) / (max - min)`
- Handles edge case where all ranks are identical (score = 1.0)
- Applies `MinScore` threshold after normalization
- Returns metadata including raw ts_rank value for debugging
- Used by `HybridSearchService` for keyword search component

### HybridSearchService

```csharp
public class HybridSearchService : IKnowledgeSearch
{
    // Implements IKnowledgeSearch interface
}
```

- Registered as `IKnowledgeSearch` (main search entry point)
- Depends on: `VectorSearchService`, `KeywordSearchService`, `IEnumerable<ISearchReranker>`
- Routes searches based on `SearchOptions.Mode`:
  - `Semantic` → VectorSearchService only
  - `Keyword` → KeywordSearchService only
  - `Hybrid` → both in parallel via `Task.WhenAll()`
- For Hybrid mode:
  - Tags vector results with `metadata["source"] = "vector"`
  - Tags keyword results with `metadata["source"] = "keyword"`
  - Merges both lists (includes duplicates for reranker)
- Applies reranking if `SearchSettings.Reranker != "None"`:
  - Finds reranker by name from injected `IEnumerable<ISearchReranker>`
  - Calls `reranker.RerankAsync()` with merged hits
- Final filtering: applies `MinScore` threshold, limits to `TopK`
- Reports `Duration` via `Stopwatch`
- `SearchStreamAsync` currently returns batch results (enhancement opportunity for true streaming)

---

## Settings Types (Phase 2 ✅)

All settings are records with `{ get; set; }` properties for form binding.

```csharp
public record EmbeddingSettings
{
    public string Provider { get; set; } = "Ollama";                     // Ollama | OpenAI | AzureOpenAI | Anthropic
    public string Model { get; set; } = "nomic-embed-text";
    public int Dimensions { get; set; } = 768;
    public string? BaseUrl { get; set; } = "http://localhost:11434";
    public string? ApiKey { get; set; }
    public string? AzureDeploymentName { get; set; }
    public int BatchSize { get; set; } = 32;
    public int TimeoutSeconds { get; set; } = 30;
}

public record ChunkingSettings
{
    public string Strategy { get; set; } = "Semantic";                   // FixedSize | Recursive | Semantic | DocumentAware
    public int MaxChunkSize { get; set; } = 512;
    public int Overlap { get; set; } = 50;
    public int MinChunkSize { get; set; } = 100;
    public double SemanticThreshold { get; set; } = 0.5;
    public string[] RecursiveSeparators { get; set; } = ["\n\n", "\n", ". ", " "];
    public bool RespectDocumentStructure { get; set; } = true;
}

public record SearchSettings
{
    public string Mode { get; set; } = "Hybrid";                         // Vector | Keyword | Hybrid
    public int TopK { get; set; } = 10;
    public string Reranker { get; set; } = "RRF";                        // None | RRF | CrossEncoder
    public int RrfK { get; set; } = 60;
    public double VectorWeight { get; set; } = 0.7;
    public double MinimumScore { get; set; } = 0.0;
    public string? CrossEncoderModel { get; set; }
    public bool EnableQueryExpansion { get; set; } = false;
    public bool IncludeWebSearch { get; set; } = false;
}

public record LlmSettings
{
    public string Provider { get; set; } = "Ollama";                     // Ollama | OpenAI | AzureOpenAI | Anthropic
    public string Model { get; set; } = "llama3.2";
    public string? BaseUrl { get; set; } = "http://localhost:11434";
    public string? ApiKey { get; set; }
    public string? AzureDeploymentName { get; set; }
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 2000;
    public int TimeoutSeconds { get; set; } = 60;
    public string? SystemPrompt { get; set; }
}

public record UploadSettings
{
    public int MaxFileSizeMb { get; set; } = 100;
    public string[] AllowedExtensions { get; set; } = [".txt", ".md", ".pdf", ".docx", ".pptx", ".csv"];
    public string DefaultPath { get; set; } = "/uploads";
    public int ParallelWorkers { get; set; } = 4;
    public bool EnableVirusScanning { get; set; } = false;
    public bool AutoStartIngestion { get; set; } = true;
    public int BatchSize { get; set; } = 100;
}

public record WebSearchSettings
{
    public string Provider { get; set; } = "None";                       // None | Brave | Serper | Tavily
    public string? ApiKey { get; set; }
    public int MaxResults { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 10;
    public bool SafeSearch { get; set; } = true;
    public string? Region { get; set; }
}

public record StorageSettings
{
    public string VectorStoreProvider { get; set; } = "PgVector";        // SqliteVec | PgVector | Qdrant | Pinecone | AzureAISearch
    public string DocumentStoreProvider { get; set; } = "Postgres";      // Postgres | MongoDB
    public string FileStorageProvider { get; set; } = "MinIO";           // Local | MinIO | AzureBlob | S3
    public string? MinioEndpoint { get; set; } = "localhost:9000";
    public string? MinioAccessKey { get; set; }
    public string? MinioSecretKey { get; set; }
    public string MinioBucketName { get; set; } = "aikp-files";
    public bool MinioUseSSL { get; set; } = false;
    public string? LocalStorageRootPath { get; set; } = "knowledge-data";
    public string? AzureBlobConnectionString { get; set; }
    public string? AzureBlobContainerName { get; set; }
}
```

**Settings Configuration Categories:**
- `Knowledge:Embedding` → EmbeddingSettings
- `Knowledge:Chunking` → ChunkingSettings
- `Knowledge:Search` → SearchSettings
- `Knowledge:LLM` → LlmSettings
- `Knowledge:Upload` → UploadSettings
- `Knowledge:WebSearch` → WebSearchSettings
- `Knowledge:Storage` → StorageSettings

**Live Reload:**
- Settings saved via `ISettingsStore.SaveAsync()` → stored in DB → triggers `ISettingsReloader.Reload()` → `IOptionsMonitor<T>.CurrentValue` updates without app restart

---

## REST API Endpoints (Phase 6 ✅)

### Documents

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/documents` | Upload files (multipart/form-data). FormData: `files` (IFormFileCollection), `collectionId?`, `destinationPath?`, `strategy?`. Streams to MinIO, enqueues ingestion. Returns `UploadResponse` with batch ID, document IDs, job IDs. |
| `GET` | `/api/documents` | List documents. Query params: `collectionId?`. Returns array of `Document` objects. |
| `GET` | `/api/documents/{id}` | Get single document metadata by ID. Returns `Document` or 404. |
| `DELETE` | `/api/documents/{id}` | Delete document + associated chunks + vectors (cascade). Also deletes file from storage. Returns 204 No Content. |
| `POST` | `/api/documents/reindex` | Trigger reindex with content-hash comparison and settings-change detection. Body: `ReindexRequest`. Returns `{ batchId, totalDocuments, enqueuedCount, skippedCount, failedCount, reasonCounts, message }`. |
| `GET` | `/api/documents/{id}/reindex-check` | Check if a specific document needs reindexing. Returns hash comparison and settings comparison details for debugging. |

**Request/Response DTOs:**
```csharp
public record UploadResponse(string? BatchId, List<UploadedDocumentResponse> Documents, int TotalCount, int SuccessCount);
public record UploadedDocumentResponse(string DocumentId, string? JobId, string FileName, long SizeBytes, string VirtualPath, string? Error = null);
public record ReindexRequest(
    string? CollectionId = null,
    IReadOnlyList<string>? DocumentIds = null,
    bool? Force = null,
    bool? DetectSettingsChanges = null,
    ChunkingStrategy? Strategy = null);
```

### Search

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/search` | Simple search. Query params: `q` (required), `mode?` (Semantic/Keyword/Hybrid, default: Hybrid), `topK?` (default: 10), `collectionId?`. Returns `SearchResult`. |
| `POST` | `/api/search` | Search with complex filters. Body: `SearchRequest` with `Query`, `Mode?`, `TopK?`, `CollectionId?`, `Filters?`. Returns `SearchResult`. |

**Request/Response DTOs:**
```csharp
public record SearchRequest(string Query, SearchMode? Mode = null, int? TopK = null, string? CollectionId = null, Dictionary<string, string>? Filters = null);
// SearchResult defined in IKnowledgeSearch (see above)
```

### Batches

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/batches/{id}/status` | Get batch ingestion progress. Returns `IngestionJobStatus` or 404 if batch not found. |

### Settings (Connection Testing Feature ✅)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/settings/{category}` | Get current settings for a category. Categories: `embedding`, `chunking`, `search`, `llm`, `upload`, `websearch`, `storage`. Returns settings record (EmbeddingSettings, ChunkingSettings, etc.). |
| `PUT` | `/api/settings/{category}` | Update settings for a category. Body: JSON settings object. Saves to database and triggers IOptionsMonitor live reload. Returns `{ success: true, message: string }`. |
| `POST` | `/api/settings/test-connection` | Test connectivity to external services before saving settings. Body: `TestConnectionRequest`. Returns `ConnectionTestResult` with success status, message, and details. |

**Request/Response DTOs:**
```csharp
public record TestConnectionRequest(
    string Category,              // "embedding", "llm", or "storage"
    JsonElement Settings,         // Current form values (EmbeddingSettings, LlmSettings, or StorageSettings)
    int? TimeoutSeconds = null);  // Optional timeout (default: 10)

// ConnectionTestResult defined in IConnectionTester (see above)
```

**Supported Categories for Test Connection:**
- `embedding` → Tests Ollama endpoint via GET /api/tags
- `llm` → Tests Ollama endpoint via GET /api/tags
- `storage` → Tests MinIO S3 connectivity via ListBucketsAsync()

**Example Success Response:**
```json
{
  "success": true,
  "message": "Connected to Ollama at http://localhost:11434 (3 models available)",
  "details": {
    "baseUrl": "http://localhost:11434",
    "modelCount": 3,
    "models": ["nomic-embed-text", "llama3.2", "llama3.2:1b"]
  },
  "duration": "00:00:01.234"
}
```

**Example Failure Response:**
```json
{
  "success": false,
  "message": "Connection failed: Connection refused",
  "details": {
    "error": "Connection refused",
    "errorType": "HttpRequestException"
  },
  "duration": "00:00:05.001"
}
```

### SignalR Hub

| Hub | Path | Methods |
|-----|------|---------|
| `IngestionHub` | `/hubs/ingestion` | `SubscribeToJob(string jobId)` - Join group for job updates<br>`UnsubscribeFromJob(string jobId)` - Leave group |

**Server-to-client events:**
- `IngestionProgress` - Sends job status update: `{ jobId, state, currentPhase?, percentComplete, errorMessage?, startedAt?, completedAt? }`
- Broadcast by `IngestionProgressBroadcaster` every 500ms for active jobs

---

## CLI Commands (Phase 6 ✅)

Binary: `aikp` (AIKnowledge.CLI project)

**Configuration:**
- Reads `ApiBaseUrl` from appsettings.json or env var (default: `https://localhost:5001`)
- SSL validation bypassed for localhost in development

### Commands

#### ingest
```bash
aikp ingest <path> [--collection <id>] [--strategy <name>] [--destination <path>]
```

Uploads file(s) to knowledge base via `POST /api/documents`.

**Arguments:**
- `path` (required): File or directory path

**Options:**
- `--collection <id>`: Collection ID for organization
- `--strategy <name>`: Chunking strategy (Semantic, FixedSize, Recursive) [default: Semantic]
- `--destination <path>`: Virtual destination path in knowledge base [default: uploads]

**Example:**
```bash
aikp ingest ./docs --collection research --strategy Semantic
```

#### search
```bash
aikp search "<query>" [--mode <mode>] [--top <n>] [--collection <id>]
```

Searches knowledge base via `GET /api/search`, displays formatted results.

**Arguments:**
- `query` (required): Search query text (quote if contains spaces)

**Options:**
- `--mode <mode>`: Search mode (Semantic, Keyword, Hybrid) [default: Hybrid]
- `--top <n>`: Number of results to return [default: 10]
- `--collection <id>`: Filter by collection ID

**Example:**
```bash
aikp search "machine learning best practices" --mode Hybrid --top 5
```

#### reindex
```bash
aikp reindex [--collection <id>] [--force] [--no-detect-changes]
```

Triggers reindexing via `POST /api/documents/reindex` with content-hash comparison.

**Options:**
- `--collection <id>`: Reindex only documents in this collection
- `--force`: Skip content-hash comparison, reindex all documents
- `--no-detect-changes`: Disable chunking/embedding settings change detection

**Example:**
```bash
aikp reindex --collection research
aikp reindex --force                    # Reindex everything
aikp reindex --no-detect-changes        # Only reindex if content hash changed
```

**Output:** Reports total documents evaluated, enqueued, skipped (unchanged), failed, with breakdown by reason (ContentChanged, ChunkingSettingsChanged, EmbeddingSettingsChanged, Forced, etc.)

---

## MCP Server (Phase 6 ✅)

**Protocol**: JSON-RPC 2.0
**Endpoint**: `POST /mcp`
**Convenience**: `GET /mcp/tools` (list available tools)

### RPC Methods

#### tools/list
Returns array of available tools with JSON Schema for parameters.

**Request:**
```json
{
  "jsonrpc": "2.0",
  "method": "tools/list",
  "id": "1"
}
```

**Response:**
```json
{
  "jsonrpc": "2.0",
  "result": {
    "tools": [ /* array of McpTool */ ]
  },
  "id": "1"
}
```

#### tools/call
Executes a tool and returns result.

**Request:**
```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "search_knowledge",
    "arguments": {
      "query": "test",
      "mode": "Hybrid",
      "topK": 5
    }
  },
  "id": "2"
}
```

**Response:**
```json
{
  "jsonrpc": "2.0",
  "result": {
    "content": [
      { "type": "text", "text": "Found 3 results..." }
    ],
    "isError": false
  },
  "id": "2"
}
```

### Available Tools

#### search_knowledge

Search the knowledge base using semantic, keyword, or hybrid search.

**Parameters:**
- `query` (string, required): Search query text
- `mode` (string, optional): "Semantic", "Keyword", or "Hybrid" [default: Hybrid]
- `topK` (number, optional): Number of results [default: 10]
- `collectionId` (string, optional): Filter by collection

**Returns:** Text with formatted search results including scores, content, file names, and sources.

#### list_documents

List all documents in the knowledge base.

**Parameters:**
- `collectionId` (string, optional): Filter by collection ID

**Returns:** Text with formatted document list including ID, file name, size, created date, collection.

#### ingest_document

Add a document to the knowledge base.

**Parameters:**
- `path` (string, required): Virtual path for document (e.g., "/documents/report.pdf")
- `content` (string, required): Base64-encoded document content
- `fileName` (string, required): Original file name with extension
- `collectionId` (string, optional): Collection ID for organization
- `strategy` (string, optional): "Semantic", "FixedSize", or "Recursive" [default: Semantic]

**Returns:** Text with document ID, job ID, and confirmation that ingestion is queued.

**Implementation:**
- `McpServer` class in `AIKnowledge.Web.Mcp`
- Depends on: `IKnowledgeSearch`, `IDocumentStore`, `IKnowledgeFileSystem`, `IIngestionQueue`
- Registered as singleton in DI container
- Full error handling with JSON-RPC 2.0 error responses

---

## Breaking Changes

### 2026-02-04 — Storage Backend: SQLite → PostgreSQL + MinIO

**Change**: Default storage backend changed from SQLite + sqlite-vec + local filesystem to PostgreSQL + pgvector + MinIO. `IVectorStore`, `IDocumentStore`, and `IKnowledgeFileSystem` implementations now target Postgres and S3 respectively.

**Migration**: No data migration needed (project is pre-release). New implementations: `PgVectorStore`, `PostgresDocumentStore`, `MinioFileSystem`. Old `LocalKnowledgeFileSystem` remains available for non-Docker development.

---

<!-- Log breaking changes with migration guidance -->
