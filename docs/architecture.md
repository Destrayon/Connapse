# Architecture

Connapse is a .NET 10 Blazor WebApp that transforms uploaded documents into searchable knowledge for AI agents. This document describes the system architecture, design patterns, and data flow.

## System Overview

```
┌─────────────┐
│   Clients   │  Blazor UI, CLI, MCP Servers, REST API
└──────┬──────┘
       │
┌──────┴──────────────────────────────────────────────┐
│              Connapse.Web                         │
│  ┌────────────────┐      ┌──────────────────┐      │
│  │ Blazor Pages   │      │  API Endpoints   │      │
│  │  - Upload      │      │  - Documents     │      │
│  │  - Search      │      │  - Search        │      │
│  │  - Settings    │      │  - Settings      │      │
│  └────────┬───────┘      └────────┬─────────┘      │
│           │                       │                 │
│           └───────────┬───────────┘                 │
│                       │                             │
│           ┌───────────┴──────────┐                  │
│           │   Service Layer      │                  │
│           │  - IngestionQueue    │                  │
│           │  - ReindexService    │                  │
│           │  - ProgressBroadcast │                  │
│           └──────────────────────┘                  │
└──────────────────┬──────────────────────────────────┘
                   │
┌──────────────────┴──────────────────────────────────┐
│           Core Domain Services                       │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────┐ │
│  │  Ingestion   │  │   Search     │  │  Storage  │ │
│  │  Pipeline    │  │   Service    │  │  Layer    │ │
│  └──────┬───────┘  └──────┬───────┘  └─────┬─────┘ │
│         │                 │                 │       │
│    Parse → Chunk    Vector + Keyword    Postgres   │
│         → Embed          → RRF Fusion    + MinIO   │
│         → Store                                     │
└─────────────────────────────────────────────────────┘
                   │
┌──────────────────┴──────────────────────────────────┐
│             Infrastructure                           │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────┐ │
│  │  PostgreSQL  │  │    MinIO     │  │  Ollama   │ │
│  │  + pgvector  │  │  (S3 API)    │  │ (optional)│ │
│  │              │  │              │  │           │ │
│  │ - documents  │  │ - original   │  │ - embed   │ │
│  │ - chunks     │  │   files      │  │ - llm     │ │
│  │ - vectors    │  │              │  │           │ │
│  │ - FTS index  │  │              │  │           │ │
│  │ - settings   │  │              │  │           │ │
│  └──────────────┘  └──────────────┘  └───────────┘ │
└─────────────────────────────────────────────────────┘
```

## Project Structure

```
src/
├── Connapse.Web/          # Blazor UI + API endpoints + hosting
├── Connapse.Core/         # Domain models, interfaces, shared types
├── Connapse.Ingestion/    # Document parsing, chunking, pipeline orchestration
├── Connapse.Search/       # Vector search, keyword search, hybrid fusion
├── Connapse.Storage/      # Database, vector store, object storage, settings
├── Connapse.Agents/       # (Planned) Agent orchestration, tools, memory
└── Connapse.CLI/          # (Planned) Command-line interface

tests/
├── Connapse.Core.Tests/           # Unit tests (parsers, chunkers, RRF)
├── Connapse.Ingestion.Tests/      # Unit tests for ingestion logic
└── Connapse.Integration.Tests/    # Integration tests with Testcontainers
```

### Dependency Graph

```
Web → Ingestion → Storage
   → Search    → Core
   → Storage   ↑
   → Core      │
               │
Ingestion → Core
         → Storage

Search → Core
      → Storage

Storage → Core

CLI → Core
   → Storage
   → Ingestion
   → Search
```

**Principle**: Core has no dependencies. All projects reference Core. Cross-layer dependencies flow downward or laterally, never upward.

## Core Abstractions

### Domain Interfaces

All swappable implementations are defined as interfaces in `Connapse.Core`:

| Interface | Purpose | Implementations |
|-----------|---------|----------------|
| `IKnowledgeIngester` | Document ingestion pipeline | `IngestionPipeline` |
| `IKnowledgeSearch` | Query → ranked results | `HybridSearchService` |
| `IEmbeddingProvider` | Text → vector | `OllamaEmbeddingProvider` |
| `IVectorStore` | Vector storage + similarity search | `PgVectorStore` |
| `IDocumentStore` | Document metadata CRUD | `PostgresDocumentStore` |
| `IDocumentParser` | File → ParsedDocument | `TextParser`, `PdfParser`, `OfficeParser` |
| `IChunkingStrategy` | ParsedDocument → Chunks | `SemanticChunker`, `FixedSizeChunker`, `RecursiveChunker` |
| `ISearchReranker` | Result fusion + reranking | `RrfReranker`, `CrossEncoderReranker` |
| `ISettingsStore` | Runtime-mutable settings | `PostgresSettingsStore` |
| `IConnectionTester` | Service connectivity validation | `MinioConnectionTester`, `OllamaConnectionTester` |

