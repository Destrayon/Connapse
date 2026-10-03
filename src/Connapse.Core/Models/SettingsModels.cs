using System.ComponentModel.DataAnnotations;

namespace Connapse.Core;

/// <summary>
/// Embedding provider settings.
/// </summary>
public record EmbeddingSettings
{
    /// <summary>
    /// Provider type: Ollama | OpenAI | AzureOpenAI | Anthropic
    /// </summary>
    public string Provider { get; set; } = "Ollama";

    /// <summary>
    /// Model identifier (e.g., "nomic-embed-text", "text-embedding-3-small").
    /// </summary>
    public string Model { get; set; } = "nomic-embed-text";

    /// <summary>
    /// Embedding vector dimensions (must match model output).
    /// </summary>
    public int Dimensions { get; set; } = 768;

    /// <summary>
    /// Base URL for Ollama embedding service.
    /// Defaults come from appsettings.json or environment variables — no hardcoded default
    /// so that Docker deployments can override via Knowledge__Embedding__BaseUrl.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// True puts the model's published instruction before each search query and stored chunk
    /// (nomic-embed-text's "search_query: " / "search_document: ", for example); models without one
    /// are unaffected. False uses <see cref="QueryPrefix"/> and <see cref="DocumentPrefix"/> instead.
    /// A switch rather than null-means-default, because saved settings turn null into "".
    /// Changing prefixes changes the vectors, so documents are re-embedded on the next reindex.
    /// </summary>
    public bool UseModelPrefixes { get; set; } = true;

    /// <summary>Text put before every search query when <see cref="UseModelPrefixes"/> is false; empty for none.</summary>
    public string? QueryPrefix { get; set; }

    /// <summary>Text put before every stored chunk when <see cref="UseModelPrefixes"/> is false; empty for none.</summary>
    public string? DocumentPrefix { get; set; }

    /// <summary>
    /// Most embedding requests ingestion may have in flight at once. Kept below what the provider
    /// can serve so searches never queue behind a large sync. Raise it for a hosted provider.
    /// </summary>
    [Range(1, 256)]
    public int MaxConcurrentIngestionRequests { get; set; } = 2;

    /// <summary>Most query embedding requests in flight at once.</summary>
    [Range(1, 256)]
    public int MaxConcurrentQueryRequests { get; set; } = 8;

    /// <summary>
    /// API key — legacy shared field, kept for backward compatibility.
    /// Prefer the provider-specific key properties below.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// OpenAI API key.
    /// </summary>
    public string? OpenAiApiKey { get; set; }

    /// <summary>
    /// OpenAI base URL override (for proxies / compatible endpoints).
    /// </summary>
    public string? OpenAiBaseUrl { get; set; }

    /// <summary>
    /// Azure OpenAI resource endpoint (e.g., https://your-resource.openai.azure.com).
    /// </summary>
    public string? AzureEndpoint { get; set; }

    /// <summary>
    /// Azure OpenAI API key.
    /// </summary>
    public string? AzureApiKey { get; set; }

    /// <summary>
    /// Azure-specific deployment name (for AzureOpenAI).
    /// </summary>
    public string? AzureDeploymentName { get; set; }

    /// <summary>
    /// Batch size for embedding requests (default: 16).
    /// </summary>
    public int BatchSize { get; set; } = 16;

    /// <summary>
    /// Request timeout in seconds (default: 300). Local Ollama may need several minutes
    /// for large batches or cold model loads; cloud providers are typically much faster.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// Chunking strategy settings.
/// </summary>
public record ChunkingSettings
{
    /// <summary>
    /// Chunking strategy: Semantic | FixedSize | Recursive | DocumentAware | SentenceWindow | Record
    /// </summary>
    public string Strategy { get; set; } = "Semantic";

    /// <summary>
    /// Maximum chunk size in tokens (default: 512).
    /// </summary>
    public int MaxChunkSize { get; set; } = 512;

    /// <summary>
    /// Token overlap between consecutive chunks (default: 50).
    /// </summary>
    public int Overlap { get; set; } = 50;

    /// <summary>
    /// Minimum chunk size in tokens (default: 100). Behavior depends on strategy:
    /// RecursiveChunker, SemanticChunker, and SentenceAwareFixedSizeChunker merge
    /// sub-min chunks into a neighbour. FixedSizeChunker may still filter the last
    /// sub-min chunk (tracked as a parity follow-up). SentenceWindowChunker
    /// intentionally bypasses MinChunkSize — its chunks are designed to be
    /// sentence-sized; the wider window text lives in metadata.
    /// </summary>
    public int MinChunkSize { get; set; } = 100;

