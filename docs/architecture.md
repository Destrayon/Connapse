# Architecture

> Part of [Connapse](https://github.com/Destrayon/Connapse) — open-source AI knowledge management platform.

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
│  │  - Containers  │      │  - Containers    │      │
│  │  - File Browser│      │  - Files/Folders │      │
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
│         → Embed          → CC Fusion     + MinIO   │
│         → Store                                     │
└─────────────────────────────────────────────────────┘
                   │
┌──────────────────┴──────────────────────────────────┐
│             Infrastructure                           │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────┐ │
│  │  PostgreSQL  │  │    MinIO     │  │  Ollama   │ │
│  │  + pgvector  │  │  (S3 API)    │  │ (optional)│ │
│  │              │  │              │  │           │ │
│  │ - containers │  │ - original   │  │ - embed   │ │
│  │ - documents  │  │   files      │  │ - llm     │ │
│  │ - chunks     │  │              │  │           │ │
│  │ - vectors    │  │              │  │           │ │
│  │ - folders    │  │              │  │           │ │
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
├── Connapse.Identity/     # Auth: ASP.NET Core Identity, PAT, JWT, RBAC, audit logging
├── Connapse.Ingestion/    # Document parsing, chunking, pipeline orchestration
├── Connapse.Search/       # Vector search, keyword search, hybrid fusion
├── Connapse.Storage/      # Database, vector store, object storage, settings
├── Connapse.Agents/       # (Planned) Agent orchestration, tools, memory
└── Connapse.CLI/          # Command-line interface (auth, container mgmt, upload, search)

tests/
├── Connapse.Core.Tests/           # Unit tests (parsers, chunkers, fusion, search)
├── Connapse.Ingestion.Tests/      # Unit tests for ingestion logic
├── Connapse.Identity.Tests/       # Unit tests (PatService, JwtTokenService, ScopeHandler, AdminSeed)
└── Connapse.Integration.Tests/    # Integration tests with Testcontainers
```

### Dependency Graph

```
Web → Identity  → Core
   → Ingestion → Storage
   → Search    → Core
   → Storage   ↑
   → Core      │
               │
Identity → Core

Ingestion → Core
         → Storage

Search → Core
      → Storage

Storage → Core
       → AWSSDK.S3, AWSSDK.SSOAdmin, AWSSDK.SSOOIDC
       → Azure.Storage.Blobs, Azure.Identity
       → OpenAI, Azure.AI.OpenAI, Anthropic

CLI → Core
   (no longer references Identity, Ingestion, Search, or Storage — uses HTTP API only)
```

**Principle**: Core has no dependencies. All projects reference Core. Cross-layer dependencies flow downward or laterally, never upward. `Connapse.CLI` is a thin HTTP client and does not reference domain projects directly.

## Core Abstractions

### Domain Interfaces

All swappable implementations are defined as interfaces in `Connapse.Core` or `Connapse.Identity`:

**Core (`Connapse.Core`)**:

| Interface | Purpose | Implementations |
|-----------|---------|----------------|
| `IKnowledgeIngester` | Document ingestion pipeline | `IngestionPipeline` |
| `IKnowledgeSearch` | Query → ranked results | `HybridSearchService` |
| `IContainerStore` | Container CRUD (isolated vector indexes) | `PostgresContainerStore` |
| `IFolderStore` | Folder management within containers | `PostgresFolderStore` |
| `IDocumentStore` | Document metadata CRUD (container-scoped) | `PostgresDocumentStore` |
| `IVectorStore` | Vector storage + similarity search | `PgVectorStore` |
| `IEmbeddingProvider` | Text → vector | `OllamaEmbeddingProvider`, `OpenAiEmbeddingProvider`, `AzureOpenAiEmbeddingProvider` |
| `IIngestionQueue` | Background ingestion job queue + cancellation | `IngestionQueue` |
| `IReindexService` | Content-hash dedup reindexing | `ReindexService` |
| `IDocumentParser` | File → ParsedDocument | `TextParser`, `PdfParser`, `OfficeParser` |
| `IChunkingStrategy` | ParsedDocument → Chunks | `SemanticChunker`, `FixedSizeChunker`, `RecursiveChunker` |
| `ISearchReranker` | Result reranking | `CrossEncoderReranker` |
| `ISettingsStore` | Runtime-mutable settings | `PostgresSettingsStore` |
| `ILlmProvider` | LLM completion + streaming | `OllamaLlmProvider`, `OpenAiLlmProvider`, `AzureOpenAiLlmProvider`, `AnthropicLlmProvider` |
| `IConnector` | Storage backend I/O | `MinioConnector`, `FilesystemConnector`, `S3Connector`, `AzureBlobConnector` |
| `IConnectorFactory` | Create connector from container | `ConnectorFactory` |
| `IContainerSettingsResolver` | Per-container settings overrides | `ContainerSettingsResolver` |
| `ICloudScopeService` | IAM-derived access control | `CloudScopeService` |
| `IConnectionTester` | Service connectivity validation | `MinioConnectionTester`, `OllamaConnectionTester`, `S3ConnectionTester`, `AzureBlobConnectionTester`, `AwsSsoConnectionTester`, `AzureAdConnectionTester`, `OpenAiConnectionTester`, `AzureOpenAiConnectionTester`, `AnthropicConnectionTester` |

**Identity (`Connapse.Identity`)**:

| Interface | Purpose | Implementations |
|-----------|---------|----------------|
| `IPatService` | Personal Access Token lifecycle | `PatService` |
| `ITokenService` | JWT generation + refresh rotation | `JwtTokenService` |
| `IAuditLogger` | Structured audit trail | `AuditLogger` |
| `IAgentService` | Agent + agent API key CRUD | `AgentService` |
| `IInviteService` | Invite-only registration tokens | `InviteService` |
| `ICloudIdentityService` | Cloud identity linking (AWS/Azure) | `CloudIdentityService` |
| `ICloudIdentityStore` | Encrypted cloud identity persistence | `PostgresCloudIdentityStore` |

### Core Models

All domain types live in the `Connapse.Core` namespace (files in `Models/` folder):

```csharp
// Containers & Storage
enum ConnectorType { MinIO = 0, Filesystem = 1, S3 = 3, AzureBlob = 4 }
record Container(string Id, string Name, string? Description, ConnectorType ConnectorType,
    string? ConnectorConfig, DateTime CreatedAt, DateTime UpdatedAt, int DocumentCount);
record ContainerSettingsOverrides { ChunkingSettings? Chunking, EmbeddingSettings? Embedding,
    SearchSettings? Search, UploadSettings? Upload };
record CreateContainerRequest(string Name, string? Description,
    ConnectorType? ConnectorType, string? ConnectorConfig);
record Folder(string Id, string ContainerId, string Path, DateTime CreatedAt);
record Document(string Id, string ContainerId, string FileName, string? ContentType, string Path,
    long SizeBytes, DateTime CreatedAt, Dictionary<string, string> Metadata);

// Ingestion
record IngestionOptions(string? DocumentId, string? FileName, string? ContentType,
    string? ContainerId, string? Path, ChunkingStrategy Strategy, Dictionary<string, string>? Metadata);
record IngestionResult(string DocumentId, int ChunkCount, TimeSpan Duration, ...);
record IngestionProgress(IngestionPhase Phase, double PercentComplete, string? Message);
record IngestionJob(string JobId, string DocumentId, string ContainerId, string FileName,
    string StoragePath, string Path, IngestionOptions Options);

// Search
record SearchOptions(int TopK = 10, float MinScore = 0.0f, string? ContainerId = null,
    SearchMode Mode = SearchMode.Hybrid, Dictionary<string, string>? Filters = null);
record SearchResult(List<SearchHit> Hits, int TotalMatches, TimeSpan Duration);
record SearchHit(string ChunkId, string DocumentId, string Content, float Score, ...);

// Settings (5 app categories + 2 identity categories)
record EmbeddingSettings(string Provider, string BaseUrl, string Model, int Dimensions, ...);
record ChunkingSettings(string Strategy, int MaxTokens, int Overlap, ...);
record SearchSettings(string Mode, int TopK, float FusionAlpha, string FusionMethod,
    bool AutoCut, bool EnableCrossModelSearch, ...);
record LlmSettings(string Provider, string BaseUrl, string Model, string? ApiKey, ...);
record UploadSettings(long MaxFileSizeBytes, List<string> AllowedExtensions, ...);
// Identity settings (separate config sections)
class AwsSsoSettings { IssuerUrl, Region, ClientId?, ClientSecret?, ClientSecretExpiresAt? }
class AzureAdSettings { ClientId, TenantId, ClientSecret }
```

## Data Flow

### Upload → Ingestion → Search Flow

```
1. User uploads file(s) via File Browser UI, API, CLI, or MCP
   ↓
2. POST /api/containers/{containerId}/files
   - Validates file type + size
   - Generates DocumentId (GUID)
   - Streams file to MinIO (original storage)
   - Creates Document record in Postgres (status: Pending, container-scoped)
   - Enqueues IngestionJob (Channel<T> queue) with ContainerId + Path
   ↓
3. Background IngestionWorker (IHostedService)
   - Dequeues job (4 concurrent workers, per-job CancellationToken)
   - Fetches file from MinIO
   - Calls IngestionPipeline.IngestAsync()
     ↓
     3a. Parse: IDocumentParser → ParsedDocument
     3b. Chunk: IChunkingStrategy → List<ChunkInfo>
     3c. Embed: IEmbeddingProvider → float[][]
     3d. Store: IVectorStore + IDocumentStore (upsert — updates on reindex)
   - Broadcasts progress via SignalR (IngestionProgressBroadcaster)
   - Updates Document status (Ready | Failed)
   ↓
4. Search becomes available immediately
   - User queries via UI, API, CLI, or MCP
   - GET /api/containers/{containerId}/search?q=query&mode=Hybrid&topK=10
     ↓
     4a. HybridSearchService runs Vector + Keyword in parallel (separate DbContext scopes)
     4b. Convex Combination fuses results (min-max normalize inputs, alpha-weighted sum)
     4c. Optional: CrossEncoderReranker rescores top results
     4d. Optional: AutoCut trims results after largest score gap
     4e. Apply TopK limit
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
-- Container isolation
containers (id, name, description, created_at, updated_at)
-- Unique index on name

-- Documents (container-scoped)
documents (id, container_id, path, file_name, content_hash, size_bytes,
           mime_type, status, error_message, metadata, created_at, updated_at)
-- FK: container_id → containers (CASCADE DELETE)

-- Chunks (denormalized container_id for query performance)
chunks (id, document_id, container_id, content, chunk_index,
        search_vector, metadata, created_at)
-- FK: document_id → documents (CASCADE DELETE)
-- FTS: search_vector tsvector column + GIN index

-- Chunk vectors (denormalized container_id)
chunk_vectors (id, chunk_id, container_id, embedding, model_id, metadata)
-- FK: chunk_id → chunks (CASCADE DELETE)
-- pgvector: embedding vector (UNCONSTRAINED — supports mixed dimensions)
-- Partial IVFFlat indexes per model_id (created when ≥10 vectors)

-- Folders (for empty folder support)
folders (id, container_id, path, created_at)
-- FK: container_id → containers (CASCADE DELETE)
-- Unique index on (container_id, path)

-- Settings (runtime-mutable, JSONB per category)
settings (category, values, updated_at)
-- JSONB per category (embedding, chunking, search, llm, upload, awssso, azuread)

-- Cloud identities (user ↔ cloud provider linkage)
user_cloud_identities (id, user_id, provider, encrypted_data, created_at, last_used_at)
-- Unique index on (user_id, provider)
-- DataProtection-encrypted identity data
```

**Indexes**:
- `containers.name` (unique B-tree) — name lookups
- `documents.container_id` (B-tree) — container-scoped queries
- `documents.content_hash` (B-tree) — deduplication on reindex
- `documents.(container_id, path)` (unique B-tree) — path uniqueness within container
- `chunks.document_id` (B-tree) — cascade delete, chunk retrieval
- `chunks.container_id` (B-tree) — container-scoped search
- `chunks.search_vector` (GIN) — full-text search
- `chunk_vectors.chunk_id` (B-tree) — cascade delete
- `chunk_vectors.container_id` (B-tree) — container-scoped vector search
- `chunk_vectors.embedding` — partial IVFFlat per model_id (created dynamically when ≥10 vectors)
- `chunk_vectors.model_id` (B-tree) — model-specific vector queries
- `folders.(container_id, path)` (unique B-tree) — folder path uniqueness

**pgvector Operator**: `<=>` (cosine distance). Converted to similarity: `1 - distance`.

### MinIO (S3-Compatible Object Storage)

**Purpose**: Store original uploaded files separately from chunks/vectors.

**Structure**:
```
Bucket: knowledge-files
├── {containerId}/{documentId}/original.{ext}
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

Three providers, selected at runtime via `EmbeddingSettings.Provider`:

| Provider | Implementation | SDK | Notes |
|----------|---------------|-----|-------|
| **Ollama** | `OllamaEmbeddingProvider` | HttpClient | Default, local-first, `nomic-embed-text` (768d) |
| **OpenAI** | `OpenAiEmbeddingProvider` | `OpenAI` 2.9.0 | `text-embedding-3-small/large`, Matryoshka truncation |
| **Azure OpenAI** | `AzureOpenAiEmbeddingProvider` | `Azure.AI.OpenAI` 2.1.0 | Same models via Azure endpoint |

**DI registration**: Factory delegate reads `EmbeddingSettings.Provider` at scope time — no restart needed.

**Multi-dimension support**: The `chunk_vectors.embedding` column is unconstrained `vector` (no fixed dimension). Partial IVFFlat indexes are created per `model_id` by `VectorColumnManager` when ≥10 vectors exist. Queries cast to `::vector(N)` where N = query vector length.

### Vector Storage (IVectorStore)

**Current Implementation**: `PgVectorStore`
- Uses raw SQL with named `NpgsqlParameter` objects (EF Core doesn't support `<=>` operator)
- **Critical**: Must use named parameters (not positional `{N}`) with pgvector `Vector` type — positional params silently fail
- Cosine distance metric
- Filters: `containerId` (required), `documentId`, `metadata` key-value pairs
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
| **Hybrid** | Vector + Keyword + Convex Combination fusion | Most queries (default) |

**Cross-Model Search**: When `SearchSettings.EnableCrossModelSearch` is true and legacy vectors exist (vectors from a different embedding model), Semantic mode is automatically overridden to Hybrid. This ensures keyword search covers documents whose vectors are incompatible with the current model's query vector.

### Hybrid Search Flow

```
User Query: "How to configure embeddings?" (container: my-project)
│
├─ VectorSearchService (separate DbContext scope)
│  └─ Embed query → float[]
│  └─ PgVectorStore.SearchAsync(vector, containerId, topK=20)
│  └─ Returns 20 results ranked by cosine similarity (filtered by container)
│
├─ KeywordSearchService (separate DbContext scope)
│  └─ Build tsquery: "configure & embedding"
│  └─ Query chunks.search_vector WHERE container_id = @id (GIN index)
│  └─ Returns 20 results ranked by ts_rank
│
└─ Convex Combination Fusion (default) or DBSF
   └─ Min-max normalize each input list independently to [0,1]
   └─ score = alpha × norm_vector + (1 - alpha) × norm_keyword
   └─ Deduplicate by chunk_id
   └─ Return fused results with meaningful [0,1] scores

(Optional) CrossEncoderReranker
   └─ Rescore top results with (query, chunk) similarity model
   └─ Provider scores ([0,1]) used directly — no post-normalization

(Optional) AutoCut
   └─ Detect largest score gap in results
   └─ Trim tail after gap (keeps top relevance cluster)

└─ Apply TopK limit → return final results
```

**Fusion Methods**:

| Method | Normalization | When to Use |
|--------|--------------|-------------|
| **Convex Combination** (default) | Min-max on inputs | General purpose — clear [0,1] scores |
| **DBSF** | Mean ± 3σ on inputs | Outlier-heavy data — one retriever returns extreme scores |

**Convex Combination Formula**:
```
score(chunk) = alpha × norm_vector(chunk) + (1 - alpha) × norm_keyword(chunk)
where alpha ∈ [0,1] (default 0.5), norm = min-max normalized input score
```

**Why CC over RRF?**: RRF discards score magnitude (rank-only), producing tiny ~0.016 scores that require misleading post-normalization. CC preserves score information, produces genuine [0,1] output, and outperforms RRF by 1-5 nDCG@10 points (Bruch et al., ACM TOIS 2023). Industry standard: Elasticsearch linear retriever, Weaviate relativeScoreFusion, OpenSearch normalization processor.

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
- Per-job `CancellationTokenSource` linked to application shutdown token

**Job Cancellation**: `IIngestionQueue.CancelJobForDocumentAsync(documentId)` cancels in-progress jobs (used when deleting a file that's still being ingested).

**Upsert Logic**: `IngestionPipeline` checks `FindAsync` before insert — updates existing document during reindex, creates new otherwise.

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

| Surface | Technology | Auth | Use Case |
|---------|------------|------|----------|
| **Blazor UI** | Interactive Server | Cookie | End users, settings management |
| **REST API** | Minimal API | PAT or JWT Bearer | Programmatic access, integrations |
| **MCP Server** | Model Context Protocol | Agent API Key | AI agents (Claude Desktop, etc.) |
| **CLI** | .NET tool / native binary (`connapse`) | PAT (stored in `~/.connapse/credentials.json`) | Automation, CI/CD, scripting |

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
| **Auth** | ASP.NET Core Identity | Cookie + PAT + JWT (HS256) |
| **HTTP Client** | HttpClient | Built-in, typed clients |
| **Real-time** | SignalR | Ingestion progress (JWT auth via query string) |
| **Testing** | xUnit + FluentAssertions + Testcontainers | 457 tests (unit + integration) |
| **Containerization** | Docker + Docker Compose | Latest |
| **CI/CD** | GitHub Actions | Build, test, release (native binaries + NuGet) |

## Design Patterns

### Dependency Injection

All services registered in `Program.cs` with appropriate lifetimes:

```csharp
// Scoped (per-request)
builder.Services.AddScoped<IKnowledgeIngester, IngestionPipeline>();
builder.Services.AddScoped<IKnowledgeSearch, HybridSearchService>();
builder.Services.AddScoped<IContainerStore, PostgresContainerStore>();
builder.Services.AddScoped<IFolderStore, PostgresFolderStore>();
builder.Services.AddScoped<IDocumentStore, PostgresDocumentStore>();
builder.Services.AddScoped<IVectorStore, PgVectorStore>();
builder.Services.AddScoped<IReindexService, ReindexService>();

// Singleton (shared state)
builder.Services.AddSingleton<IIngestionQueue, IngestionQueue>();
builder.Services.AddSingleton<IngestionProgressBroadcaster>();
builder.Services.AddSingleton<ISettingsReloader, SettingsReloader>();

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

## Authentication & Authorization (v0.2.0)

Connapse uses a three-tier auth system implemented in `Connapse.Identity`.

### Auth Tiers

| Tier | Mechanism | Used By |
|------|-----------|---------|
| **Cookie** | ASP.NET Core Identity (14-day sliding) | Blazor UI browser sessions |
| **PAT** | `X-Api-Key: cnp_<sha256-hashed>` | CLI, REST API, automation |
| **JWT** | HS256 Bearer (60-90 min + refresh) | SDK clients, external integrations |

**Multi-scheme handler**: A `PolicyScheme` named `MultiScheme` selects the correct auth handler based on request headers. `DefaultAuthenticateScheme` is explicitly set to `"MultiScheme"` to prevent `AddIdentity<>` from overriding it with the cookie scheme.

### Roles & Scopes

| Role | Access |
|------|--------|
| **Admin** | All endpoints, user management, settings, agent management |
| **Editor** | Upload, delete files, manage containers and folders, search |
| **Viewer** | Search and browse (read-only) |
| **Agent** | MCP endpoint only (injected via synthetic `Agent` claim from API key handler) |

Authorization policies: `RequireAdmin`, `RequireEditor`, `RequireViewer`, `RequireAgent` — implemented via `ScopeAuthorizationHandler` which maps roles to permission scopes.

### Identity Database

`ConnapseIdentityDbContext` is a separate `IdentityDbContext` that shares the same PostgreSQL database but uses its own migration history (`__identity_ef_migrations_history`). Tables:

```
users               — ASP.NET Core Identity users (ConnapseUser)
roles               — ConnapseRole (Admin, Editor, Viewer)
user_roles, user_claims, role_claims, user_logins, user_tokens  — Identity join tables
personal_access_tokens — PATs (SHA-256 hashed, cnp_ prefix)
refresh_tokens      — JWT refresh token rotation
audit_logs          — Structured audit trail
user_invitations    — Invite-only registration tokens
agents              — Agent entities (non-human identities)
agent_api_keys      — Agent API keys (SHA-256 hashed)
```

### First-Time Setup

On a fresh install, `AdminSeedService` runs on startup:
1. If `CONNAPSE_ADMIN_EMAIL` and `CONNAPSE_ADMIN_PASSWORD` env vars are set, creates the admin account automatically.
2. If no users exist and env vars are absent, the login page shows a setup form (first user becomes admin).
3. Subsequent users require an admin invitation token.

### Input Validation

- **File uploads**: Type whitelist, size limits, content scanning (planned)
- **Query sanitization**: Parameterized SQL, no string concatenation
- **Path traversal**: Virtual file system with root validation

### Secrets Management

- **Development**: User Secrets (`dotnet user-secrets`)
- **Production**: Environment variables or Azure Key Vault / AWS Secrets Manager
- **Never committed**: `.gitignore` includes `appsettings.*.json` (except `appsettings.json` defaults)

## Performance Characteristics

### Ingestion

- **Target**: < 30 seconds from upload to searchable
- **Actual**: ~5-15 seconds for typical documents (10-50 pages)
- **Bottleneck**: Embedding API latency (Ollama: ~100ms per chunk)
- **Optimization**: Batch embedding (4 concurrent requests)

### Search

- **Vector search**: < 50ms for 10k chunks (pgvector HNSW index)
- **Keyword search**: < 20ms (GIN index on tsvector)
- **Hybrid (CC fusion)**: < 100ms (both searches + fusion)
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

**Current**: Container-based isolation provides project-level separation. Each container has its own vector index, document set, and folder hierarchy. All queries are scoped to a single container — no cross-container data leakage.

**Future** (for true multi-user environments):
- **Tenant isolation**: Add `tenant_id` to containers, row-level security
- **Separate databases**: One Postgres DB per tenant (schema-per-tenant)
- **Separate buckets**: MinIO bucket per tenant

### Advanced Features

- **Web crawling**: URL ingestion with scheduled refresh
- **OCR**: Extract text from scanned PDFs and images
- **Entity extraction**: NER (Named Entity Recognition) for metadata
- **Question answering**: RAG-powered Q&A with citations
- **Agent tools**: Extended MCP tools for summarization, conversation memory

## Connector Architecture (v0.3.0)

### Connector Types

A **Container** is a logical knowledge base; a **Connector** is the storage technology that backs it.

| Type | `SupportsLiveWatch` | Background Sync | Config Required |
|------|---------------------|-----------------|-----------------|
| **MinIO** | No | 5-min polling | No (global) |
| **Filesystem** | Yes (`FileSystemWatcher`) | Live + 5-min rescan | `rootPath` |
| **S3** | No | 5-min polling | `bucketName`, `region` |
| **AzureBlob** | No | 5-min polling | `storageAccountName`, `containerName` |

### IConnector Interface

All connectors implement `IConnector`: `ReadFileAsync`, `WriteFileAsync`, `DeleteFileAsync`, `ListFilesAsync`, `ExistsAsync`, `WatchAsync`. Paths are virtual (`/docs/file.pdf`), not filesystem-absolute.

### ConnectorWatcherService

`BackgroundService` (Singleton + HostedService) that manages all container watching:

- **Filesystem**: `FileSystemWatcher` with 750ms debounce, 5-min rescan safety net
- **Cloud (S3/AzureBlob/MinIO)**: 5-min `PeriodicTimer` polling, in-memory snapshot for change detection
- First poll: detects creates + deletes; subsequent polls: also detects LastModified/SizeBytes changes
- New remote files pre-registered as "Pending" in DB before ingestion starts (immediate UI feedback)

### Per-Container Settings

`ContainerSettingsOverrides` allows each container to override global settings:

```
Resolution order: Container override → Global DB setting → Application default
```

Resolved via `IContainerSettingsResolver` for chunking, embedding, search, and upload settings.

See [connectors.md](connectors.md) for full connector documentation.

## Cloud Identity & Scope Enforcement (v0.3.0)

### Identity Linking

Users link one cloud identity per provider via their Profile page:

| Provider | Flow | Stored Data |
|----------|------|-------------|
| **AWS** | IAM Identity Center device authorization | Account IDs, primary account, display name |
| **Azure** | OAuth2 authorization code + PKCE | Object ID, Tenant ID, display name |

Identity data is encrypted at rest via ASP.NET Core DataProtection (`IDataProtector`).

### Scope Enforcement

`CloudScopeService` checks cloud identity permissions before allowing access to cloud-backed containers:

1. Retrieve user's linked identity for the container's cloud provider
2. Call provider-specific `ICloudIdentityProvider.DiscoverScopesAsync()`
3. Cache result: 15-min allow TTL, 5-min deny TTL (`IConnectorScopeCache`)
4. Inject allowed prefix filter into search/browse queries

Enforcement applied to: document endpoints, search endpoints, folder endpoints, sync trigger.

See [aws-sso-setup.md](aws-sso-setup.md) and [azure-identity-setup.md](azure-identity-setup.md) for setup guides.

## Multi-Provider Support (v0.3.0)

### Embedding Providers

| Provider | SDK | Models |
|----------|-----|--------|
| Ollama | HttpClient (typed) | nomic-embed-text, all-minilm, etc. |
| OpenAI | `OpenAI` 2.9.0 | text-embedding-3-small, text-embedding-3-large |
| Azure OpenAI | `Azure.AI.OpenAI` 2.1.0 | Same models via Azure endpoint |

### LLM Providers

| Provider | SDK | Models |
|----------|-----|--------|
| Ollama | HttpClient (NDJSON) | llama3.2, mistral, etc. |
| OpenAI | `OpenAI` 2.9.0 | gpt-4o, gpt-4o-mini, etc. |
| Azure OpenAI | `Azure.AI.OpenAI` 2.1.0 | Same models via Azure endpoint |
| Anthropic | `Anthropic` 12.8.0 | claude-sonnet-4-20250514, etc. |

All providers implement `ILlmProvider` (CompleteAsync + StreamAsync via IAsyncEnumerable). Resolved at scope time via factory delegate reading `LlmSettings.Provider`.

### Cross-Encoder Reranking Providers

`CrossEncoderReranker` uses `ILlmProvider` for reranking (refactored from raw Ollama HTTP).

### Multi-Dimension Vector Support

The `chunk_vectors.embedding` column is unconstrained `vector` (no fixed dimension). This enables:
- Switching embedding models without dropping existing vectors
- Mixed embedding dimensions across containers/time periods
- `VectorColumnManager` creates partial IVFFlat indexes per `model_id` when ≥10 vectors exist
- Search queries add `model_id` WHERE filter and `::vector(N)` dimension cast

## References

- [api.md](api.md) — REST API + MCP reference
- [connectors.md](connectors.md) — Connector types and configuration
- [deployment.md](deployment.md) — Deployment guides