### Core Models

All domain types live in the `Connapse.Core` namespace (files in `Models/` folder):

```csharp
// Ingestion
record IngestionOptions(string? DocumentId, string? FileName, string? ContentType, ...);
record IngestionResult(string DocumentId, int ChunkCount, TimeSpan Duration, ...);
record IngestionProgress(IngestionPhase Phase, double PercentComplete, string? Message);
record ParsedDocument(string Content, Dictionary<string, string> Metadata, List<string> Warnings);
record ChunkInfo(string Content, int ChunkIndex, int TokenCount, ...);

// Search
record SearchOptions(int TopK, float MinScore, SearchMode Mode, ...);
record SearchResult(List<SearchHit> Hits, int TotalMatches, TimeSpan Duration);
record SearchHit(string ChunkId, string DocumentId, string Content, float Score, ...);

// Storage
record Document(Guid Id, string FileName, string ContentType, long FileSize, ...);
record Chunk(Guid Id, Guid DocumentId, string Content, int ChunkIndex, ...);

// Settings (7 categories)
record EmbeddingSettings(string Provider, string BaseUrl, string Model, int Dimensions, ...);
record ChunkingSettings(string Strategy, int MaxTokens, int Overlap, ...);
record SearchSettings(string Mode, int TopK, float MinScore, string RerankerStrategy, ...);
record LlmSettings(string Provider, string BaseUrl, string Model, ...);
record StorageSettings(string MinioEndpoint, string MinioAccessKey, ...);
record UploadSettings(long MaxFileSizeBytes, List<string> AllowedExtensions, ...);
record WebSearchSettings(string Provider, string ApiKey, ...);
```

## Data Flow

### Upload → Ingestion → Search Flow

```
1. User uploads file(s) via UI or API
   ↓
2. POST /api/documents
   - Validates file type + size
   - Generates DocumentId (GUID)
   - Streams file to MinIO (original storage)
   - Creates Document record in Postgres (status: Pending)
   - Enqueues IngestionJob (Channel<T> queue)
   ↓
3. Background IngestionWorker (IHostedService)
   - Dequeues job (4 concurrent workers)
   - Fetches file from MinIO
   - Calls IngestionPipeline.IngestAsync()
     ↓
     3a. Parse: IDocumentParser → ParsedDocument
     3b. Chunk: IChunkingStrategy → List<ChunkInfo>
     3c. Embed: IEmbeddingProvider → float[][]
     3d. Store: IVectorStore + IDocumentStore
   - Broadcasts progress via SignalR (IngestionProgressBroadcaster)
   - Updates Document status (Ready | Failed)
   ↓
4. Search becomes available immediately
   - User queries via UI or API
   - GET /api/search?q=query&mode=Hybrid&topK=10
     ↓
     4a. HybridSearchService runs Vector + Keyword in parallel
     4b. RrfReranker fuses results (Reciprocal Rank Fusion)
     4c. Optional: CrossEncoderReranker rescores top results
   - Returns ranked SearchHits with chunk content + metadata
```

### Settings Hierarchy

Settings are layered with increasing priority:

```
1. appsettings.json (defaults)
   ↓
2. appsettings.{Environment}.json (dev, staging, prod)
   ↓
3. User Secrets (local dev only, not committed)
   ↓
4. Environment Variables (Docker, Kubernetes)
   ↓
5. Database (Settings table, JSONB per category)
   ↓
6. CLI Arguments (highest priority)
```

**Implementation**: `DatabaseSettingsProvider` implements `IConfigurationProvider` and loads settings from Postgres at startup. Services use `IOptionsMonitor<T>` (not `IOptions<T>`) to react to runtime changes.

**Settings Page**: Users modify settings via Blazor UI → `PUT /api/settings/{category}` → Postgres → `IOptionsMonitor<T>.OnChange` triggers reload.

## Storage Architecture

### PostgreSQL + pgvector Schema

