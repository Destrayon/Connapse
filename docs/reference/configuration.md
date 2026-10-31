---
title: Configuration
type: reference
audience: admin
---

# Configuration

Every setting Connapse reads, with its environment variable and default.

## Where settings come from

Connapse reads settings from these places, and a later one wins over an earlier one:

1. `appsettings.json`
2. `appsettings.<Environment>.json`, where the environment is `ASPNETCORE_ENVIRONMENT`
3. User secrets (development only)
4. Environment variables
5. The `settings` table in PostgreSQL, which is what the **Settings** and **Providers** pages save to
6. Command-line arguments

A value saved on the **Settings** page therefore beats the same setting in an environment variable. To go back to the environment variable, reset that settings group on the page or with `DELETE /api/v1/settings/{category}`.

## Environment variable names

An environment variable's name is the setting's section path with each `:` replaced by two underscores. `Knowledge:Embedding:Model` is set as `Knowledge__Embedding__Model`. A list item takes its index as the last part: `Cors__AllowedOrigins__0`.

## Always required

| Env var | What it is | Secret? |
|---------|-----------|---------|
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string. The database needs the `vector` extension. | Yes |
| `Knowledge__Storage__MinIO__Endpoint` | MinIO host and port, such as `minio:9000` | No |
| `Knowledge__Storage__MinIO__AccessKey` | MinIO access key | No |
| `Knowledge__Storage__MinIO__SecretKey` | MinIO secret key | Yes |
| `CONNAPSE_ADMIN_EMAIL` | Email of the Owner account. On start, Connapse creates it if no account has this email, and makes it Owner and Admin if one does. | No |
| `CONNAPSE_ADMIN_PASSWORD` | Password of that Owner account | Yes |

`Identity__Jwt__Secret` is recommended as well: set it to at least 32 random characters. When it is empty, Connapse generates one into the data directory, and tokens stop working if that directory is lost.

## Server

These are read once at startup, so a change needs a restart. None of them is on a settings page.

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `DataDirectory`: Where Connapse keeps generated keys and state | — | `DataDirectory` | string | `"appdata"` | Yes | No |
| `Cors:AllowedOrigins`: Browser origins allowed to call the API and `/mcp`, besides loopback | — | `Cors__AllowedOrigins__0`, `__1`, … | string[] | empty | Yes | No |
| `Mcp:AllowAnonymousDiscovery`: Let MCP clients list tools before signing in | — | `Mcp__AllowAnonymousDiscovery` | bool | `false` | Yes | No |
| `Hangfire:IngestionWorkerCount`: Files ingested at once, and parser processes kept | — | `Hangfire__IngestionWorkerCount` | int | `4` | Yes | No |
| `Hangfire:SummaryWorkerCount`: Summary jobs run at once | — | `Hangfire__SummaryWorkerCount` | int | `1` | Yes | No |
| `Hangfire:MaxPoolSize`: Most database connections the job queue may hold | — | `Hangfire__MaxPoolSize` | int | `30` | Yes | No |
| `Hangfire:DisableServer`: Queue jobs but run none on this instance | — | `Hangfire__DisableServer` | bool | `false` | Yes | No |

### `RateLimiting`

Each limit allows `PermitLimit` requests per `WindowSeconds`. The API limit counts per user, the MCP limit per agent, and the auth limit per IP address; anonymous API calls also count per IP address against the auth limit. A caller over a limit gets `429`.

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `AuthPermitLimit` | — | `RateLimiting__AuthPermitLimit` | int | `10` | Yes | No |
| `AuthWindowSeconds` | — | `RateLimiting__AuthWindowSeconds` | int | `60` | Yes | No |
| `ApiPermitLimit` | — | `RateLimiting__ApiPermitLimit` | int | `200` | Yes | No |
| `ApiWindowSeconds` | — | `RateLimiting__ApiWindowSeconds` | int | `60` | Yes | No |
| `McpPermitLimit` | — | `RateLimiting__McpPermitLimit` | int | `600` | Yes | No |
| `McpWindowSeconds` | — | `RateLimiting__McpWindowSeconds` | int | `60` | Yes | No |

## Knowledge settings

Each group below is a tab on the **Settings** page and a category of `GET` and `PUT /api/v1/settings/{category}`. They take effect without a restart unless the table says otherwise. `Upload:ParallelWorkers` is shown on the page but nothing reads it: the number of files ingested at once is `Hangfire__IngestionWorkerCount`.

