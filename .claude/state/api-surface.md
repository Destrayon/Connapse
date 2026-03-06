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
    ConnectorType ConnectorType,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int DocumentCount = 0,
    ContainerSettingsOverrides? SettingsOverrides = null,
    string? ConnectorConfig = null);

public enum ConnectorType { MinIO = 0, Filesystem = 1, S3 = 3, AzureBlob = 4 }

public record CreateContainerRequest(
    string Name,
    string? Description = null,
    ConnectorType ConnectorType = ConnectorType.MinIO,
    string? ConnectorConfig = null);
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

// Implementations: OllamaEmbeddingProvider, OpenAiEmbeddingProvider, AzureOpenAiEmbeddingProvider
// - Ollama: HttpClient with typed client pattern
// - OpenAI: OpenAI SDK 2.9.0 (EmbeddingClient)
// - Azure OpenAI: Azure.AI.OpenAI SDK 2.1.0 (AzureOpenAIClient)
// - DI: factory delegate reads EmbeddingSettings.Provider at scope time
// - Dimensions sent only for text-embedding-3-* models (Matryoshka truncation)
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
// - Filters: containerId, documentId, pathPrefix, modelId via WHERE clauses
// - JOINs chunks + documents for complete result metadata
// - Quoted column aliases in SQL ("ChunkId", "Distance", etc.)
// - v0.3.0: Unconstrained vector column with partial IVFFlat indexes per model_id
// - v0.3.0: ::vector(N) dimension cast in search queries (N = queryVector.Length)
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

