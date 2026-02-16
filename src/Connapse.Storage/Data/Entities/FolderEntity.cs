namespace Connapse.Storage.Data.Entities;

public class FolderEntity
{
    public Guid Id { get; set; }
    public Guid ContainerId { get; set; }
    public string Path { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public ContainerEntity Container { get; set; } = null!;
}
