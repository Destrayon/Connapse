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

### ISettingsStore (new)

```csharp
public interface ISettingsStore
{
    Task<T> GetAsync<T>(string category, CancellationToken ct = default) where T : class, new();
    Task SaveAsync<T>(string category, T settings, CancellationToken ct = default) where T : class;
    Task ResetAsync(string category, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default);
}
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

## Settings Types (new)

```csharp
public class EmbeddingSettings
{
    public const string Category = "Embedding";
    public string Provider { get; set; } = "Ollama";       // Ollama | OpenAI | AzureOpenAI
    public string Model { get; set; } = "nomic-embed-text";
    public int Dimensions { get; set; } = 768;
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string? ApiKey { get; set; }
}

public class ChunkingSettings
{
    public const string Category = "Chunking";
    public ChunkingStrategyType Strategy { get; set; } = ChunkingStrategyType.Recursive;
    public int MaxChunkSize { get; set; } = 512;        // tokens
    public int Overlap { get; set; } = 50;               // tokens
    public float SemanticThreshold { get; set; } = 0.5f; // for Semantic strategy
}

public class SearchSettings
{
    public const string Category = "Search";
    public SearchMode DefaultMode { get; set; } = SearchMode.Hybrid;
    public int DefaultTopK { get; set; } = 10;
    public float DefaultMinScore { get; set; } = 0.7f;
    public RerankStrategy RerankStrategy { get; set; } = RerankStrategy.Rrf;
    public int RrfK { get; set; } = 60;
    public string? CrossEncoderModel { get; set; }
}

public class LlmSettings
{
    public const string Category = "LLM";
    public string Provider { get; set; } = "Ollama";      // Ollama | OpenAI | AzureOpenAI | Anthropic
    public string Model { get; set; } = "llama3.2";
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string? ApiKey { get; set; }
    public float Temperature { get; set; } = 0.7f;
    public int MaxTokens { get; set; } = 2048;
}

public class UploadSettings
{
    public const string Category = "Uploads";
    public int MaxFileSizeMb { get; set; } = 100;
    public int MaxBatchSize { get; set; } = 200;
    public string AllowedExtensions { get; set; } = ".txt,.md,.csv,.pdf,.docx,.pptx,.json,.xml,.html";
    public bool AutoIngestOnUpload { get; set; } = true;
}

public class WebSearchSettings
{
    public const string Category = "WebSearch";
    public string Provider { get; set; } = "None";        // Brave | Serper | Tavily | None
    public string? ApiKey { get; set; }
    public int MaxResults { get; set; } = 10;
}

public class StorageSettings
{
    public const string Category = "Storage";
    public string MinioEndpoint { get; set; } = "localhost:9000";
    public string MinioBucket { get; set; } = "aikp-documents";
    public string? MinioAccessKey { get; set; }
    public string? MinioSecretKey { get; set; }
    public bool MinioUseSsl { get; set; } = false;
}
```

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