// Implementations (15 total):
// - Infrastructure: OllamaConnectionTester, MinioConnectionTester
// - Cloud Connectors: S3ConnectionTester, AzureBlobConnectionTester
// - Cloud Identity: AwsSsoConnectionTester, AzureAdConnectionTester
// - Embedding: OpenAiConnectionTester, AzureOpenAiConnectionTester,
//              TeiConnectionTester, CohereConnectionTester, JinaConnectionTester, AzureAIFoundryConnectionTester
// - LLM: OpenAiLlmConnectionTester, AzureOpenAiLlmConnectionTester, AnthropicConnectionTester
```

### IAgentTool / IAgentMemory / IKnowledgeFileSystem / IFileStore

These interfaces remain unchanged from the initial design. See CLAUDE.md for definitions.

> **Note**: `IWebSearchProvider` was removed in v0.3.0 (settings cleanup). Web search is no longer a configurable strategy.

---

## Identity Interfaces (Connapse.Identity — v0.2.0)

### IPatService

```csharp
public interface IPatService
{
    Task<(string RawToken, PersonalAccessTokenEntity Entity)> CreateAsync(
        string userId, string name, DateTime? expiresAt, CancellationToken ct = default);
    Task<IReadOnlyList<PersonalAccessTokenEntity>> ListAsync(string userId, CancellationToken ct = default);
    Task<bool> RevokeAsync(string userId, Guid tokenId, CancellationToken ct = default);
    Task<ConnapseUser?> ValidateAsync(string rawToken, CancellationToken ct = default);
}
// Token format: cnp_<64-random-chars>. Stored as SHA-256 hash.
// Prefix: first 12 chars for display ("cnp_abc123...")
```

### ITokenService (JWT)

```csharp
public interface ITokenService
{
    Task<TokenResponse> CreateTokenAsync(ConnapseUser user, CancellationToken ct = default);
    Task<TokenResponse?> RefreshAsync(string refreshToken, CancellationToken ct = default);
}
// HS256 signed JWTs. Access token: 90 min. Refresh token: 30 days.
// Refresh tokens are single-use (rotation on each call).
// TokenResponse: { AccessToken, RefreshToken, ExpiresIn, TokenType }
```

### IAuditLogger

```csharp
public interface IAuditLogger
{
    Task LogAsync(string userId, string action, string? resourceId = null,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default);
}
// Actions emitted: "doc.uploaded", "doc.deleted", "container.created", "container.deleted",
//                  "auth.login", "auth.pat.created", "auth.pat.revoked"
// Fire-and-forget pattern (no await at call sites) — audit failures do not propagate.
```

### IAgentService

```csharp
public interface IAgentService
{
    Task<AgentEntity> CreateAsync(string name, string? description, CancellationToken ct = default);
    Task<AgentEntity?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<AgentEntity>> ListAsync(CancellationToken ct = default);
    Task<bool> SetEnabledAsync(Guid id, bool isEnabled, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<(string RawToken, AgentApiKeyEntity Entity)> CreateKeyAsync(Guid agentId, string name, CancellationToken ct = default);
    Task<IReadOnlyList<AgentApiKeyEntity>> ListKeysAsync(Guid agentId, CancellationToken ct = default);
    Task<bool> RevokeKeyAsync(Guid agentId, Guid keyId, CancellationToken ct = default);
    Task<(AgentEntity? Agent, AgentApiKeyEntity? Key)> ValidateKeyAsync(string rawToken, CancellationToken ct = default);
}
// Agent API keys: cnp_ prefix, SHA-256 stored, same format as PATs.
// ApiKeyAuthenticationHandler checks both tables (PATs first, then agent keys).
// Valid agent key injects ClaimTypes.Role = "Agent" as synthetic claim.
```

### IInviteService

```csharp
public interface IInviteService
{
    Task<UserInvitationEntity> CreateAsync(string email, string role, string invitedByUserId, CancellationToken ct = default);
    Task<UserInvitationEntity?> ValidateAsync(string token, CancellationToken ct = default);
    Task<ConnapseUser> AcceptAsync(string token, string password, CancellationToken ct = default);
    Task<IReadOnlyList<UserInvitationEntity>> ListPendingAsync(CancellationToken ct = default);
    Task<bool> RevokeAsync(Guid id, CancellationToken ct = default);
}
// Token: SHA-256 hashed GUID stored in user_invitations table. 7-day expiry.
// Accept: creates user account, assigns role, marks invitation used.
```

---

## Connector & Cloud Interfaces (Connapse.Core + Connapse.Storage — v0.3.0)

### IConnector

```csharp
public interface IConnector
{
    ConnectorType Type { get; }
    bool SupportsLiveWatch { get; }
    Task<Stream> ReadFileAsync(string path, CancellationToken ct = default);
    Task WriteFileAsync(string path, Stream content, string? contentType = null, CancellationToken ct = default);
    Task DeleteFileAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default);
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);
    IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default);
}
// Implementations: MinioConnector, FilesystemConnector, S3Connector, AzureBlobConnector
```

### IConnectorFactory

```csharp
public interface IConnectorFactory
{
    IConnector Create(Container container);
}
// Implementation: ConnectorFactory (Singleton)
```

### IContainerSettingsResolver

```csharp
public interface IContainerSettingsResolver
{
    Task<ChunkingSettings> GetChunkingSettingsAsync(Guid containerId, CancellationToken ct = default);
    Task<EmbeddingSettings> GetEmbeddingSettingsAsync(Guid containerId, CancellationToken ct = default);
    Task<SearchSettings> GetSearchSettingsAsync(Guid containerId, CancellationToken ct = default);
    Task<UploadSettings> GetUploadSettingsAsync(Guid containerId, CancellationToken ct = default);
}
// Implementation: ContainerSettingsResolver — merges global settings with per-container overrides
```

### ILlmProvider

```csharp
public interface ILlmProvider
{
    string Provider { get; }
    string ModelId { get; }
    Task<string> CompleteAsync(string systemPrompt, string userPrompt,
        LlmCompletionOptions? options = null, CancellationToken ct = default);
    IAsyncEnumerable<string> StreamAsync(string systemPrompt, string userPrompt,
        LlmCompletionOptions? options = null, CancellationToken ct = default);
}
public record LlmCompletionOptions(float? Temperature = null, int? MaxTokens = null);
// Implementations: OllamaLlmProvider, OpenAiLlmProvider, AzureOpenAiLlmProvider, AnthropicLlmProvider
// DI: factory delegate reads LlmSettings.Provider at scope time
```

### ICloudScopeService

```csharp
public interface ICloudScopeService
{
    Task<CloudScopeResult?> GetScopesAsync(Guid userId, Container container, CancellationToken ct = default);
}