### `Knowledge:Embedding`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `Provider`: Provider type: Ollama \| OpenAI \| AzureOpenAI \| Anthropic | **Settings** → **Embedding** | `Knowledge__Embedding__Provider` | string | `"Ollama"` | No | No |
| `Model`: Model identifier (e.g., "nomic-embed-text", "text-embedding-3-small"). | **Settings** → **Embedding** | `Knowledge__Embedding__Model` | string | `"nomic-embed-text"` | No | No |
| `Dimensions`: Embedding vector dimensions (must match model output). | **Settings** → **Embedding** | `Knowledge__Embedding__Dimensions` | int | `768` | No | No |
| `BaseUrl`: Base URL for Ollama embedding service. | **Settings** → **Embedding** | `Knowledge__Embedding__BaseUrl` | string? | `"http://localhost:11434"` | No | No |
| `UseModelPrefixes`: True puts the model's published instruction before each search query and stored chunk (nomic-embed-text's "search_query: " / "search_document: ", for example); models without one are unaffected. | **Settings** → **Embedding** | `Knowledge__Embedding__UseModelPrefixes` | bool | `true` | No | No |
| `QueryPrefix`: Text put before every search query when `UseModelPrefixes` is false; empty for none. | **Settings** → **Embedding** | `Knowledge__Embedding__QueryPrefix` | string? | — | No | No |
| `DocumentPrefix`: Text put before every stored chunk when `UseModelPrefixes` is false; empty for none. | **Settings** → **Embedding** | `Knowledge__Embedding__DocumentPrefix` | string? | — | No | No |
| `MaxConcurrentIngestionRequests`: Most embedding requests ingestion may have in flight at once. | **Settings** → **Embedding** | `Knowledge__Embedding__MaxConcurrentIngestionRequests` | int | `2` | No | No |
| `MaxConcurrentQueryRequests`: Most query embedding requests in flight at once. | **Settings** → **Embedding** | `Knowledge__Embedding__MaxConcurrentQueryRequests` | int | `8` | No | No |
| `ApiKey`: API key — legacy shared field, kept for backward compatibility. | **Settings** → **Embedding** | `Knowledge__Embedding__ApiKey` | string? | — | No | Yes |
| `OpenAiApiKey`: OpenAI API key. | **Settings** → **Embedding** | `Knowledge__Embedding__OpenAiApiKey` | string? | — | No | Yes |
| `OpenAiBaseUrl`: OpenAI base URL override (for proxies / compatible endpoints). | **Settings** → **Embedding** | `Knowledge__Embedding__OpenAiBaseUrl` | string? | — | No | No |
| `AzureEndpoint`: Azure OpenAI resource endpoint (e.g., https://your-resource.openai.azure.com). | **Settings** → **Embedding** | `Knowledge__Embedding__AzureEndpoint` | string? | — | No | No |
| `AzureApiKey`: Azure OpenAI API key. | **Settings** → **Embedding** | `Knowledge__Embedding__AzureApiKey` | string? | — | No | Yes |
| `AzureDeploymentName`: Azure-specific deployment name (for AzureOpenAI). | **Settings** → **Embedding** | `Knowledge__Embedding__AzureDeploymentName` | string? | — | No | No |
| `BatchSize`: Batch size for embedding requests (default: 16). | **Settings** → **Embedding** | `Knowledge__Embedding__BatchSize` | int | `16` | No | No |
| `TimeoutSeconds`: Request timeout in seconds (default: 300). | **Settings** → **Embedding** | `Knowledge__Embedding__TimeoutSeconds` | int | `300` | No | No |

### `Knowledge:Chunking`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `Strategy`: Chunking strategy: Semantic \| FixedSize \| Recursive \| DocumentAware \| SentenceWindow \| Record | **Settings** → **Chunking** | `Knowledge__Chunking__Strategy` | string | `"Semantic"` | No | No |
| `MaxChunkSize`: Maximum chunk size in tokens (default: 512). | **Settings** → **Chunking** | `Knowledge__Chunking__MaxChunkSize` | int | `512` | No | No |
| `Overlap`: Token overlap between consecutive chunks (default: 50). | **Settings** → **Chunking** | `Knowledge__Chunking__Overlap` | int | `50` | No | No |
| `MinChunkSize`: Minimum chunk size in tokens (default: 100). | **Settings** → **Chunking** | `Knowledge__Chunking__MinChunkSize` | int | `100` | No | No |
| `SemanticThreshold`: For Semantic chunking: similarity threshold for splitting (0.0-1.0, default: 0.5). | **Settings** → **Chunking** | `Knowledge__Chunking__SemanticThreshold` | double | `0.5` | No | No |
| `SemanticBufferSize`: For Semantic chunking: number of sentences on each side included in each sentence's embedded context window. | **Settings** → **Chunking** | `Knowledge__Chunking__SemanticBufferSize` | int | `1` | No | No |
| `SemanticBreakpointMethod`: For Semantic chunking: method used to compute the adaptive breakpoint threshold from observed pairwise *distances* (1 - cosine similarity). | **Settings** → **Chunking** | `Knowledge__Chunking__SemanticBreakpointMethod` | string | `"Percentile"` | No | No |
| `SemanticBreakpointAmount`: For Semantic chunking: parameter for the breakpoint method. | **Settings** → **Chunking** | `Knowledge__Chunking__SemanticBreakpointAmount` | double | `95` | No | No |
| `RecursiveSeparators`: For Recursive chunking: separators in order of preference. | **Settings** → **Chunking** | `Knowledge__Chunking__RecursiveSeparators` | string[] | `["\n\n", "\n", ". ", " "]` | No | No |
| `PrependHeaderPath`: For DocumentAware (Markdown) chunking: prepend the header breadcrumb (e.g., "Engineering > Deploy > Rollback") to the chunk body before embedding. | **Settings** → **Chunking** | `Knowledge__Chunking__PrependHeaderPath` | bool | `true` | No | No |
| `PrependDocumentTitle`: Prepend the document's title (the parser's title, else a file name with a letter in it) as its own line to every chunk, so later chunks of a long document still say what they belong to, for keyword and vector search alike. | **Settings** → **Chunking** | `Knowledge__Chunking__PrependDocumentTitle` | bool | `true` | No | No |
| `SentenceWindowSize`: For SentenceWindow chunking: number of sentences on each side of the indexed sentence to include in Metadata["window"] (total window = 2N+1). | **Settings** → **Chunking** | `Knowledge__Chunking__SentenceWindowSize` | int | `3` | No | No |

