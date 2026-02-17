namespace Connapse.Storage.Data.Entities;

public class BatchEntity
{
    public Guid Id { get; set; }
    public int TotalFiles { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public string Status { get; set; } = "Processing";
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Navigation properties
    public List<BatchDocumentEntity> BatchDocuments { get; set; } = [];
}
