using Connapse.Core;

namespace Connapse.Storage.Data.Entities;

public class DocumentEntity
{
    public Guid Id { get; set; }
    public Guid ContainerId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public string Path { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int ChunkCount { get; set; }
    public int Generation { get; set; } = 1;
    public string Status { get; set; } = "Pending";
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastIndexedAt { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();

    // Per-document summary (input to container summary rollup)
    public string? Summary { get; set; }
    public DateTime? SummaryGeneratedAt { get; set; }
    public string? SummaryContentHash { get; set; } // raw-file content hash (= content_hash) at time of summary

    // Multi-stage enrichment lifecycle driving UI status pills.
    // Distinct from Status (which tracks the ingestion job's lifecycle string).
    public IngestionState IngestionState { get; set; } = IngestionState.Pending;

    // Navigation properties
    public ContainerEntity Container { get; set; } = null!;
    public List<ChunkEntity> Chunks { get; set; } = [];
    public List<ChunkVectorEntity> ChunkVectors { get; set; } = [];
    public List<BatchDocumentEntity> BatchDocuments { get; set; } = [];
}