public record CloudScopeResult(bool HasAccess, IReadOnlyList<string> AllowedPrefixes, string? Error = null)
{
    public static CloudScopeResult Deny(string reason);
    public static CloudScopeResult Allow(IReadOnlyList<string> prefixes);
    public static CloudScopeResult FullAccess();
    public bool IsPathAllowed(string path);
}
// Implementation: CloudScopeService (in Connapse.Web)
```

### ICloudIdentityProvider

```csharp
public interface ICloudIdentityProvider
{
    CloudProvider Provider { get; }
    Task<CloudScopeResult> DiscoverScopesAsync(CloudIdentityData identityData, Container container, CancellationToken ct = default);
}
public enum CloudProvider { AWS = 0, Azure = 1 }
// Implementations: AwsIdentityProvider, AzureIdentityProvider (in Connapse.Storage/CloudScope)
```

### IConnectorScopeCache

```csharp
public interface IConnectorScopeCache
{
    Task<CloudScopeResult?> GetAsync(Guid userId, Guid containerId);
    Task SetAsync(Guid userId, Guid containerId, CloudScopeResult result, TimeSpan ttl);
    void Invalidate(Guid userId, Guid containerId);
}
// Implementation: ConnectorScopeCache (IMemoryCache, 15-min allow TTL, 5-min deny TTL)
```

### ICloudIdentityService

```csharp
public interface ICloudIdentityService
{
    Task<CloudIdentityDto?> GetAsync(Guid userId, CloudProvider provider, CancellationToken ct = default);
    Task<IReadOnlyList<CloudIdentityDto>> ListAsync(Guid userId, CancellationToken ct = default);
    Task<bool> DisconnectAsync(Guid userId, CloudProvider provider, CancellationToken ct = default);
    AzureConnectResult GetAzureConnectUrl(string baseUrl);
    Task<CloudIdentityDto> HandleAzureCallbackAsync(Guid userId, string code, string codeVerifier, string redirectUri, CancellationToken ct = default);
    Task<AwsDeviceAuthStartResult> StartAwsDeviceAuthAsync(CancellationToken ct = default);
    Task<CloudIdentityDto?> PollAwsDeviceAuthAsync(Guid userId, string deviceCode, CancellationToken ct = default);
    bool IsAwsSsoConfigured();
    bool IsAzureAdConfigured();
}
// Implementation: CloudIdentityService (in Connapse.Identity)
```

### IAwsSsoClientRegistrar

```csharp
public interface IAwsSsoClientRegistrar
{
    Task<AwsSsoSettings> EnsureRegisteredAsync(AwsSsoSettings settings, CancellationToken ct = default);
    Task<AwsDeviceAuthorizationResult> StartDeviceAuthorizationAsync(AwsSsoSettings settings, CancellationToken ct = default);
    Task<string?> PollForTokenAsync(AwsSsoSettings settings, string deviceCode, CancellationToken ct = default);
    Task<AwsSsoUserInfo> ListUserAccountsAsync(AwsSsoSettings settings, string accessToken, CancellationToken ct = default);
}
// Implementation: AwsSsoClientRegistrar (in Connapse.Storage)
// Device authorization flow: RegisterClient → StartDeviceAuthorization → poll CreateToken
```

### VectorModelDiscovery

```csharp
// Not an interface, but a key v0.3.0 service
public record EmbeddingModelInfo(string ModelId, int Dimensions, long VectorCount);
// VectorModelDiscovery: GetModelsAsync (GROUP BY model_id), HasLegacyVectorsAsync
```

---

## Settings Types

All settings are records with `{ get; set; }` properties for form binding.

```csharp
public record EmbeddingSettings { Provider, Model, Dimensions, BaseUrl, ApiKey, AzureDeploymentName, BatchSize, TimeoutSeconds }
public record ChunkingSettings  { Strategy, MaxChunkSize, Overlap, MinChunkSize, SemanticThreshold, RecursiveSeparators, RespectDocumentStructure }
public record SearchSettings    { Mode, TopK, Reranker, RrfK, VectorWeight, MinimumScore (default 0.5), CrossEncoderModel, EnableQueryExpansion, EnableCrossModelSearch (v0.3.0) }
public record LlmSettings       { Provider, Model, BaseUrl, ApiKey, AzureDeploymentName, Temperature, MaxTokens, TimeoutSeconds, SystemPrompt }
public record UploadSettings    { MaxFileSizeMb, AllowedExtensions, DefaultPath, ParallelWorkers, EnableVirusScanning, AutoStartIngestion, BatchSize }
// WebSearchSettings — REMOVED in v0.3.0
// StorageSettings   — REMOVED in v0.3.0
```

**Settings Configuration Categories:**
- `Knowledge:Embedding` -> EmbeddingSettings
- `Knowledge:Chunking` -> ChunkingSettings
- `Knowledge:Search` -> SearchSettings
- `Knowledge:LLM` -> LlmSettings
- `Knowledge:Upload` -> UploadSettings
- `Knowledge:WebSearch` -> ~~WebSearchSettings~~ (REMOVED in v0.3.0)
- `Knowledge:Storage` -> ~~StorageSettings~~ (REMOVED in v0.3.0)
- `Identity:AwsSso` -> AwsSsoSettings (v0.3.0)
- `Identity:AzureAd` -> AzureAdSettings (v0.3.0)

---

## REST API Endpoints

All endpoints (except `POST /api/v1/auth/token` and `POST /api/v1/auth/token/refresh`) require authentication.

### Auth Endpoints (`/api/v1/auth`)

| Method | Path | Auth Required | Role | Description |
|--------|------|---------------|------|-------------|
| `POST` | `/api/v1/auth/token` | No | — | Email+password → JWT TokenResponse |
| `POST` | `/api/v1/auth/token/refresh` | No | — | Refresh token → new token pair (rotation) |
| `GET` | `/api/v1/auth/pats` | Yes | Any | List authenticated user's PATs |
| `POST` | `/api/v1/auth/pats` | Yes | Any | Create PAT (returns raw token once) |
| `DELETE` | `/api/v1/auth/pats/{id}` | Yes | Any (own) | Revoke PAT |
| `GET` | `/api/v1/auth/users` | Yes | Admin | List all users with roles |
| `PUT` | `/api/v1/auth/users/{id}/roles` | Yes | Admin | Assign roles (Owner and Agent roles protected) |

### Agent Endpoints (`/api/v1/agents`)

| Method | Path | Auth Required | Role | Description |
|--------|------|---------------|------|-------------|
| `GET` | `/api/v1/agents` | Yes | Admin | List all agents |
| `POST` | `/api/v1/agents` | Yes | Admin | Create agent |
| `GET` | `/api/v1/agents/{id}` | Yes | Admin | Get agent |
| `PUT` | `/api/v1/agents/{id}/status` | Yes | Admin | Enable/disable agent |
| `DELETE` | `/api/v1/agents/{id}` | Yes | Admin | Delete agent + all keys |
| `GET` | `/api/v1/agents/{id}/keys` | Yes | Admin | List agent API keys |
| `POST` | `/api/v1/agents/{id}/keys` | Yes | Admin | Create agent API key (returns raw token once) |
| `DELETE` | `/api/v1/agents/{agentId}/keys/{keyId}` | Yes | Admin | Revoke agent API key |

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
| `GET` | `/api/containers/{id}/search/models` | Get embedding models with vectors in this container (v0.3.0). |

### Container Connector Operations (v0.3.0)

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/containers/test-connection` | Test S3/AzureBlob connector config before creating. Body: `TestConnectorConfigRequest`. |
| `POST` | `/api/containers/{id}/sync` | Sync files from remote connector (S3/AzureBlob/MinIO). Returns 400 for Filesystem. |
| `GET` | `/api/containers/{id}/settings` | Get per-container settings overrides. |
| `PUT` | `/api/containers/{id}/settings` | Save per-container settings overrides. |