    /// <summary>
    /// For Semantic chunking: similarity threshold for splitting (0.0-1.0, default: 0.5).
    /// </summary>
    public double SemanticThreshold { get; set; } = 0.5;

    /// <summary>
    /// For Semantic chunking: number of sentences on each side included in each
    /// sentence's embedded context window. Default 1 (so each sentence is embedded
    /// with one sentence of context on each side — a 3-sentence window). Smooths
    /// noise on short or stylistically-different individual sentences.
    /// </summary>
    public int SemanticBufferSize { get; set; } = 1;

    /// <summary>
    /// For Semantic chunking: method used to compute the adaptive breakpoint
    /// threshold from observed pairwise *distances* (1 - cosine similarity).
    /// Percentile splits where distance > Nth percentile (default N=95).
    /// StandardDeviation splits at mean + N*σ. InterQuartile at mean + N*IQR.
    /// Gradient splits where distance is changing fastest (useful for highly
    /// correlated domains where absolute distances stay low — legal/medical).
    /// </summary>
    public string SemanticBreakpointMethod { get; set; } = "Percentile";

    /// <summary>
    /// For Semantic chunking: parameter for the breakpoint method. For
    /// Percentile and Gradient, this is the percentile (0-100; default 95 matches
    /// LangChain). For StandardDeviation, the σ multiplier (default 3).
    /// For InterQuartile, the IQR multiplier (default 1.5).
    /// </summary>
    public double SemanticBreakpointAmount { get; set; } = 95;

    /// <summary>
    /// For Recursive chunking: separators in order of preference.
    /// </summary>
    public string[] RecursiveSeparators { get; set; } = ["\n\n", "\n", ". ", " "];

    /// <summary>
    /// For DocumentAware (Markdown) chunking: prepend the header breadcrumb
    /// (e.g., "Engineering > Deploy > Rollback") to the chunk body before
    /// embedding. Default true — biggest single retrieval-quality win at
    /// ~10-30 token tax per chunk. Sets Metadata["OffsetEstimated"]="true"
    /// since prepended text isn't source-verbatim.
    /// </summary>
    public bool PrependHeaderPath { get; set; } = true;

    /// <summary>
    /// For SentenceWindow chunking: number of sentences on each side of the
    /// indexed sentence to include in Metadata["window"] (total window = 2N+1).
    /// Default 3 matches LlamaIndex's SentenceWindowNodeParser default.
    /// </summary>
    public int SentenceWindowSize { get; set; } = 3;
}

/// <summary>
/// Search configuration settings.
/// </summary>
public record SearchSettings
{
    /// <summary>
    /// Search mode: Vector | Keyword | Hybrid
    /// </summary>
    public string Mode { get; set; } = "Hybrid";

    /// <summary>
    /// Number of results to return (default: 10).
    /// </summary>
    public int TopK { get; set; } = 10;

    /// <summary>
    /// Reranking strategy: None | CrossEncoder
    /// </summary>
    public string Reranker { get; set; } = "None";

    /// <summary>
    /// Semantic weight for Convex Combination fusion (0.0-1.0, default: 0.75).
    /// Higher values favor vector/semantic results, lower values favor keyword results.
    /// At extremes (0 or 1), hits from the zero-weighted source score 0 and may be
    /// filtered by MinimumScore. Clamped to [0,1] at fusion time.
    /// The default was chosen on the 8-domain BEIR dev suite with nomic-embed-text (#552): macro
    /// nDCG@10 peaks at 0.75 on a plateau from 0.65 to 0.8, and leave-one-domain-out picks 0.75 in
    /// 7 of 8 folds. The earlier 0.3 was tuned while Ollama's nomic vectors were degraded by
    /// missing lowercasing (#561), which made dense search look weaker than keyword search.
    /// </summary>
    [Range(0f, 1f)]
    public float FusionAlpha { get; set; } = 0.75f;

    /// <summary>
    /// Hybrid search: candidates each side (vector, keyword) retrieves before fusion (default: 30).
    /// Every pooled candidate is scored on both sides, so a hit only one side found is still
    /// ranked on its real score from the other rather than zero. Never below the requested TopK.
    /// </summary>
    [Range(1, 500)]
    public int HybridCandidatePool { get; set; } = 30;

