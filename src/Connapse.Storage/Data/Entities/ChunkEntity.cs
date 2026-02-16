using NpgsqlTypes;

namespace Connapse.Storage.Data.Entities;

public class ChunkEntity
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string Content { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public int TokenCount { get; set; }
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    public NpgsqlTsVector SearchVector { get; set; } = null!;

    // Navigation properties
    public DocumentEntity Document { get; set; } = null!;
    public ChunkVectorEntity? ChunkVector { get; set; }
}
