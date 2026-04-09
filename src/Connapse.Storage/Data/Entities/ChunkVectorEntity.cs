using Pgvector;

namespace Connapse.Storage.Data.Entities;

/// <summary>
/// Represents a stored embedding vector for a single document chunk.
/// </summary>
public class ChunkVectorEntity
{
    public Guid ChunkId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid ContainerId { get; set; }
    public Vector Embedding { get; set; } = null!;
    public string ModelId { get; set; } = string.Empty;
    /// <summary>SHA-256 hex hash of the chunk content, used for embedding cache lookups.</summary>
    public string? ContentHash { get; set; }
    /// <summary>Embedding vector dimensions, used for cache key matching.</summary>
    public int? Dimensions { get; set; }

    // Navigation properties
    public ChunkEntity Chunk { get; set; } = null!;
    public DocumentEntity Document { get; set; } = null!;
}