    /// <summary>
    /// A container's vectors for one model get their own approximate (HNSW) index once there are
    /// at least this many (default: 20,000). Below it, search is exact and already fast; above it,
    /// exact search takes seconds (#571). Indexes are built in the background.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int VectorIndexMinVectors { get; set; } = 20_000;

    /// <summary>
    /// How many candidates an HNSW search keeps (pgvector's hnsw.ef_search, default: 800). Higher
    /// finds more of the true nearest neighbours at some latency; measured on 100k–300k containers
    /// of real 768-dim embeddings: 200 → recall@30 0.94–0.97, 400 → 0.97–0.99, 800 → 0.99–1.00
    /// at p95 ≤ 73 ms (#571).
    /// </summary>
    [Range(1, 1000)]
    public int VectorIndexEfSearch { get; set; } = 800;

    /// <summary>
    /// Keyword ranking function: Bm25 | TsRank (default: Bm25).
    /// Bm25 is Lucene's BM25 computed in plain SQL from per-container statistics, so it runs on any
    /// PostgreSQL, including managed services without search extensions. It falls back to TsRank
    /// for searches not scoped to one container, stop-word-only queries, and containers whose
    /// chunks predate it until the background backfill reaches them.
    /// </summary>
    public string KeywordRanker { get; set; } = "Bm25";

    /// <summary>
    /// BM25 term-frequency saturation (default: 1.2, Lucene's default).
    /// </summary>
    public double Bm25K1 { get; set; } = 1.2;

    /// <summary>
    /// BM25 length normalisation, 0 (none) to 1 (full) (default: 0.75, Lucene's default).
    /// </summary>
    public double Bm25B { get; set; } = 0.75;

    /// <summary>
    /// Fusion method: ConvexCombination | DBSF (default: ConvexCombination).
    /// ConvexCombination: min-max normalizes inputs, then alpha-weighted sum.
    /// DBSF: Distribution-Based Score Fusion — normalizes using mean ± 3σ, more robust to outliers.
    /// </summary>
    public string FusionMethod { get; set; } = "ConvexCombination";

    /// <summary>
    /// Minimum similarity score floor (0.0-1.0, default: 0).
    /// Set to 0 by default — TopK is the primary result limiter.
    /// Raise to filter low-relevance results (e.g., 0.2 to drop noise).
    /// </summary>
    public double MinimumScore { get; set; } = 0;

    /// <summary>
    /// When enabled, automatically trims results after the largest score gap.
    /// Keeps the top cluster of closely-scored results, discarding the long tail.
    /// Applied after MinimumScore filter, before TopK limit.
    /// </summary>
    public bool AutoCut { get; set; } = false;

    /// <summary>
    /// Cross-encoder reranking provider: TEI | Cohere | Jina | AzureAIFoundry
    /// </summary>
    public string CrossEncoderProvider { get; set; } = "TEI";

    /// <summary>
    /// Cross-encoder model name (e.g., "BAAI/bge-reranker-large", "rerank-v3.5", "jina-reranker-v3").
    /// The default is the model the optional Compose <c>reranker</c> service loads; TEI serves
    /// whichever model it was started with, so for TEI this names it rather than selects it.
    /// </summary>
    public string? CrossEncoderModel { get; set; } = "Alibaba-NLP/gte-reranker-modernbert-base";

    /// <summary>
    /// Base URL for self-hosted reranker (TEI) or Azure AI Foundry endpoint.
    /// </summary>
    public string? CrossEncoderBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// API key for cloud reranker providers (Cohere, Jina).
    /// </summary>
    public string? CrossEncoderApiKey { get; set; }

    /// <summary>
    /// Maximum results to return from cross-encoder reranking (0 = no limit, rerank all).
    /// </summary>
    public int CrossEncoderTopN { get; set; } = 0;

    /// <summary>
    /// Top-ranked candidates handed to the cross-encoder after fusion (default: 30). Reranking cost
    /// grows with this; the 2026-09-24 evaluation measured 30 at about 45 ms on a GPU. Never
    /// fewer than the search needs to fill its page.
    /// </summary>
    [Range(1, 500)]
    public int RerankCandidates { get; set; } = 30;