```sql
-- Core tables
documents (id, file_name, content_type, file_size, content_hash, storage_path,
           collection_id, created_at, updated_at)

chunks (id, document_id, content, chunk_index, token_count,
        start_offset, end_offset, metadata, created_at)
-- FTS: tsvector column + GIN index for keyword search

chunk_vectors (chunk_id, embedding, model_id, dimensions)
-- pgvector: embedding vector(1536) with cosine distance index

settings (category, data, updated_at)
-- JSONB per category (embedding, chunking, search, llm, storage, upload, websearch)

-- Reindexing
batches (id, status, total_documents, completed_documents,
         created_at, started_at, completed_at)
```

**Indexes**:
- `documents.content_hash` (B-tree) — deduplication on reindex
- `chunks.document_id` (B-tree) — cascade delete, chunk retrieval
- `chunks.content_tsvector` (GIN) — full-text search
- `chunk_vectors.embedding` (IVFFLAT/HNSW) — vector similarity search

**pgvector Operator**: `<=>` (cosine distance). Converted to similarity: `1 - distance`.

### MinIO (S3-Compatible Object Storage)

**Purpose**: Store original uploaded files separately from chunks/vectors.

**Structure**:
```
Bucket: knowledge-files
├── documents/{documentId}/original.{ext}
└── (future) thumbnails/{documentId}/preview.png
```

**Why S3 protocol?**:
- Cloud-agnostic (swap MinIO → AWS S3, Azure Blob, GCS with only config change)
- Standard SDK (`AWSSDK.S3`)
- Scales independently of database

## Ingestion Pipeline

### Parsing (IDocumentParser)

| Parser | Supported Extensions | Library | Extracted |
|--------|---------------------|---------|-----------|
| `TextParser` | .txt, .md, .csv, .json, .xml, .yaml | Built-in | Raw text, line count, detected type |
| `PdfParser` | .pdf | PdfPig | Text + metadata (title, author, pages) |
| `OfficeParser` | .docx, .pptx | OpenXML SDK | Paragraphs, tables, slides, properties |

**Future**: `.xlsx`, `.html`, `.eml`, code files with syntax-aware parsing.

### Chunking (IChunkingStrategy)

| Strategy | Description | Best For | Settings |
|----------|-------------|----------|----------|
| `Semantic` | Embedding-similarity-based boundaries | Long-form prose, articles | `SimilarityThreshold`, `MaxTokens` |
| `FixedSize` | Token-count chunks with overlap | General purpose, predictable | `MaxTokens`, `Overlap` |
| `Recursive` | Split on paragraphs → sentences → words | Structured documents | `MaxTokens`, `Separators` |
| `DocumentAware` | (Planned) Respects document structure (headers, sections) | Technical docs | — |

**Token Counting**: Uses `Tiktoken` (cl100k_base tokenizer) for accurate token counts.

**Overlap**: Maintains context between chunks (default: 10% overlap).

### Embedding (IEmbeddingProvider)

**Current Implementation**: `OllamaEmbeddingProvider`
- Model: `nomic-embed-text` (768d) or configurable
- Batch support: Parallel requests (default: 4 concurrent)
- Fallback: Single-threaded if batch fails
- Validation: Warns if returned dimensions ≠ expected

**Future Providers**: `OpenAIEmbeddingProvider`, `AzureOpenAIEmbeddingProvider`, `AnthropicEmbeddingProvider`

### Vector Storage (IVectorStore)

