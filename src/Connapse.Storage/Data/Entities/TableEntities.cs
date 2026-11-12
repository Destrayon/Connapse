namespace Connapse.Storage.Data.Entities;

/// <summary>
/// A table in a container (CON-301): the catalog entry for one real PostgreSQL table in the container's schema.
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

    /// <summary>The documents a merged table's rows came from (CON-304); null for a table from one document.</summary>
    public Guid[]? SourceDocuments { get; set; }

    /// <summary>The headers of the columns an admin hid from every caller (CON-304).</summary>
    public string[] HiddenColumns { get; set; } = [];

    /// <summary>For a table found inside its document (CON-312): its page, null for a format without pages.</summary>
    public int? Page { get; set; }

    /// <summary>For a table found inside its document (CON-312): its number among the document's tables.</summary>
    public int? TableNumber { get; set; }

    /// <summary>For a table found inside its document (CON-312): the hash of its header.</summary>
    public string? HeaderHash { get; set; }

    /// <summary>True when an admin hid the whole table from every caller (CON-312).</summary>
    public bool Hidden { get; set; }
}

/// <summary>A passage holding all or part of a table found in its document (CON-312), and the span it takes up.</summary>
public class TablePassageLinkEntity
{
    public long Id { get; set; }
    public Guid ContainerId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid TableId { get; set; }
    public Guid ChunkId { get; set; }
    public int Start { get; set; }
    public int Length { get; set; }
}

/// <summary>
/// A table's search card embedded with one embedding model (CON-310). <see cref="TextHash"/> is the hash of the card it
/// was made from; once the card changes (a new description, a column hidden) the vector is stale and isn't used.
/// </summary>
public class TableCardVectorEntity
{
    public Guid TableId { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public Guid ContainerId { get; set; }
    public string TextHash { get; set; } = string.Empty;
    public Pgvector.Vector Embedding { get; set; } = null!;
    public DateTime EmbeddedAt { get; set; }
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