    /// <summary>
    /// Request timeout in seconds for cross-encoder reranking (default: 5). A search waits this long
    /// at most, then returns the un-reranked order.
    /// </summary>
    public int CrossEncoderTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// When true, Semantic searches automatically include keyword results to surface
    /// documents embedded with previous embedding models. Useful during model transitions
    /// before re-embedding completes.
    /// </summary>
    public bool EnableCrossModelSearch { get; set; } = false;

    /// <summary>
    /// When a search hit's metadata carries a "window" key (set by
    /// SentenceWindowChunker), substitute Content with the window text after
    /// reranking, before TopK truncation. Default true. Reranker still scores
    /// against the precise sentence; the wider window is for the answer-time LLM.
    /// </summary>
    public bool SentenceWindowSubstituteOnSearch { get; set; } = true;
}

/// <summary>
/// LLM provider settings for agent interactions.
/// </summary>
public record LlmSettings
{
    /// <summary>
    /// Provider type: Ollama | OpenAI | AzureOpenAI | Anthropic
    /// </summary>
    public string Provider { get; set; } = "Ollama";

    /// <summary>
    /// Model identifier (e.g., "llama3.2", "gpt-4o", "claude-3-5-sonnet-20241022").
    /// </summary>
    public string Model { get; set; } = "llama3.2";

    /// <summary>
    /// Base URL for Ollama LLM service.
    /// Defaults come from appsettings.json or environment variables — no hardcoded default
    /// so that Docker deployments can override via Knowledge__Llm__BaseUrl.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// API key — legacy shared field, kept for backward compatibility.
    /// Prefer the provider-specific key properties below.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// OpenAI API key.
    /// </summary>
    public string? OpenAiApiKey { get; set; }

    /// <summary>
    /// OpenAI base URL override (for proxies / compatible endpoints).
    /// </summary>
    public string? OpenAiBaseUrl { get; set; }

    /// <summary>
    /// Azure OpenAI resource endpoint (e.g., https://your-resource.openai.azure.com).
    /// </summary>
    public string? AzureEndpoint { get; set; }

    /// <summary>
    /// Azure OpenAI API key.
    /// </summary>
    public string? AzureApiKey { get; set; }

    /// <summary>
    /// Azure-specific deployment name (for AzureOpenAI).
    /// </summary>
    public string? AzureDeploymentName { get; set; }

    /// <summary>
    /// Anthropic API key.
    /// </summary>
    public string? AnthropicApiKey { get; set; }

    /// <summary>
    /// Anthropic base URL override (for proxies).
    /// </summary>
    public string? AnthropicBaseUrl { get; set; }

    /// <summary>
    /// Temperature for response generation (0.0-2.0, default: 0.7).
    /// </summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>
    /// Maximum tokens in response (default: 2000).
    /// </summary>
    public int MaxTokens { get; set; } = 2000;

    /// <summary>
    /// Request timeout in seconds (default: 300). LLM calls — especially container
    /// rollups with many-doc input prompts on local Ollama models — routinely exceed
    /// the prior 60s ceiling. 300s matches the EmbeddingSettings default and is well
    /// under the Hangfire job lock timeout (600s for RollupContainerAsync), so the
    /// HTTP call fails before the job times out rather than after.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum number of completion requests issued concurrently to a local Ollama
    /// instance (default: 1). Ollama serializes generation internally, so firing many
    /// requests at once builds a deep queue where each request's wall-clock grows with its
    /// queue position and the slowest ones exceed <see cref="TimeoutSeconds"/>. Gating
    /// concurrency app-side makes callers wait in-process so each request runs alone and
    /// finishes within the timeout. Only applies to the Ollama provider; cloud providers
    /// (OpenAI/Azure/Anthropic) are not gated. Read once at startup — changing it requires
    /// a restart.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 1;

}

/// <summary>
/// Upload and ingestion settings.
/// </summary>
public record UploadSettings
{
    /// <summary>
    /// Number of parallel ingestion workers (default: 4).
    /// </summary>
    public int ParallelWorkers { get; set; } = 4;

    /// <summary>
    /// Longest a single file may spend in its parser before it fails with <c>parse_timeout</c>
    /// (default: 300 seconds). A parser stuck on a malformed file otherwise holds its document in
    /// Processing for good.
    /// </summary>
    public int ParseTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Run the built-in parsers in Connapse.ParserHost processes that can be killed at the deadline
    /// and are capped in memory (default: true). Off, they run inside the web process, where a
    /// parser that ignores its deadline keeps its thread until the process restarts.
    /// </summary>
    public bool IsolateParsers { get; set; } = true;

