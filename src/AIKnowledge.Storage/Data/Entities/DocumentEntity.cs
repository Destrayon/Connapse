namespace AIKnowledge.Storage.Data.Entities;

public class DocumentEntity
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public string? CollectionId { get; set; }
    public string VirtualPath { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int ChunkCount { get; set; }
    public string Status { get; set; } = "Pending";
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastIndexedAt { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();

    // Navigation properties
    public List<ChunkEntity> Chunks { get; set; } = [];
    public List<ChunkVectorEntity> ChunkVectors { get; set; } = [];
    public List<BatchDocumentEntity> BatchDocuments { get; set; } = [];
}
