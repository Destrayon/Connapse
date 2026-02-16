# API Surface

Public interfaces and contracts. Track breaking changes here.

---

## Core Interfaces

### IContainerStore

```csharp
public interface IContainerStore
{
    Task<Container> CreateAsync(CreateContainerRequest request, CancellationToken ct = default);
    Task<Container?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Container?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Container>> ListAsync(CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default); // Fails if not empty
    Task<bool> ExistsAsync(Guid id, CancellationToken ct = default);
}

public record Container(
    string Id,
    string Name,
    string? Description,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int DocumentCount = 0);

public record CreateContainerRequest(string Name, string? Description = null);
```

Implementation: `PostgresContainerStore`

### IFolderStore

```csharp
public interface IFolderStore
{
    Task<Folder> CreateAsync(Guid containerId, string path, CancellationToken ct = default);
    Task<IReadOnlyList<Folder>> ListAsync(Guid containerId, string? parentPath = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid containerId, string path, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid containerId, string path, CancellationToken ct = default);
}

public record Folder(string Id, string ContainerId, string Path, DateTime CreatedAt);
```

Implementation: `PostgresFolderStore` — cascade deletes nested docs/subfolders, lists immediate children only.

---

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
    string? ContainerId = null,
    string? Path = null,
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
    float MinScore = 0.0f,
    string? ContainerId = null,
    SearchMode Mode = SearchMode.Hybrid,
    Dictionary<string, string>? Filters = null);

public record SearchResult(List<SearchHit> Hits, int TotalMatches, TimeSpan Duration);
public record SearchHit(string ChunkId, string DocumentId, string Content, float Score, Dictionary<string, string> Metadata);
public enum SearchMode { Semantic, Keyword, Hybrid }
```

Note: `MinScore` default is 0.0 at the options level. Endpoints apply the effective default from `SearchSettings.MinimumScore` (default 0.5). Caller-provided minScore overrides the settings value.

### IEmbeddingProvider

```csharp
public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default);
    int Dimensions { get; }
    string ModelId { get; }
}

// Implementation: OllamaEmbeddingProvider
// - HttpClient with typed client pattern
// - Batch processing (sends multiple parallel requests)
// - Dimension validation with warnings
// - Configurable timeout (default: 30s)
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

// Implementation: PgVectorStore
// - Raw SQL with NAMED NpgsqlParameter objects (CRITICAL: positional params silently drop Vector type)
// - Cosine distance -> similarity conversion (1 - distance)
// - Filters: containerId, documentId, pathPrefix via WHERE clauses
// - JOINs chunks + documents for complete result metadata
// - Quoted column aliases in SQL ("ChunkId", "Distance", etc.)
```

### IDocumentStore

```csharp
public interface IDocumentStore
{
    Task<string> StoreAsync(Document document, CancellationToken ct = default);
    Task<Document?> GetAsync(string documentId, CancellationToken ct = default);
    Task<IReadOnlyList<Document>> ListAsync(Guid containerId, string? pathPrefix = null, CancellationToken ct = default);
    Task DeleteAsync(string documentId, CancellationToken ct = default);
    Task<bool> ExistsByPathAsync(Guid containerId, string path, CancellationToken ct = default);
}

public record Document(
    string Id,
    string ContainerId,
    string FileName,
    string? ContentType,
    string Path,
    long SizeBytes,
    DateTime CreatedAt,
    Dictionary<string, string> Metadata);

// Implementation: PostgresDocumentStore
// - ListAsync filters by containerId (required) and optional pathPrefix
// - ExistsByPathAsync checks for duplicate files at same path
// - Metadata includes Status, ContentHash, ChunkCount
```

### IDocumentParser

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

// Implementations:
// - TextParser: .txt, .md, .csv, .json, .xml, .yaml
// - PdfParser: .pdf via PdfPig
// - OfficeParser: .docx, .pptx via OpenXML
```

### IChunkingStrategy

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

// Implementations: FixedSizeChunker, RecursiveChunker, SemanticChunker
```

### ISearchReranker

```csharp
public interface ISearchReranker
{
    string Name { get; }
    Task<List<SearchHit>> RerankAsync(
        string query,
        List<SearchHit> hits,
        CancellationToken cancellationToken = default);
}

// Implementations: RrfReranker ("RRF"), CrossEncoderReranker ("CrossEncoder")
```

### IReindexService

```csharp
public interface IReindexService
{
    Task<ReindexResult> ReindexAsync(ReindexOptions options, CancellationToken ct = default);
    Task<ReindexCheck> CheckDocumentAsync(string documentId, CancellationToken ct = default);
}

public record ReindexOptions
{
    public string? ContainerId { get; init; }        // Filter to container (was CollectionId)
    public IReadOnlyList<string>? DocumentIds { get; init; }
    public bool Force { get; init; } = false;
    public bool DetectSettingsChanges { get; init; } = true;
    public ChunkingStrategy? Strategy { get; init; }
}

