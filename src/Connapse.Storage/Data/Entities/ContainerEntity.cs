namespace Connapse.Storage.Data.Entities;

public class ContainerEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public List<DocumentEntity> Documents { get; set; } = [];
    public List<FolderEntity> Folders { get; set; } = [];
}