**Current Implementation**: `PgVectorStore`
- Uses raw SQL with parameterized queries (EF Core doesn't support `<=>` operator)
- Cosine distance metric
- Filters: `documentId`, `collectionId`, `metadata` key-value pairs
- Batch deletion: `DeleteByDocumentIdAsync` for reindexing

**Query Example**:
```sql
SELECT chunk_id, 1 - (embedding <=> @query_vector) AS similarity, content
FROM chunk_vectors v
JOIN chunks c ON c.id = v.chunk_id
WHERE 1 - (embedding <=> @query_vector) >= @min_score
ORDER BY embedding <=> @query_vector
LIMIT @top_k;
```

## Search Architecture

### Search Modes

| Mode | Description | When to Use |
|------|-------------|------------|
| **Semantic** | Vector similarity only | Conceptual queries ("machine learning basics") |
| **Keyword** | Full-text search (FTS) only | Exact terms ("API key configuration") |
| **Hybrid** | Vector + Keyword + RRF fusion | Most queries (default) |

### Hybrid Search Flow

```
User Query: "How to configure embeddings?"
│
├─ VectorSearchService
│  └─ Embed query → float[]
│  └─ PgVectorStore.SearchAsync(vector, topK=20)
│  └─ Returns 20 results ranked by cosine similarity
│
├─ KeywordSearchService
│  └─ Build tsquery: "configure & embedding"
│  └─ Query chunks.content_tsvector (GIN index)
│  └─ Returns 20 results ranked by ts_rank
│
└─ RrfReranker (Reciprocal Rank Fusion)
   └─ Combine both result sets
   └─ Score = Σ(1 / (k + rank)) for k=60
   └─ Deduplicate by chunk_id
   └─ Return top 10 fused results

(Optional) CrossEncoderReranker
   └─ Rescore top 10 with (query, chunk) similarity model
   └─ Return reranked top 10
```

**RRF Formula**:
```
score(chunk) = Σ (1 / (k + rank_i))
where k=60, rank_i is the rank in result set i
```

**Why RRF?**: No model required, mathematically sound, proven in production RAG systems.

## Reindexing

### Content-Hash Deduplication

**Goal**: Skip re-processing unchanged documents on bulk reindex.

**Mechanism**:
1. Compute SHA-256 hash of file content on upload
2. Store in `documents.content_hash`
3. On reindex:
   - Fetch file from MinIO
   - Compute hash
   - If `hash == document.content_hash`: skip
   - Else: delete old chunks/vectors → re-ingest

**Settings-Change Detection**:
- If `EmbeddingSettings.Model` changes → force reindex all (vectors incompatible)
- If `ChunkingSettings` changes → reindex all (chunk boundaries changed)

### Reindex Strategies

| Mode | When to Use |
|------|------------|
| `Auto` | Skip unchanged, detect settings changes |
| `Force` | Re-process everything (e.g., after schema migration) |
| `DocumentIds` | Reindex specific documents |

## Background Processing

### Ingestion Queue

**Implementation**: `Channel<IngestionJob>` (bounded capacity: 1000)

**Worker**: `IngestionWorker : BackgroundService`
- Processes 4 concurrent jobs (configurable via `UploadSettings.ConcurrentIngestions`)
- Each job: fetch from MinIO → ingest → broadcast progress → update status

**Backpressure**: If queue full, upload endpoint returns 429 Too Many Requests.

**Progress Tracking**: SignalR hub (`IngestionProgressBroadcaster`) broadcasts updates:
```csharp
record IngestionProgressUpdate(
    string JobId,
    string State,       // Pending | Processing | Completed | Failed
    string? CurrentPhase, // Parsing | Chunking | Embedding | Storing
    double PercentComplete,
    string? ErrorMessage,
    DateTime? StartedAt,
    DateTime? CompletedAt);
```

## Access Surfaces

The same core services power multiple interfaces:

| Surface | Technology | Use Case |
|---------|------------|----------|
| **Blazor UI** | Interactive Server | End users, settings management |
| **REST API** | Minimal API | Programmatic access, integrations |
| **MCP Server** | Model Context Protocol | AI agents (Claude Desktop, etc.) |
| **CLI** | (Planned) .NET CLI tool | Automation, CI/CD, scripting |

All surfaces call the same domain services (`IKnowledgeIngester`, `IKnowledgeSearch`, etc.) — no duplication.

## Technology Stack

| Layer | Technology | Version |
|-------|-----------|---------|
| **Runtime** | .NET | 10 |
| **UI Framework** | Blazor WebApp | Interactive Server mode |
| **Database** | PostgreSQL + pgvector | 17 + latest pgvector |
| **Object Storage** | MinIO | Latest (S3-compatible) |
| **Embedding/LLM** | Ollama | Optional (local-first) |
| **ORM** | Entity Framework Core | 10 |
| **HTTP Client** | HttpClient | Built-in, typed clients |
| **Real-time** | SignalR | Ingestion progress |
| **Testing** | xUnit + FluentAssertions + Testcontainers | Latest |
| **Containerization** | Docker + Docker Compose | Latest |

## Design Patterns

### Dependency Injection

All services registered in `Program.cs` with appropriate lifetimes:

```csharp
// Scoped (per-request)
builder.Services.AddScoped<IKnowledgeIngester, IngestionPipeline>();
builder.Services.AddScoped<IKnowledgeSearch, HybridSearchService>();
builder.Services.AddScoped<IDocumentStore, PostgresDocumentStore>();
builder.Services.AddScoped<IVectorStore, PgVectorStore>();

// Singleton (shared state)
builder.Services.AddSingleton<IIngestionQueue, IngestionQueue>();
builder.Services.AddSingleton<IngestionProgressBroadcaster>();

// Hosted (background workers)
builder.Services.AddHostedService<IngestionWorker>();
```

### Strategy Pattern

Swappable implementations via interfaces:
- Chunking: `SemanticChunker | FixedSizeChunker | RecursiveChunker`
- Reranking: `RrfReranker | CrossEncoderReranker`
- Parsing: `TextParser | PdfParser | OfficeParser`

Selected at runtime based on `Settings` or `IngestionOptions`.

### Factory Pattern

`DocumentParserFactory` selects parser based on file extension:

```csharp
IDocumentParser parser = fileName.ToLower() switch
{
    var f when f.EndsWith(".pdf") => new PdfParser(),
    var f when f.EndsWith(".docx") => new OfficeParser(),
    _ => new TextParser()
};
```

### Options Pattern

All configuration via strongly-typed records + `IOptionsMonitor<T>`:

```csharp
public class HybridSearchService
{
    private readonly IOptionsMonitor<SearchSettings> _settings;

    public HybridSearchService(IOptionsMonitor<SearchSettings> settings)
    {
        _settings = settings;
        _settings.OnChange(OnSettingsChanged); // React to runtime changes
    }
}
```

## Security Considerations

### Input Validation

- **File uploads**: Type whitelist, size limits, content scanning (planned)
- **Query sanitization**: Parameterized SQL, no string concatenation
- **Path traversal**: Virtual file system with root validation

### Secrets Management

- **Development**: User Secrets (`dotnet user-secrets`)
- **Production**: Environment variables or Azure Key Vault / AWS Secrets Manager
- **Never committed**: `.gitignore` includes `appsettings.*.json` (except `appsettings.json` defaults)

### Authentication & Authorization

- **Phase 1**: No auth (single-user, local deployment)
- **Planned**: JWT bearer tokens, role-based access, document-level permissions

## Performance Characteristics

### Ingestion

- **Target**: < 30 seconds from upload to searchable
- **Actual**: ~5-15 seconds for typical documents (10-50 pages)
- **Bottleneck**: Embedding API latency (Ollama: ~100ms per chunk)
- **Optimization**: Batch embedding (4 concurrent requests)

### Search

- **Vector search**: < 50ms for 10k chunks (pgvector HNSW index)
- **Keyword search**: < 20ms (GIN index on tsvector)
- **Hybrid (RRF)**: < 100ms (both searches + fusion)
- **Cross-encoder reranking**: +200-500ms (depends on model)

### Concurrency

- **Upload**: 4 concurrent ingestion workers
- **Search**: Unlimited (read-only, stateless)
- **Settings updates**: Write lock on settings table (rare operation)

## Observability

### Logging

- **Structured logging**: `ILogger<T>` with JSON output in production
- **Levels**: Debug (dev), Information (prod), Warning (issues), Error (failures)
- **Enrichment**: Request IDs, document IDs, chunk counts, timings

### Metrics (Planned)

- Ingestion rate (docs/min)
- Search latency (p50, p95, p99)
- Queue depth (pending jobs)
- Embedding token usage

### Health Checks (Planned)

- `/health`: Overall health
- `/health/ready`: Ready to serve traffic (DB + MinIO reachable)
- `/health/live`: Process alive

## Future Architecture Directions

### Horizontal Scaling

- **Stateless web tier**: Scale web service behind load balancer
- **External queue**: Swap `Channel<T>` → Redis Streams / RabbitMQ
- **Distributed cache**: Add Redis for settings + session state

### Multi-Tenancy

- **Tenant isolation**: Add `tenant_id` to all tables, row-level security
- **Separate databases**: One Postgres DB per tenant (schema-per-tenant)
- **Separate buckets**: MinIO bucket per tenant

### Advanced Features

- **Web crawling**: URL ingestion with scheduled refresh
- **OCR**: Extract text from scanned PDFs and images
- **Entity extraction**: NER (Named Entity Recognition) for metadata
- **Question answering**: RAG-powered Q&A with citations
- **Agent tools**: MCP tools for file management, search, summarization

## References

- [.claude/state/decisions.md](../.claude/state/decisions.md) — Architectural decisions with rationale
- [.claude/state/conventions.md](../.claude/state/conventions.md) — Code patterns and conventions
- [.claude/state/api-surface.md](../.claude/state/api-surface.md) — All public interfaces
- [api.md](api.md) — REST API + MCP reference
- [deployment.md](deployment.md) — Deployment guides