public record ReindexResult { /* BatchId, TotalDocuments, EnqueuedCount, SkippedCount, FailedCount, ReasonCounts, Documents */ }
public record ReindexDocumentResult(string DocumentId, string FileName, ReindexAction Action, ReindexReason Reason, string? JobId = null, string? ErrorMessage = null);
public record ReindexCheck(string DocumentId, bool NeedsReindex, ReindexReason Reason, ...);
public enum ReindexAction { Enqueued, Skipped, Failed }
public enum ReindexReason { Unchanged, ContentChanged, ChunkingSettingsChanged, EmbeddingSettingsChanged, Forced, FileNotFound, NeverIndexed, Error }
```

### IIngestionQueue

```csharp
public interface IIngestionQueue
{
    Task EnqueueAsync(IngestionJob job, CancellationToken cancellationToken = default);
    Task<IngestionJob?> DequeueAsync(CancellationToken cancellationToken = default);
    Task<IngestionJobStatus?> GetStatusAsync(string jobId);
    Task<bool> CancelJobForDocumentAsync(string documentId);  // NEW: cancel in-progress jobs
    int QueueDepth { get; }
}

public record IngestionJob(
    string JobId,
    string DocumentId,
    string Path,              // Was VirtualPath
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

// Implementation: IngestionQueue (Channel-based, capacity: 1000)
// - Tracks job cancellation tokens per job
// - Maps document IDs to job IDs for cancellation
```

### ISettingsStore

```csharp
public interface ISettingsStore
{
    Task<T?> GetAsync<T>(string category, CancellationToken ct = default) where T : class;
    Task SaveAsync<T>(string category, T settings, CancellationToken ct = default) where T : class;
    Task ResetAsync(string category, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default);
}

// Implementation: PostgresSettingsStore (JSONB via JsonDocument)
```

### IConnectionTester

```csharp
public interface IConnectionTester
{
    Task<ConnectionTestResult> TestConnectionAsync(object settings, TimeSpan? timeout = null, CancellationToken ct = default);
}

// Implementations: OllamaConnectionTester, MinioConnectionTester
```

### IAgentTool / IAgentMemory / IKnowledgeFileSystem / IFileStore / IWebSearchProvider

These interfaces remain unchanged from the initial design. See CLAUDE.md for definitions.

---

## Settings Types

All settings are records with `{ get; set; }` properties for form binding.

```csharp
public record EmbeddingSettings { Provider, Model, Dimensions, BaseUrl, ApiKey, AzureDeploymentName, BatchSize, TimeoutSeconds }
public record ChunkingSettings  { Strategy, MaxChunkSize, Overlap, MinChunkSize, SemanticThreshold, RecursiveSeparators, RespectDocumentStructure }
public record SearchSettings    { Mode, TopK, Reranker, RrfK, VectorWeight, MinimumScore (default 0.5), CrossEncoderModel, EnableQueryExpansion, IncludeWebSearch }
public record LlmSettings       { Provider, Model, BaseUrl, ApiKey, AzureDeploymentName, Temperature, MaxTokens, TimeoutSeconds, SystemPrompt }
public record UploadSettings    { MaxFileSizeMb, AllowedExtensions, DefaultPath, ParallelWorkers, EnableVirusScanning, AutoStartIngestion, BatchSize }
public record WebSearchSettings { Provider, ApiKey, MaxResults, TimeoutSeconds, SafeSearch, Region }
public record StorageSettings   { VectorStoreProvider, DocumentStoreProvider, FileStorageProvider, MinioEndpoint, MinioAccessKey, MinioSecretKey, MinioBucketName, MinioUseSSL, ... }
```

**Settings Configuration Categories:**
- `Knowledge:Embedding` -> EmbeddingSettings
- `Knowledge:Chunking` -> ChunkingSettings
- `Knowledge:Search` -> SearchSettings
- `Knowledge:LLM` -> LlmSettings
- `Knowledge:Upload` -> UploadSettings
- `Knowledge:WebSearch` -> WebSearchSettings
- `Knowledge:Storage` -> StorageSettings

---

## REST API Endpoints

### Containers

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/containers` | Create container. Body: `{ name, description? }`. Returns `Container`. |
| `GET` | `/api/containers` | List all containers. Returns array of `Container`. |
| `GET` | `/api/containers/{id}` | Get container details. Returns `Container` or 404. |
| `DELETE` | `/api/containers/{id}` | Delete container. Fails with 400 if not empty. Returns 204. |

### Container Files

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/containers/{id}/files` | Upload files. Multipart form-data: `files`, `path?`, `strategy?`. Returns `UploadResponse`. |
| `GET` | `/api/containers/{id}/files` | List files/folders at path. Query: `?path=/folder/`. Returns array of entries. |
| `GET` | `/api/containers/{id}/files/{fileId}` | Get file details including indexing status. |
| `GET` | `/api/containers/{id}/files/{fileId}/reindex-check` | Check if file needs reindex. |
| `DELETE` | `/api/containers/{id}/files/{fileId}` | Delete file + chunks (cascade). Returns 204. |

### Container Folders

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/containers/{id}/folders` | Create empty folder. Body: `{ path }`. Returns `Folder`. |
| `DELETE` | `/api/containers/{id}/folders` | Delete folder. Query: `?path=/folder/`. Cascade deletes nested files/folders. Returns 204. |

### Container Search

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/containers/{id}/search` | Search within container. Query: `?q=...&path=/folder/&mode=Hybrid&topK=10&minScore=0.5`. |
| `POST` | `/api/containers/{id}/search` | Search with complex filters. Body: `ContainerSearchRequest`. |

### Container Reindex

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/containers/{id}/reindex` | Reindex documents in container. Body: `{ force?, detectSettingsChanges? }`. |

### Settings

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/settings/{category}` | Get settings for category. |
| `PUT` | `/api/settings/{category}` | Update settings. Triggers live reload. |
| `POST` | `/api/settings/test-connection` | Test connectivity before saving. |

### Batches

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/batches/{id}/status` | Get batch ingestion progress. |

### SignalR Hub

| Hub | Path | Methods |
|-----|------|---------|
| `IngestionHub` | `/hubs/ingestion` | `SubscribeToJob(jobId)`, `UnsubscribeFromJob(jobId)` |

**Server-to-client events:**
- `IngestionProgress` - Sends `IngestionProgressUpdate` DTO every 500ms for active jobs

### Legacy Endpoints (REMOVED)

These endpoints from Feature #1 have been replaced by container-scoped versions:
- `POST /api/documents` -> `POST /api/containers/{id}/files`
- `GET /api/documents` -> `GET /api/containers/{id}/files`
- `DELETE /api/documents/{id}` -> `DELETE /api/containers/{id}/files/{id}`
- `GET /api/search` -> `GET /api/containers/{id}/search`
- `POST /api/search` -> `POST /api/containers/{id}/search`
- `POST /api/documents/reindex` -> `POST /api/containers/{id}/reindex`

---

## CLI Commands

Binary: `aikp` (AIKnowledge.CLI project)

```bash
# Container management
aikp container create <name> [--description "..."]
aikp container list
aikp container delete <name>

# Upload files to container
aikp upload <path> --container <name> [--destination /folder/] [--strategy Semantic]

# Search within container
aikp search "<query>" --container <name> [--mode Hybrid] [--top 10] [--path /folder/] [--min-score 0.5]

# Reindex container
aikp reindex --container <name> [--force] [--no-detect-changes]
```

---

## MCP Server

**Protocol**: JSON-RPC 2.0
**Endpoint**: `POST /mcp`
**Convenience**: `GET /mcp/tools`

### Available Tools

| Tool | Parameters | Description |
|------|-----------|-------------|
| `container_create` | `name` (required), `description?` | Create a new container |
| `container_list` | (none) | List all containers with document counts |
| `container_delete` | `name` (required) | Delete an empty container |
| `upload_file` | `containerId` (required), `fileName` (required), `content` (required, base64), `path?`, `strategy?` | Upload file to container |
| `list_files` | `containerId` (required), `path?` | List files/folders in container |
| `delete_file` | `containerId` (required), `fileId` (required) | Delete file from container |
| `search_knowledge` | `query` (required), `containerId` (required), `path?`, `mode?`, `topK?`, `minScore?` | Search within container |

All tools accept container name or ID for `containerId` (resolved via name lookup).

---

## Breaking Changes

### 2026-02-06 -- Container-Based File Browser (Feature #2)

**Change**: Documents now require a container. All file operations, search, and reindex endpoints moved under `/api/containers/{id}/...`. `CollectionId` removed entirely. `VirtualPath` renamed to `Path`.

**Migration**:
- No user data to migrate (pre-release)
- All API consumers must use container-scoped endpoints
- CLI commands require `--container` flag
- MCP tools require `containerId` parameter
- `IngestionJob.VirtualPath` -> `IngestionJob.Path`
- `IngestionOptions.CollectionId` -> `IngestionOptions.ContainerId`
- `SearchOptions.CollectionId` -> `SearchOptions.ContainerId`
- `ReindexOptions.CollectionId` -> `ReindexOptions.ContainerId`
- `SearchOptions.MinScore` default changed from 0.7 to 0.0 (endpoints control effective value)

### 2026-02-04 -- Storage Backend: SQLite -> PostgreSQL + MinIO

**Change**: Default storage backend changed from SQLite to PostgreSQL + pgvector + MinIO.

---

<!-- Log breaking changes with migration guidance -->