### Container Reindex

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/containers/{id}/reindex` | Reindex documents in container. Body: `{ force?, detectSettingsChanges?, strategy? }`. |

### Settings

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/settings/{category}` | Get settings for category. |
| `PUT` | `/api/settings/{category}` | Update settings. Triggers live reload. |
| `POST` | `/api/settings/test-connection` | Test connectivity before saving. |
| `GET` | `/api/settings/embedding-models` | Get all embedding models with vectors globally (v0.3.0). |
| `POST` | `/api/settings/reindex` | Trigger re-embedding (fire-and-forget) (v0.3.0). |
| `GET` | `/api/settings/reindex/status` | Get reindex queue depth status (v0.3.0). |

### Cloud Identity Endpoints (v0.3.0)

| Method | Path | Auth Required | Role | Description |
|--------|------|---------------|------|-------------|
| `GET` | `/api/v1/auth/cloud/identities` | Yes | Any | List user's linked cloud identities |
| `GET` | `/api/v1/auth/cloud/azure/connect` | Yes | Any | Redirect to Azure AD authorize endpoint (OAuth2+PKCE) |
| `GET` | `/api/v1/auth/cloud/azure/callback` | Yes | Any | Azure AD OAuth2 callback handler |
| `POST` | `/api/v1/auth/cloud/aws/device-auth` | Yes | Any | Start AWS IAM Identity Center device authorization flow |
| `POST` | `/api/v1/auth/cloud/aws/device-auth/poll` | Yes | Any | Poll for AWS device authorization completion |
| `DELETE` | `/api/v1/auth/cloud/{provider}` | Yes | Any | Disconnect cloud identity (aws or azure) |

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

