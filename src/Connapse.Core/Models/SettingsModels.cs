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
    /// Base URL for the embedding service (Ollama or custom endpoint).
    /// </summary>
    public string? BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// API key for cloud providers (OpenAI, Azure, Anthropic).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Azure-specific deployment name (for AzureOpenAI).
    /// </summary>
    public string? AzureDeploymentName { get; set; }

    /// <summary>
    /// Batch size for embedding requests (default: 32).
    /// </summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>
    /// Request timeout in seconds (default: 30).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Chunking strategy settings.
/// </summary>
public record ChunkingSettings
{
    /// <summary>
    /// Chunking strategy: FixedSize | Recursive | Semantic | DocumentAware
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
    /// Minimum chunk size in tokens (default: 100).
    /// </summary>
    public int MinChunkSize { get; set; } = 100;

    /// <summary>
    /// For Semantic chunking: similarity threshold for splitting (0.0-1.0, default: 0.5).
    /// </summary>
    public double SemanticThreshold { get; set; } = 0.5;

    /// <summary>
    /// For Recursive chunking: separators in order of preference.
    /// </summary>
    public string[] RecursiveSeparators { get; set; } = ["\n\n", "\n", ". ", " "];

    /// <summary>
    /// Respect document structure (headings, paragraphs) when chunking.
    /// </summary>
    public bool RespectDocumentStructure { get; set; } = true;
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
    /// Reranking strategy: None | RRF | CrossEncoder
    /// </summary>
    public string Reranker { get; set; } = "RRF";

    /// <summary>
    /// RRF k-value for rank fusion (default: 60).
    /// </summary>
    public int RrfK { get; set; } = 60;

    /// <summary>
    /// For hybrid search: weight of vector search (0.0-1.0, default: 0.7).
    /// Keyword weight = 1.0 - VectorWeight.
    /// </summary>
    public double VectorWeight { get; set; } = 0.7;

    /// <summary>
    /// Minimum similarity score threshold (0.0-1.0, default: 0.5).
    /// Scores below this are filtered out. Lower values return more results.
    /// For nomic-embed-text, relevant results typically score 0.55-0.75.
    /// </summary>
    public double MinimumScore { get; set; } = 0.5;

    /// <summary>
    /// Cross-encoder model for reranking (if Reranker = CrossEncoder).
    /// </summary>
    public string? CrossEncoderModel { get; set; }

    /// <summary>
    /// Enable query expansion (generate alternative queries).
    /// </summary>
    public bool EnableQueryExpansion { get; set; } = false;

    /// <summary>
    /// Include web search results alongside knowledge base results.
    /// </summary>
    public bool IncludeWebSearch { get; set; } = false;
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
    /// Base URL for the LLM service (Ollama or custom endpoint).
    /// </summary>
    public string? BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// API key for cloud providers.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Azure-specific deployment name (for AzureOpenAI).
    /// </summary>
    public string? AzureDeploymentName { get; set; }

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

    /// <summary>
    /// System prompt prefix for agent interactions.
    /// </summary>
    public string? SystemPrompt { get; set; }
}

/// <summary>
/// Upload and ingestion settings.
/// </summary>
public record UploadSettings
{
    /// <summary>
    /// Maximum file size in MB (default: 100).
    /// </summary>
    public int MaxFileSizeMb { get; set; } = 100;

    /// <summary>
    /// Allowed file extensions (empty = all allowed).
    /// </summary>
    public string[] AllowedExtensions { get; set; } = [".txt", ".md", ".pdf", ".docx", ".pptx", ".csv"];

    /// <summary>
    /// Default virtual path for uploads (default: "/uploads").
    /// </summary>
    public string DefaultPath { get; set; } = "/uploads";

    /// <summary>
    /// Number of parallel ingestion workers (default: 4).
    /// </summary>
    public int ParallelWorkers { get; set; } = 4;

    /// <summary>
    /// Enable virus scanning on upload (requires ClamAV or similar).
    /// </summary>
    public bool EnableVirusScanning { get; set; } = false;

    /// <summary>
    /// Automatically start ingestion after upload (default: true).
    /// </summary>
    public bool AutoStartIngestion { get; set; } = true;

    /// <summary>
    /// Batch size for bulk operations (default: 100).
    /// </summary>
    public int BatchSize { get; set; } = 100;
}

/// <summary>
/// Web search provider settings.
/// </summary>
public record WebSearchSettings
{
    /// <summary>
    /// Provider type: None | Brave | Serper | Tavily
    /// </summary>
    public string Provider { get; set; } = "None";

    /// <summary>
    /// API key for the web search provider.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Number of web results to retrieve (default: 5).
    /// </summary>
    public int MaxResults { get; set; } = 5;

    /// <summary>
    /// Request timeout in seconds (default: 10).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Enable safe search filtering.
    /// </summary>
    public bool SafeSearch { get; set; } = true;

    /// <summary>
    /// Country/region for search results (ISO 3166-1 alpha-2, e.g., "US").
    /// </summary>
    public string? Region { get; set; }
}

/// <summary>
/// Storage backend settings.
/// </summary>
public record StorageSettings
{
    /// <summary>
    /// Vector store provider: SqliteVec | PgVector | Qdrant | Pinecone | AzureAISearch
    /// </summary>
    public string VectorStoreProvider { get; set; } = "PgVector";

    /// <summary>
    /// Document store provider: Postgres | MongoDB
    /// </summary>
    public string DocumentStoreProvider { get; set; } = "Postgres";

    /// <summary>
    /// File storage provider: Local | MinIO | AzureBlob | S3
    /// </summary>
    public string FileStorageProvider { get; set; } = "MinIO";

    /// <summary>
    /// MinIO/S3 endpoint (e.g., "localhost:9000").
    /// </summary>
    public string? MinioEndpoint { get; set; } = "localhost:9000";

    /// <summary>
    /// MinIO/S3 access key.
    /// </summary>
    public string? MinioAccessKey { get; set; }

    /// <summary>
    /// MinIO/S3 secret key.
    /// </summary>
    public string? MinioSecretKey { get; set; }

    /// <summary>
    /// MinIO/S3 bucket name (default: "aikp-files").
    /// </summary>
    public string MinioBucketName { get; set; } = "aikp-files";

    /// <summary>
    /// Use SSL/TLS for MinIO/S3 connection.
    /// </summary>
    public bool MinioUseSSL { get; set; } = false;

    /// <summary>
    /// Local file system root path (when FileStorageProvider = Local).
    /// </summary>
    public string? LocalStorageRootPath { get; set; } = "knowledge-data";

    /// <summary>
    /// Azure Blob Storage connection string.
    /// </summary>
    public string? AzureBlobConnectionString { get; set; }

    /// <summary>
    /// Azure Blob container name.
    /// </summary>
    public string? AzureBlobContainerName { get; set; }
}