    /// <summary>
    /// Memory each parser process may use, native libraries included, before its parse fails as
    /// <c>parse_out_of_memory</c> (default: 2,048 MiB). Its managed heap is capped at three quarters
    /// of this; the process as a whole is watched while it parses and killed past the limit.
    /// </summary>
    public int ParserMemoryLimitMb { get; set; } = 2048;

    /// <summary>
    /// Whether parser processes confine themselves with Landlock on Linux (#641): read-only access
    /// to the runtime and the app's code, no settings or data files, no network, no reach into
    /// other processes. "Auto" (default) confines where the kernel allows and warns where it does
    /// not; "Required" refuses to parse without it; "Off" never confines.
    /// </summary>
    public string ParserSandbox { get; set; } = "Auto";

    /// <summary>Files a parser process handles before it is replaced with a fresh one (default: 50).</summary>
    public int ParserFilesPerProcess { get; set; } = 50;

    /// <summary>
    /// OCR PDF pages that have no text layer, or one too garbled to read, on the CPU (default:
    /// true). Off, a PDF with no text at all fails as <c>no_text_layer</c> rather than being
    /// indexed as empty.
    /// </summary>
    public bool PdfOcr { get; set; } = true;

    /// <summary>
    /// Most pages of one PDF that are OCR'd (default: 50). OCR costs seconds a page, so the rest of
    /// a long scan is left unread, with a warning, rather than running past the parse deadline.
    /// </summary>
    public int MaxOcrPagesPerDocument { get; set; } = 50;

    /// <summary>
    /// CPU threads each OCR'd page may use (default: 1). Every ingestion worker can OCR at once, so
    /// OCR can occupy this many cores per worker; one keeps a scan batch from starving search on a
    /// small server. More threads mostly spin rather than speed a page up: measured, a scanned page
    /// took 3.8 s on one thread against 3.6 s on three, and 2.3 to 7 s in a two-CPU container.
    /// </summary>
    public int PdfOcrThreads { get; set; } = 1;

    /// <summary>Resolution PDF pages are rendered at for OCR (default: 200 dpi).</summary>
    public int PdfOcrDpi { get; set; } = 200;

    /// <summary>Largest file that is parsed at all (default: 256 MiB).</summary>
    public long MaxFileBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Most pages a PDF may have (default: 5,000).</summary>
    public int MaxPdfPages { get; set; } = 5000;

    /// <summary>
    /// Most bytes a DOCX or PPTX may expand to (default: 512 MiB). Both formats are ZIP packages,
    /// so a small file can inflate to gigabytes.
    /// </summary>
    public long MaxDecompressedBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>
    /// Highest overall compression ratio a DOCX or PPTX may have once it expands past 10 MiB
    /// (default: 100). Ordinary Office files sit far below it; zip bombs sit far above.
    /// </summary>
    public int MaxCompressionRatio { get; set; } = 100;

    /// <summary>Most characters a parser may extract from one file (default: 20 million).</summary>
    public int MaxExtractedCharacters { get; set; } = 20_000_000;

    /// <summary>
    /// PDF, DOCX and PPTX text with at least this share of unreadable glyphs, replacement or
    /// control characters fails as <c>garbled_text</c> (default: 0.5).
    /// </summary>
    public double GarbledSuspiciousRatio { get; set; } = 0.5;

    /// <summary>
    /// PDF, DOCX and PPTX text with less than this share of letters and digits fails as
    /// <c>garbled_text</c> (default: 0.25).
    /// </summary>
    public double GarbledMinAlphanumericRatio { get; set; } = 0.25;

    /// <summary>
    /// Text with at least this share of unreadable characters is still indexed but carries a
    /// warning (default: 0.1).
    /// </summary>
    public double WarnSuspiciousRatio { get; set; } = 0.1;

    /// <summary>Text shorter than this, in non-whitespace characters, is too short to judge (default: 200).</summary>
    public int MinCharactersForQualityCheck { get; set; } = 200;