### `Knowledge:Search`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `Mode`: Search mode: Vector \| Keyword \| Hybrid | **Settings** → **Search** | `Knowledge__Search__Mode` | string | `"Hybrid"` | No | No |
| `TopK`: Number of results to return (default: 10). | **Settings** → **Search** | `Knowledge__Search__TopK` | int | `10` | No | No |
| `Reranker`: Reranking strategy: None \| CrossEncoder | **Settings** → **Search** | `Knowledge__Search__Reranker` | string | `"None"` | No | No |
| `FusionAlpha`: Semantic weight for Convex Combination fusion (0.0-1.0, default: 0.65). | **Settings** → **Search** | `Knowledge__Search__FusionAlpha` | float | `0.65` | No | No |
| `HybridCandidatePool`: Hybrid search: candidates each side (vector, keyword) retrieves before fusion (default: 30). | **Settings** → **Search** | `Knowledge__Search__HybridCandidatePool` | int | `30` | No | No |
| `VectorIndexMinVectors`: A container's vectors for one model get their own approximate (HNSW) index once there are at least this many (default: 20,000). | **Settings** → **Search** | `Knowledge__Search__VectorIndexMinVectors` | int | `20,000` | No | No |
| `VectorIndexEfSearch`: How many candidates an HNSW search keeps (pgvector's hnsw.ef_search, default: 800). | **Settings** → **Search** | `Knowledge__Search__VectorIndexEfSearch` | int | `800` | No | No |
| `KeywordRanker`: Keyword ranking function: Bm25 \| TsRank (default: Bm25). | **Settings** → **Search** | `Knowledge__Search__KeywordRanker` | string | `"Bm25"` | No | No |
| `Bm25K1`: BM25 term-frequency saturation (default: 1.2, Lucene's default). | **Settings** → **Search** | `Knowledge__Search__Bm25K1` | double | `1.2` | No | No |
| `Bm25B`: BM25 length normalisation, 0 (none) to 1 (full) (default: 0.75, Lucene's default). | **Settings** → **Search** | `Knowledge__Search__Bm25B` | double | `0.75` | No | No |
| `FusionMethod`: Fusion method: ConvexCombination \| DBSF (default: ConvexCombination). | **Settings** → **Search** | `Knowledge__Search__FusionMethod` | string | `"ConvexCombination"` | No | No |
| `MinimumScore`: Minimum similarity score floor (0.0-1.0, default: 0). | **Settings** → **Search** | `Knowledge__Search__MinimumScore` | double | `0` | No | No |
| `AutoCut`: When enabled, automatically trims results after the largest score gap. | **Settings** → **Search** | `Knowledge__Search__AutoCut` | bool | `false` | No | No |
| `CrossEncoderProvider`: Cross-encoder reranking provider: TEI \| Cohere \| Jina \| AzureAIFoundry | **Settings** → **Search** | `Knowledge__Search__CrossEncoderProvider` | string | `"TEI"` | No | No |
| `CrossEncoderModel`: Cross-encoder model name (e.g., "BAAI/bge-reranker-large", "rerank-v3.5", "jina-reranker-v3"). | **Settings** → **Search** | `Knowledge__Search__CrossEncoderModel` | string? | `"Alibaba-NLP/gte-reranker-modernbert-base"` | No | No |
| `CrossEncoderBaseUrl`: Base URL for self-hosted reranker (TEI) or Azure AI Foundry endpoint. | **Settings** → **Search** | `Knowledge__Search__CrossEncoderBaseUrl` | string? | `"http://localhost:8080"` | No | No |
| `CrossEncoderApiKey`: API key for cloud reranker providers (Cohere, Jina). | **Settings** → **Search** | `Knowledge__Search__CrossEncoderApiKey` | string? | — | No | Yes |
| `CrossEncoderTopN`: Maximum results to return from cross-encoder reranking (0 = no limit, rerank all). | **Settings** → **Search** | `Knowledge__Search__CrossEncoderTopN` | int | `0` | No | No |
| `RerankCandidates`: Top-ranked candidates handed to the cross-encoder after fusion (default: 30). | **Settings** → **Search** | `Knowledge__Search__RerankCandidates` | int | `30` | No | No |
| `CrossEncoderTimeoutSeconds`: Request timeout in seconds for cross-encoder reranking (default: 5). | **Settings** → **Search** | `Knowledge__Search__CrossEncoderTimeoutSeconds` | int | `5` | No | No |
| `EnableCrossModelSearch`: When true, Semantic searches automatically include keyword results to surface documents embedded with previous embedding models. | **Settings** → **Search** | `Knowledge__Search__EnableCrossModelSearch` | bool | `false` | No | No |
| `SentenceWindowSubstituteOnSearch`: When a search hit's metadata carries a "window" key (set by SentenceWindowChunker), substitute Content with the window text after reranking, before TopK truncation. | **Settings** → **Search** | `Knowledge__Search__SentenceWindowSubstituteOnSearch` | bool | `true` | No | No |

### `Knowledge:LLM`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `Provider`: Provider type: Ollama \| OpenAI \| AzureOpenAI \| Anthropic | **Settings** → **LLM** | `Knowledge__LLM__Provider` | string | `"Ollama"` | No | No |
| `Model`: Model identifier (e.g., "llama3.2", "gpt-4o", "claude-3-5-sonnet-20241022"). | **Settings** → **LLM** | `Knowledge__LLM__Model` | string | `"llama3.2:3b"` | No | No |
| `BaseUrl`: Base URL for Ollama LLM service. | **Settings** → **LLM** | `Knowledge__LLM__BaseUrl` | string? | — | No | No |
| `ApiKey`: API key — legacy shared field, kept for backward compatibility. | **Settings** → **LLM** | `Knowledge__LLM__ApiKey` | string? | — | No | Yes |
| `OpenAiApiKey`: OpenAI API key. | **Settings** → **LLM** | `Knowledge__LLM__OpenAiApiKey` | string? | — | No | Yes |
| `OpenAiBaseUrl`: OpenAI base URL override (for proxies / compatible endpoints). | **Settings** → **LLM** | `Knowledge__LLM__OpenAiBaseUrl` | string? | — | No | No |
| `AzureEndpoint`: Azure OpenAI resource endpoint (e.g., https://your-resource.openai.azure.com). | **Settings** → **LLM** | `Knowledge__LLM__AzureEndpoint` | string? | — | No | No |
| `AzureApiKey`: Azure OpenAI API key. | **Settings** → **LLM** | `Knowledge__LLM__AzureApiKey` | string? | — | No | Yes |
| `AzureDeploymentName`: Azure-specific deployment name (for AzureOpenAI). | **Settings** → **LLM** | `Knowledge__LLM__AzureDeploymentName` | string? | — | No | No |
| `AnthropicApiKey`: Anthropic API key. | **Settings** → **LLM** | `Knowledge__LLM__AnthropicApiKey` | string? | — | No | Yes |
| `AnthropicBaseUrl`: Anthropic base URL override (for proxies). | **Settings** → **LLM** | `Knowledge__LLM__AnthropicBaseUrl` | string? | — | No | No |
| `Temperature`: Temperature for response generation (0.0-2.0, default: 0.7). | **Settings** → **LLM** | `Knowledge__LLM__Temperature` | double | `0.7` | No | No |
| `MaxTokens`: Maximum tokens in response (default: 2000). | **Settings** → **LLM** | `Knowledge__LLM__MaxTokens` | int | `2000` | No | No |
| `TimeoutSeconds`: Request timeout in seconds (default: 300). | **Settings** → **LLM** | `Knowledge__LLM__TimeoutSeconds` | int | `300` | No | No |
| `MaxConcurrentRequests`: Maximum number of completion requests issued concurrently to a local Ollama instance (default: 1). | **Settings** → **LLM** | `Knowledge__LLM__MaxConcurrentRequests` | int | `1` | Yes | No |

### `Knowledge:Upload`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `ParallelWorkers`: Number of parallel ingestion workers (default: 4). | **Settings** → **Upload** | `Knowledge__Upload__ParallelWorkers` | int | `4` | No | No |
| `ParseTimeoutSeconds`: Longest a single file may spend in its parser before it fails with `parse_timeout` (default: 300 seconds). | **Settings** → **Upload** | `Knowledge__Upload__ParseTimeoutSeconds` | int | `300` | No | No |
| `IsolateParsers`: Run the built-in parsers in Connapse.ParserHost processes that can be killed at the deadline and are capped in memory (default: true). | **Settings** → **Upload** | `Knowledge__Upload__IsolateParsers` | bool | `true` | No | No |
| `ParserMemoryLimitMb`: Memory each parser process may use, native libraries included, before its parse fails as `parse_out_of_memory` (default: 2,048 MiB). | **Settings** → **Upload** | `Knowledge__Upload__ParserMemoryLimitMb` | int | `2048` | No | No |
| `ParserSandbox`: Whether parser processes confine themselves with Landlock on Linux: read-only access to the runtime and the app's code, no settings or data files, no network, no reach into other processes. | **Settings** → **Upload** | `Knowledge__Upload__ParserSandbox` | string | `"Auto"` | No | No |
| `ParserFilesPerProcess`: Files a parser process handles before it is replaced with a fresh one (default: 50). | **Settings** → **Upload** | `Knowledge__Upload__ParserFilesPerProcess` | int | `50` | No | No |
| `PdfOcr`: OCR PDF pages that have no text layer, or one too garbled to read, on the CPU (default: true). | **Settings** → **Upload** | `Knowledge__Upload__PdfOcr` | bool | `true` | No | No |
| `MaxOcrPagesPerDocument`: Most pages of one PDF that are OCR'd (default: 50). | **Settings** → **Upload** | `Knowledge__Upload__MaxOcrPagesPerDocument` | int | `50` | No | No |
| `PdfOcrThreads`: CPU threads each OCR'd page may use (default: 1). | **Settings** → **Upload** | `Knowledge__Upload__PdfOcrThreads` | int | `1` | No | No |
| `PdfOcrDpi`: Resolution PDF pages are rendered at for OCR (default: 200 dpi). | **Settings** → **Upload** | `Knowledge__Upload__PdfOcrDpi` | int | `200` | No | No |
| `MaxFileBytes`: Largest file that is parsed at all (default: 256 MiB). | **Settings** → **Upload** | `Knowledge__Upload__MaxFileBytes` | long | `256 MiB` | No | No |
| `MaxPdfPages`: Most pages a PDF may have (default: 5,000). | **Settings** → **Upload** | `Knowledge__Upload__MaxPdfPages` | int | `5000` | No | No |
| `MaxDecompressedBytes`: Most bytes a DOCX or PPTX may expand to (default: 512 MiB). | **Settings** → **Upload** | `Knowledge__Upload__MaxDecompressedBytes` | long | `512 MiB` | No | No |
| `MaxCompressionRatio`: Highest overall compression ratio a DOCX or PPTX may have once it expands past 10 MiB (default: 100). | **Settings** → **Upload** | `Knowledge__Upload__MaxCompressionRatio` | int | `100` | No | No |
| `MaxExtractedCharacters`: Most characters a parser may extract from one file (default: 20 million). | **Settings** → **Upload** | `Knowledge__Upload__MaxExtractedCharacters` | int | `20,000,000` | No | No |
| `GarbledSuspiciousRatio`: PDF, DOCX and PPTX text with at least this share of unreadable glyphs, replacement or control characters fails as `garbled_text` (default: 0.5). | **Settings** → **Upload** | `Knowledge__Upload__GarbledSuspiciousRatio` | double | `0.5` | No | No |
| `GarbledMinAlphanumericRatio`: PDF, DOCX and PPTX text with less than this share of letters and digits fails as `garbled_text` (default: 0.25). | **Settings** → **Upload** | `Knowledge__Upload__GarbledMinAlphanumericRatio` | double | `0.25` | No | No |
| `WarnSuspiciousRatio`: Text with at least this share of unreadable characters is still indexed but carries a warning (default: 0.1). | **Settings** → **Upload** | `Knowledge__Upload__WarnSuspiciousRatio` | double | `0.1` | No | No |
| `MinCharactersForQualityCheck`: Text shorter than this, in non-whitespace characters, is too short to judge (default: 200). | **Settings** → **Upload** | `Knowledge__Upload__MinCharactersForQualityCheck` | int | `200` | No | No |
| `MaxZipEntries`: Most entries a DOCX or PPTX package may list (default: 10,000). | **Settings** → **Upload** | `Knowledge__Upload__MaxZipEntries` | int | `10,000` | No | No |
| `PdfTextMode`: How PDF pages become text: Raw, ContentOrder, XYCut, Docstrum or Layout (default: Layout). | **Settings** → **Upload** | `Knowledge__Upload__PdfTextMode` | string | `Layout` | No | No |
| `PdfRemoveRepeatedHeadersAndFooters`: Drop running headers, footers and page numbers -- text repeating at the edges of the pages -- in PDFs of three or more pages (default: true). | **Settings** → **Upload** | `Knowledge__Upload__PdfRemoveRepeatedHeadersAndFooters` | bool | `true` | No | No |
| `PdfTableMode`: Which PDF tables become Markdown tables: Off, Ruled (drawn with lines) or RuledAndStream (also borderless tables found from text alignment) (default: Ruled). | **Settings** → **Upload** | `Knowledge__Upload__PdfTableMode` | string | `Ruled` | No | No |
| `PdfLayoutOnOcrPages`: In the Layout text mode, also read OCR'd pages by layout region: running headers and footers dropped, columns ordered, tables read (default: true, #653). | **Settings** → **Upload** | `Knowledge__Upload__PdfLayoutOnOcrPages` | bool | `true` | No | No |

### `Knowledge:Summary`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `Enabled`: Master toggle. | **Settings** → **Summary** | `Knowledge__Summary__Enabled` | bool | `false` | No | No |
| `ContainerSummaryMethod`: Container summary generation method. | **Settings** → **Summary** | `Knowledge__Summary__ContainerSummaryMethod` | string | `document-clustering` | No | No |
| `LlmProvider`: LLM provider for summary generation (Ollama / OpenAI / AzureOpenAI / Anthropic). | **Settings** → **Summary** | `Knowledge__Summary__LlmProvider` | string? | — | No | No |
| `LlmModel`: Model identifier for summary generation (e.g. | **Settings** → **Summary** | `Knowledge__Summary__LlmModel` | string? | — | No | No |
| `PerDocSystemPrompt`: Override for the per-document system prompt. | **Settings** → **Summary** | `Knowledge__Summary__PerDocSystemPrompt` | string? | — | No | No |
| `ContainerRollupSystemPrompt`: Override for the container-rollup system prompt. | **Settings** → **Summary** | `Knowledge__Summary__ContainerRollupSystemPrompt` | string? | — | No | No |
| `MaxInputTokens`: Max input tokens fed to the per-document LLM call. | **Settings** → **Summary** | `Knowledge__Summary__MaxInputTokens` | int? | — | No | No |

## Sign-in and providers

The settings marked **Providers** are filled in by the setup steps on the **Providers** page, which save them to the settings table; you can also set them as environment variables. The two `Identity:PermissionEnforcement` flags are written by Connapse the first time per-user permissions work, so a later misconfiguration fails closed instead of showing everything. Don't set them yourself.

### `Identity:Jwt`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `Secret`: Key that signs access tokens; at least 32 characters. Generated into the data directory when empty. | — | `Identity__Jwt__Secret` | string? | — | Yes | Yes |
| `AccessTokenLifetimeMinutes`: How long an access token from `POST /api/v1/auth/token` lasts. | — | `Identity__Jwt__AccessTokenLifetimeMinutes` | int | `60` | No | No |
| `RefreshTokenLifetimeDays`: How long a refresh token lasts. | — | `Identity__Jwt__RefreshTokenLifetimeDays` | int | `7` | No | No |
| `Issuer`: The `iss` claim on issued tokens. | — | `Identity__Jwt__Issuer` | string | `"Connapse"` | No | No |
| `Audience`: The `aud` claim on issued tokens. | — | `Identity__Jwt__Audience` | string | `"Connapse"` | No | No |

### `Identity:SamlSignIn`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `EntityId`: Connapse's SAML entity id, which the application registers as its audience. | **Providers** | `Identity__SamlSignIn__EntityId` | string | — | No | No |
| `AcsUrl`: Where Identity Center POSTs the assertion — Connapse's assertion consumer service. | **Providers** | `Identity__SamlSignIn__AcsUrl` | string | — | No | No |
| `IdpEntityId`: Identity Center's entity id, the `Issuer` every assertion must carry. | **Providers** | `Identity__SamlSignIn__IdpEntityId` | string | — | No | No |
| `IdpSingleSignOnUrl`: Where the sign-in request is sent, from the application's metadata. | **Providers** | `Identity__SamlSignIn__IdpSingleSignOnUrl` | string | — | No | No |
| `IdpSigningCertificate`: The base64 X.509 certificate assertions are signed with, from the same metadata. | **Providers** | `Identity__SamlSignIn__IdpSigningCertificate` | string | — | No | No |

### `Identity:IdentityCenter`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `Region`: The single region the instance lives in. | **Providers** | `Identity__IdentityCenter__Region` | string | — | No | No |
| `InstanceArn`: `arn:aws:sso:::instance/ssoins-…`. | **Providers** | `Identity__IdentityCenter__InstanceArn` | string | — | No | No |
| `IdentityStoreId`: `d-…`, the directory a trusted token issuer resolves a user into. | **Providers** | `Identity__IdentityCenter__IdentityStoreId` | string | — | No | No |

### `Identity:PermissionEnforcement`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `IsEnforcing`: True once this deployment has had SAML per-user permissions working. | — | `Identity__PermissionEnforcement__IsEnforcing` | bool | `false` | No | No |
| `AzureEnforcing`: True once this deployment has had Azure AD per-user permissions working. | — | `Identity__PermissionEnforcement__AzureEnforcing` | bool | `false` | No | No |

### `Identity:AzureAd`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `TenantId`: The Entra tenant (directory) id the application is registered under. | **Providers** | `Identity__AzureAd__TenantId` | string? | — | No | No |
| `ClientId`: The application (client) id registered in Entra ID. | **Providers** | `Identity__AzureAd__ClientId` | string? | — | No | No |
| `RedirectUri`: Where Entra redirects back to after sign-in, registered on the application. | **Providers** | `Identity__AzureAd__RedirectUri` | string? | — | No | No |
| `ClientCertificatePath`: Path to the client certificate (PEM or PFX) Connapse authenticates to Entra with. | **Providers** | `Identity__AzureAd__ClientCertificatePath` | string? | — | No | No |
| `ClientCertificatePassword`: Password for the certificate at `ClientCertificatePath`, if it is a password-protected PFX. | **Providers** | `Identity__AzureAd__ClientCertificatePassword` | string? | — | No | Yes |
| `AdminConsentPending`: True while the access app's Microsoft Graph permissions still await an administrator's consent. | **Providers** | `Identity__AzureAd__AdminConsentPending` | bool | `false` | No | No |

### `Providers:Azure`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `TenantId`: The Entra tenant Connapse's own Azure app is registered in. | **Providers** | `Providers__Azure__TenantId` | string? | — | No | No |
| `ClientId`: Connapse's own Azure app (client) id, used by Azure Blob sources. | **Providers** | `Providers__Azure__ClientId` | string? | — | No | No |
| `ClientCertificatePath`: Path to that app's client certificate (PEM or PFX). Connapse never uses a client secret. | **Providers** | `Providers__Azure__ClientCertificatePath` | string? | — | No | No |
| `ClientCertificatePassword`: Password for the certificate, if it is a password-protected PFX. | **Providers** | `Providers__Azure__ClientCertificatePassword` | string? | — | No | Yes |
| `UserAssignedManagedIdentityClientId`: Client id of a user-assigned managed identity to sign in as, when no app is set. | **Providers** | `Providers__Azure__UserAssignedManagedIdentityClientId` | string? | — | No | No |
| `UseHostManagedIdentity`: Sign in as the system-assigned managed identity of the host Connapse runs on. | **Providers** | `Providers__Azure__UseHostManagedIdentity` | bool | `false` | No | No |
| `ManagedIdentityPrincipalId`: The managed identity's service principal object id, recorded by the guided setup so the page can name it and the permissions script can grant to it. | **Providers** | `Providers__Azure__ManagedIdentityPrincipalId` | string? | — | No | No |
| `SubscriptionId`: The subscription whose role/deny assignments the RBAC resolver queries. | **Providers** | `Providers__Azure__SubscriptionId` | string? | — | No | No |

## Sources

`Sources:Security:AllowedFilesystemRoots` can only be set in configuration, never through the API or the settings table. When it is empty, any root is allowed and a warning is logged for each root in use.

### `Sources:Security`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `AllowedFilesystemRoots`: Directories a filesystem connection's `allowedRoot` may name. | — | `Sources__Security__AllowedFilesystemRoots` | IReadOnlyList<string> | `[]` | No | No |

### `Sources:GitHub`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `MirrorDirectory`: Directory holding one bare git mirror per docs source. | — | `Sources__GitHub__MirrorDirectory` | string | `"appdata/github-mirrors"` | No | No |

### `Sources:Atlassian`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `StateDirectory`: Directory holding one page-state store per Confluence source, beside the GitHub mirrors on the `appdata` volume. | — | `Sources__Atlassian__StateDirectory` | string | `"appdata/atlassian-state"` | No | No |

### `CustomSources`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `AllowedHttpHosts`: Hosts a custom source may reach over plain HTTP — a custom source server on the same machine or Docker network, such as `host.docker.internal`. | — | `CustomSources__AllowedHttpHosts` | List<string> | `[]` | No | No |

## File storage

### `Knowledge:Storage:MinIO`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `Endpoint`: MinIO (or S3-compatible) host and port for uploaded files. | — | `Knowledge__Storage__MinIO__Endpoint` | string | `"localhost:9000"` | Yes | No |
| `AccessKey`: MinIO access key. | — | `Knowledge__Storage__MinIO__AccessKey` | string | — | Yes | No |
| `SecretKey`: MinIO secret key. | — | `Knowledge__Storage__MinIO__SecretKey` | string | — | Yes | Yes |
| `UseSSL`: Connect to MinIO over HTTPS. | — | `Knowledge__Storage__MinIO__UseSSL` | bool | `false` | Yes | No |
| `BucketName`: Bucket uploaded files are stored in. | — | `Knowledge__Storage__MinIO__BucketName` | string | `"connapse-files"` | Yes | No |

### `Knowledge:FileSystem`

| Setting | UI path | Env var | Type | Default | Restart? | Secret? |
|---------|---------|---------|------|---------|----------|---------|
| `RootPath`: Root directory for the managed file system. | — | `Knowledge__FileSystem__RootPath` | string | `"knowledge-data"` | Yes | No |

## Related

- [Install Connapse](../self-host/install.md)
- [Run in production](../self-host/production.md)
- [Tune search](../guides/tune-search.md)
- [File types](file-types.md)
- [REST API](rest-api.md)
