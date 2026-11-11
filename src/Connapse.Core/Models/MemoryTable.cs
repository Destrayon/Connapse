namespace Connapse.Core;

/// <summary>
/// The built-in <c>memory</c> table of a memory container (CON-309): its facts, read through one view over memory's
/// own rows, so nothing is copied. Each row is one version of a fact; by default a read sees the facts true now and
/// not corrected, <c>seen_as_of</c> reads them as true at a time, and a changes request lists the versions that
/// started or ended in a window, corrected ones included, as memory's own tools do. Which rows show is memory's rule:
/// facts citing only sources the caller can read, and team facts or the caller's own.
/// </summary>
public static class MemoryTable
{
    /// <summary>The table's id, the same in every memory container.</summary>
    public static readonly Guid Id = new("6d656d6f-7279-4000-8000-000000000309");

    public const string Name = "memory";

    /// <summary>The view's schema and name; nothing else in the catalog lives in this schema.</summary>
    public const string StorageSchema = "tables_memory";
    public const string StorageName = "facts";

    public static IReadOnlyList<TableColumn> Columns { get; } =
    [
        new("fact", TableColumnType.Text, "This version's id."),
        new("chain", TableColumnType.Text, "The id shared by every version of the fact."),
        new("subject", TableColumnType.Text, "What the fact is about."),
        new("attribute", TableColumnType.Text, "Which property of the subject it states."),
        new("value", TableColumnType.Text, "The value."),
        new("valid_from", TableColumnType.DateTime, "When it became true in the world, UTC; empty if unknown."),
        new("valid_to", TableColumnType.DateTime, "When it stopped being true, UTC; empty while it still is."),
        new("recorded_at", TableColumnType.DateTime, "When memory recorded this version, UTC."),
        new("superseded_at", TableColumnType.DateTime, "When a correction replaced this version, UTC; empty if it wasn't."),
        new("replaces", TableColumnType.Text, "The version this one corrected."),
        new("reason", TableColumnType.Text, "Why the writer recorded or corrected it."),
        new("author", TableColumnType.Text, "Who recorded it."),
        new("scope", TableColumnType.Text, "team or personal."),
        new("sources", TableColumnType.Text, "What it was learned from, as document:<id> or node:<id>."),
    ];

    public const string Description =
        "Agent memory: one row per version of each fact. Reads show the facts true now unless seen_as_of is given; "
        + "changes lists versions that started or ended in a window, corrected ones included.";

    /// <summary>The memory table of <paramref name="containerId"/>, as a catalog entry the query layer can compile.</summary>
    public static TableInfo For(Guid containerId) => new(
        Id, containerId, Name, Description, DocumentId: null, Columns, RowCount: 0, NeedsReview: false, Rejects: [],
        StorageSchema, StorageName, Version: 1, CreatedAt: DateTime.UnixEpoch, LoadedAt: DateTime.UnixEpoch)
    { Partial = true };

    /// <summary>Whether <paramref name="table"/> is a memory table; a catalog entry can never be one.</summary>
    public static bool Is(TableInfo table) =>
        table.Id == Id && table.StorageSchema == StorageSchema && table.StorageName == StorageName;
}
