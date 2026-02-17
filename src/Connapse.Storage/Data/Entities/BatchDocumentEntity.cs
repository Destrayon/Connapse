namespace Connapse.Storage.Data.Entities;

public class BatchDocumentEntity
{
    public Guid BatchId { get; set; }
    public Guid DocumentId { get; set; }

    // Navigation properties
    public BatchEntity Batch { get; set; } = null!;
    public DocumentEntity Document { get; set; } = null!;
}
