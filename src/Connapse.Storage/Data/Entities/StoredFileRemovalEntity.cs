namespace Connapse.Storage.Data.Entities;

/// <summary>A stored file owed a removal because the record pointing to it was deleted.</summary>
public class StoredFileRemovalEntity
{
    public Guid Id { get; set; }

    /// <summary>Not a foreign key: the file stays in storage after its container is deleted, so the entry does too.</summary>
    public Guid ContainerId { get; set; }
    public string Path { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}