    /// <summary>
    /// Most entries a DOCX or PPTX package may list (default: 10,000). Reading a ZIP's directory
    /// allocates per entry, so millions of empty entries exhaust memory before any size check.
    /// </summary>
    public int MaxZipEntries { get; set; } = 10_000;

    public const string DefaultPdfTextMode = "Layout";

    /// <summary>
    /// How PDF pages become text: Raw, ContentOrder, XYCut, Docstrum or Layout (default: Layout).
    /// Layout runs a layout model on each page with a text layer, about 1.6 s a page on one core:
    /// on extract-v1 it lifted olmOCR's header and footer checks from 37% to 88% and table checks
    /// from 20% to 39% over ContentOrder (#642). ContentOrder, the best of the other four, reads a
    /// page in milliseconds; choose it where ingestion speed matters more. See PdfTextMode in
    /// Connapse.Ingestion.
    /// </summary>
    public string PdfTextMode { get; set; } = DefaultPdfTextMode;

    /// <summary>
    /// Drop running headers, footers and page numbers -- text repeating at the edges of the pages
    /// -- in PDFs of three or more pages (default: true).
    /// </summary>
    public bool PdfRemoveRepeatedHeadersAndFooters { get; set; } = true;

    public const string DefaultPdfTableMode = "Ruled";

    /// <summary>
    /// Which PDF tables become Markdown tables: Off, Ruled (drawn with lines) or RuledAndStream
    /// (also borderless tables found from text alignment) (default: Ruled). See PdfTableMode in
    /// Connapse.Ingestion.
    /// </summary>
    public string PdfTableMode { get; set; } = DefaultPdfTableMode;
}

/// <summary>
/// Container/document summary generation settings.
/// Resolution hierarchy (highest wins):
///   per-container SettingsOverridesJson.summary
///   → global DB settings table, key="Summary"
///   → property-initializer defaults below (Enabled=false, others=null)
/// </summary>
public record SummarySettings : IValidatableObject
{
    /// <summary>
    /// Master toggle. When false, both per-doc summaries and container rollups
    /// skip with reason "summaries_disabled" and no LLM call is made.
    /// Default: false (opt-in).
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Container summary generation method. See <see cref="SummaryStrategy"/> for allowed values.
    /// Default: <c>document-clustering</c> — clusters by pooled chunk embeddings and lazy-summarizes
    /// K medoid documents at rollup time. Set to <c>summary-clustering</c> to use the legacy
    /// eager per-doc summarization path.
    /// </summary>
    public string ContainerSummaryMethod { get; init; } = SummaryStrategy.DocumentClustering;

    /// <summary>
    /// LLM provider for summary generation (Ollama / OpenAI / AzureOpenAI / Anthropic).
    /// Null = inherit from instance-level LlmSettings.Provider via SummaryLlmResolver.
    /// </summary>
    public string? LlmProvider { get; init; }

    /// <summary>
    /// Model identifier for summary generation (e.g. "qwen3:14b", "claude-haiku-4-5").
    /// Null = inherit from instance-level LlmSettings.Model. Wired via
    /// LlmCompletionOptions.Model per-call override (no provider re-construction).
    /// </summary>
    public string? LlmModel { get; init; }

    /// <summary>
    /// Override for the per-document system prompt. Null/empty = use
    /// SummaryPrompts.PerDocSystemPrompt. When non-null, the stored string IS
    /// the prompt sent to the LLM — no concatenation, no automatic prefixing.
    /// </summary>
    public string? PerDocSystemPrompt { get; init; }

    /// <summary>
    /// Override for the container-rollup system prompt. Same semantics as
    /// PerDocSystemPrompt; falls back to SummaryPrompts.ContainerRollupSystemPrompt.
    /// </summary>
    public string? ContainerRollupSystemPrompt { get; init; }

    /// <summary>
    /// Max input tokens fed to the per-document LLM call. Null = use default (5_000).
    /// Maps to characters via a 4-char/token heuristic at the truncation site
    /// in PerDocSummarizer (replaces the hardcoded MaxInputCharacters = 20_000).
    /// </summary>
    public int? MaxInputTokens { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!SummaryStrategy.All.Contains(ContainerSummaryMethod))
        {
            yield return new ValidationResult(
                $"ContainerSummaryMethod must be one of: {string.Join(", ", SummaryStrategy.All)}. Got: '{ContainerSummaryMethod}'.",
                new[] { nameof(ContainerSummaryMethod) });
        }
    }
}