Binary: `connapse` (Connapse.CLI project)

Distribution:
- .NET Global Tool: `dotnet tool install -g Connapse.CLI`
- Native self-contained binaries: win-x64, linux-x64, osx-x64, osx-arm64 (GitHub Releases)
- Credentials stored: `~/.connapse/credentials.json` (`{ apiKey, apiBaseUrl, userEmail }`)

```bash
# Authentication (required before other commands)
connapse auth login [--url <server>]   # prompts email+password → creates PAT → saves credentials
connapse auth logout                   # deletes credentials file
connapse auth whoami                   # shows current identity + verifies token against server
connapse auth pat create <name> [--expires <date>]  # creates PAT, displays once
connapse auth pat list                 # lists all PATs with status
connapse auth pat revoke <guid>        # revokes a PAT

# Container management
connapse container create <name> [--description "..."]
connapse container list
connapse container delete <name>

# File operations
connapse upload <path> --container <name> [--destination /folder/] [--strategy Semantic]
connapse search "<query>" --container <name> [--mode Hybrid] [--top 10] [--path /folder/] [--min-score 0.5]
connapse reindex --container <name> [--force] [--no-detect-changes]
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
| `container_delete` | `containerId` (required) | Delete an empty container |
| `upload_file` | `containerId` (required), `fileName` (required), `content` (required, base64), `path?`, `strategy?` | Upload file to container |
| `list_files` | `containerId` (required), `path?` | List files/folders in container |
| `delete_file` | `containerId` (required), `fileId` (required) | Delete file from container |
| `get_document` | `containerId` (required), `fileId` (required, UUID or path) | Retrieve full document text content |
| `search_knowledge` | `query` (required), `containerId` (required), `path?`, `mode?`, `topK?`, `minScore?` | Search within container |

All tools accept container name or ID for `containerId` (resolved via name lookup).

---

## Breaking Changes

### 2026-03-02 -- Connectors, Cloud Identity, Multi-Provider AI (v0.3.0)

**Change**: Containers now require a `ConnectorType`. Cloud identity linking (AWS IAM Identity Center, Azure AD) enables IAM-derived scope enforcement on cloud-backed containers. Multiple embedding and LLM providers supported. Unconstrained vector column replaces fixed `vector(768)`.

**Migration**:
- `POST /api/containers` now accepts `connectorType` and `connectorConfig` fields (defaults to `MinIO` for backward compatibility)
- `WebSearchSettings` and `StorageSettings` removed — `PUT /api/settings/websearch` and `PUT /api/settings/storage` no longer exist
- `SearchSettings.IncludeWebSearch` removed; `SearchSettings.EnableCrossModelSearch` added
- `EmbeddingSettings.ApiKey` and `EmbeddingSettings.AzureDeploymentName` added for non-Ollama providers
- Vector column changed from `vector(768)` to unconstrained `vector` — existing data preserved, partial IVFFlat indexes created per model_id
- `SearchMode.Agentic` was implemented then intentionally removed — only `Semantic`, `Keyword`, `Hybrid` remain
- New settings categories: `awssso`, `azuread`, `llm` (LLM was existing but now fully wired)

**New tables added** (AppDbContext migration):
- `user_cloud_identities` (linked cloud identities with DataProtection-encrypted data)

### 2026-02-26 -- Authentication Required on All Endpoints (v0.2.0)

**Change**: All API endpoints now require authentication. Previously all endpoints were publicly accessible.

**Migration**:
- All REST API clients must include `Authorization: Bearer <jwt>` or `X-Api-Key: cnp_<token>`
- SignalR connections must include JWT via `?access_token=<token>` query string or cookie
- MCP server requires an Agent API key via `X-Api-Key`
- CLI: run `connapse auth login` to authenticate and store credentials locally
- First-time setup: visit the login page — if no users exist, a setup form appears
- Integration test infrastructure: seed admin via `CONNAPSE_ADMIN_EMAIL` / `CONNAPSE_ADMIN_PASSWORD` + `Identity:Jwt:Secret` env vars; obtain JWT via `POST /api/v1/auth/token` in test setup

**New tables added** (separate `ConnapseIdentityDbContext` migration history):
- `users`, `roles`, `user_roles`, `user_claims`, `role_claims`, `user_logins`, `user_tokens`
- `personal_access_tokens`, `refresh_tokens`, `audit_logs`, `user_invitations`
- `agents`, `agent_api_keys`

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
