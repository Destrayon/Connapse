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

### IDocumentParser (new)

```csharp
public interface IDocumentParser
{
    bool CanParse(string fileName, string? contentType);
    Task<ParsedDocument> ParseAsync(Stream content, string fileName, CancellationToken ct = default);
}

public record ParsedDocument(
    string Text,
    string FileName,
    string? Title,
    Dictionary<string, string> Metadata);  // extracted metadata (author, page count, etc.)
```

### IChunkingStrategy (new)

```csharp
public interface IChunkingStrategy
{
    ChunkingStrategyType Type { get; }
    Task<IReadOnlyList<Chunk>> ChunkAsync(ParsedDocument document, ChunkingSettings settings, CancellationToken ct = default);
}

public enum ChunkingStrategyType { FixedSize, Recursive, Semantic, DocumentAware }
```

### ISearchReranker (new)

```csharp
public interface ISearchReranker
{
    RerankStrategy Strategy { get; }
    Task<List<SearchHit>> RerankAsync(string query, List<SearchHit> hits, CancellationToken ct = default);
}

public enum RerankStrategy { None, Rrf, CrossEncoder }
```

### IIngestionQueue (new)

```csharp
public interface IIngestionQueue
{
    Task<string> EnqueueAsync(IngestionJob job, CancellationToken ct = default);
    Task<IngestionJob?> DequeueAsync(CancellationToken ct = default);
    Task<BatchStatus> GetBatchStatusAsync(string batchId, CancellationToken ct = default);
}

public record IngestionJob(
    string DocumentId,
    string BatchId,
    string VirtualPath,
    IngestionOptions Options);

public record BatchStatus(
    string BatchId,
    int TotalFiles,
    int Completed,
    int Failed,
    int Processing,
    int Pending,
    string Status);   // Processing | Completed | PartialFailure
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
- Settings saved via `ISettingsStore.SaveAsync()` → stored in DB → triggers `SettingsReloadService.ReloadSettings()` → `IOptionsMonitor<T>.CurrentValue` updates without app restart

---

## REST API Endpoints (new)

### Documents

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/documents` | Upload files (multipart/form-data). Streams to MinIO, enqueues ingestion. Returns batch ID + document IDs. |
| `GET` | `/api/documents` | List documents. Query params: `collection`, `status`, `page`, `pageSize`. |
| `GET` | `/api/documents/{id}` | Get single document metadata. |
| `DELETE` | `/api/documents/{id}` | Delete document + associated chunks + vectors. |
| `POST` | `/api/documents/reindex` | Trigger reindex. Body: `{ collectionId?, force? }`. |

### Search

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/search` | Simple search. Query params: `q`, `mode`, `topK`, `minScore`, `collection`. |
| `POST` | `/api/search` | Search with complex filters. JSON body with full `SearchOptions`. |

### Batches

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/batches/{id}/status` | Get batch ingestion progress. |

### Settings

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/settings` | List all setting categories. |
| `GET` | `/api/settings/{category}` | Get settings for a category. |
| `PUT` | `/api/settings/{category}` | Update settings for a category. Triggers live reload. |
| `DELETE` | `/api/settings/{category}` | Reset category to defaults (deletes DB override). |

---

## MCP Tools (new)

| Tool Name | Description | Parameters |
|-----------|-------------|------------|
| `search_knowledge` | Search the knowledge base | `query` (string), `mode?` (Semantic/Keyword/Hybrid), `topK?` (int), `collection?` (string) |
| `ingest_document` | Upload and ingest a document | `filePath` (string), `collection?` (string), `strategy?` (ChunkingStrategy) |
| `list_documents` | List indexed documents | `collection?` (string), `status?` (string) |

---

## Breaking Changes

### 2026-02-04 — Storage Backend: SQLite → PostgreSQL + MinIO

**Change**: Default storage backend changed from SQLite + sqlite-vec + local filesystem to PostgreSQL + pgvector + MinIO. `IVectorStore`, `IDocumentStore`, and `IKnowledgeFileSystem` implementations now target Postgres and S3 respectively.

**Migration**: No data migration needed (project is pre-release). New implementations: `PgVectorStore`, `PostgresDocumentStore`, `MinioFileSystem`. Old `LocalKnowledgeFileSystem` remains available for non-Docker development.

---

<!-- Log breaking changes with migration guidance -->
