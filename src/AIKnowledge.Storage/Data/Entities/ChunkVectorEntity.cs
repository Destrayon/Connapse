using Pgvector;

namespace AIKnowledge.Storage.Data.Entities;

public class ChunkVectorEntity
{
    public Guid ChunkId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid ContainerId { get; set; }
    public Vector Embedding { get; set; } = null!;
    public string ModelId { get; set; } = string.Empty;

    // Navigation properties
    public ChunkEntity Chunk { get; set; } = null!;
    public DocumentEntity Document { get; set; } = null!;
}
