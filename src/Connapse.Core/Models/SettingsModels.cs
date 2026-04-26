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
    /// Chunking strategy: Semantic | FixedSize | Recursive | DocumentAware | SentenceWindow
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
    /// Semantic weight for Convex Combination fusion (0.0-1.0, default: 0.5).
    /// Higher values favor vector/semantic results, lower values favor keyword results.
    /// At extremes (0 or 1), hits from the zero-weighted source score 0 and may be
    /// filtered by MinimumScore. Clamped to [0,1] at fusion time.
    /// </summary>
    [Range(0f, 1f)]
    public float FusionAlpha { get; set; } = 0.5f;

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
    /// </summary>
    public string? CrossEncoderModel { get; set; }

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
    /// Request timeout in seconds for cross-encoder reranking.
    /// </summary>
    public int CrossEncoderTimeoutSeconds { get; set; } = 30;

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
    /// Request timeout in seconds (default: 60).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;

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
}

