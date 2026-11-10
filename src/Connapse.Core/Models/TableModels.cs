namespace Connapse.Core;

/// <summary>
/// A column's type in a tables container (CON-E6), named as Frictionless Table Schema names it. Each maps to one
/// PostgreSQL type: text, bigint, numeric, boolean, date and timestamp.
/// </summary>
public enum TableColumnType
{
    Text,
    WholeNumber,
    Number,
    Boolean,
    Date,
    DateTime,
}

/// <summary>One column of a table: its original header, its type, and an optional description.</summary>
public sealed record TableColumn(string Name, TableColumnType Type, string? Description = null);

/// <summary>A cell that failed its column's type during a load, kept rather than silently stored as empty.</summary>
public sealed record TableReject(long Row, int Column, string Value, string Reason);

/// <summary>
/// What to load into a table: its name and columns as the file had them, and its rows. Each row holds one value per
/// column, in column order; null is an empty cell. Values must already have their column's type.
/// </summary>
public sealed record TableLoad(
    string Name,
    IReadOnlyList<TableColumn> Columns,
    IAsyncEnumerable<IReadOnlyList<object?>> Rows,
    Guid? DocumentId = null,
    string? Description = null,
    bool NeedsReview = false,
    IReadOnlyList<TableReject>? Rejects = null);

/// <summary>
/// A table's catalog entry. <see cref="StorageName"/> is the generated name of the PostgreSQL table holding its rows
/// right now; it changes on every reload, so a reader resolves it from the catalog each time. <see cref="Version"/>
/// counts loads, starting at 1.
/// </summary>
public sealed record TableInfo(
    Guid Id,
    Guid ContainerId,
    string Name,
    string? Description,
    Guid? DocumentId,
    IReadOnlyList<TableColumn> Columns,
    long RowCount,
    bool NeedsReview,
    IReadOnlyList<TableReject> Rejects,
    string StorageSchema,
    string StorageName,
    int Version,
    DateTime CreatedAt,
    DateTime LoadedAt);
