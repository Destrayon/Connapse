using NpgsqlTypes;

namespace Connapse.Storage.Data.Entities;

/// <summary>One fact of agent memory. Ended or superseded, never edited; deleted only by forgetting its chain.</summary>
public class MemoryFactEntity
{
    public Guid Id { get; set; }
    public Guid ChainId { get; set; }
    public Guid ContainerId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Attribute { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
    public Guid? ReplacesId { get; set; }
    public string? Reason { get; set; }
    public Guid? AuthorUserId { get; set; }
    public Guid? AuthorAgentId { get; set; }
    public string? AuthorName { get; set; }

    /// <summary>A <see cref="Connapse.Core.MemoryScope"/>: 0 personal, 1 team.</summary>
    public short Scope { get; set; }

    /// <summary>When the fact should be checked again (CON-340); null: never. Moved by a confirm, never by a read.</summary>
    public DateTimeOffset? CheckBy { get; set; }

    /// <summary>The days a confirm sets <see cref="CheckBy"/> to from then.</summary>
    public int? CheckAfterDays { get; set; }

    /// <summary>When someone last confirmed the fact still holds.</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }
    public NpgsqlTsVector SearchVector { get; set; } = null!;
    public List<MemoryFactSourceEntity> Sources { get; set; } = [];
}

/// <summary>A space's default check-by period for one attribute, kept in lower case (CON-340).</summary>
public class MemoryCheckDefaultEntity
{
    public Guid ContainerId { get; set; }
    public string Attribute { get; set; } = string.Empty;
    public int Days { get; set; }
}

/// <summary>One source a fact cites: a document id or a link node id.</summary>
public class MemoryFactSourceEntity
{
    public Guid FactId { get; set; }
    public int Ordinal { get; set; }
    public short Kind { get; set; }
    public string Ref { get; set; } = string.Empty;
}
