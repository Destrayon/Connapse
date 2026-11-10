namespace Connapse.Storage.Data.Entities;

/// <summary>
/// A table in a tables container (CON-301): the catalog entry for one real PostgreSQL table in the container's schema.
/// <see cref="StorageName"/> points at the table holding the rows now; a reload loads a new table and moves the
/// pointer, so readers never see half a load.
/// </summary>
public class ContainerTableEntity
{
    public Guid Id { get; set; }
    public Guid ContainerId { get; set; }
    public Guid? DocumentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string StorageName { get; set; } = string.Empty;

    /// <summary>The columns as a Frictionless Table Schema: <c>{"fields": [{"name", "type", "description"}]}</c>.</summary>
    public string SchemaJson { get; set; } = "{\"fields\":[]}";

    public long RowCount { get; set; }
    public bool NeedsReview { get; set; }

    /// <summary>Cells that failed their column's type, as a JSON array.</summary>
    public string RejectsJson { get; set; } = "[]";

    public int Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LoadedAt { get; set; }
}

/// <summary>
/// A table no catalog entry points at any more, waiting to be dropped. Dropping waits a grace period, so a reader
/// that resolved the old name just before a reload still finds its table.
/// </summary>
public class RetiredTableEntity
{
    public long Id { get; set; }
    public Guid ContainerId { get; set; }
    public string StorageName { get; set; } = string.Empty;
    public DateTime RetiredAt { get; set; }
}
