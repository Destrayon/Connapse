using Connapse.Core;

namespace Connapse.Storage.Data.Entities;

public class DocumentEntity
{
    public Guid Id { get; set; }

    /// <summary>
    /// Set when this document belongs to a managed container. Exactly one of
    /// ContainerId and SourceId is non-null, enforced by a database CHECK.
    /// </summary>
    public Guid? ContainerId { get; set; }

    /// <summary>
    /// Set when this document belongs to an external source. Exactly one of
    /// ContainerId and SourceId is non-null, enforced by a database CHECK.
    /// </summary>
    public Guid? SourceId { get; set; }

    /// <summary>
    /// Stored generated column: COALESCE(container_id, source_id). Never assign to
    /// this — PostgreSQL computes it. Search filters on it so the query path does
    /// not need to know whether the owner is a container or a source.
    /// </summary>
    public Guid OwnerId { get; private set; }

    public string FileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Where this document came from, absolutely and outside Connapse — <c>s3://bucket/key</c>.
    /// Null for uploads, and for anything not synced since this column existed.
    /// </summary>
    /// <remarks>
    /// Reported by the connector at sync time rather than derived from <see cref="Path"/> and the
    /// source's scope. That derivation is wrong in cases nothing can detect: a source's prefix is
    /// editable with no reconciliation of existing rows, so a source re-pointed after ingestion
    /// leaves paths relative to a prefix no longer on record, and the arithmetic would produce a
    /// confident wrong answer. For a permission filter, a document attributed to the wrong key is
    /// worse than one attributed to none.
    /// <para>
    /// Null is denied once per-user filtering is switched on (#421) — never allowed. An upload has
    /// no external address at all, so a URI scheme built for cloud RBAC cannot describe one.
    /// </para>
    /// </remarks>
    public string? ResourceUri { get; set; }
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
    public ContainerEntity? Container { get; set; }
    public SourceEntity? Source { get; set; }
    public List<ChunkEntity> Chunks { get; set; } = [];
    public List<ChunkVectorEntity> ChunkVectors { get; set; } = [];
    public List<BatchDocumentEntity> BatchDocuments { get; set; } = [];
}
