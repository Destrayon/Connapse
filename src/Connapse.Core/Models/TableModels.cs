namespace Connapse.Core;

/// <summary>
/// A column's type in a container's table (CON-E6), named as Frictionless Table Schema names it. Each maps to one
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

/// <summary>
/// One column of a table: its original header, its type, an optional description, and how the file wrote its values
/// when that isn't the plain form.
/// </summary>
public sealed record TableColumn(string Name, TableColumnType Type, string? Description = null, TableColumnFormat? Format = null);

/// <summary>
/// How a file wrote a column's values, in Frictionless Table Schema's terms: a date or time <c>format</c> pattern in
/// strptime form (<c>%d/%m/%Y</c>), a number's <c>decimalChar</c> and <c>groupChar</c>, and a boolean's
/// <c>trueValues</c> and <c>falseValues</c>. Null members are the Frictionless defaults.
/// </summary>
public sealed record TableColumnFormat(
    string? Pattern = null,
    string? DecimalChar = null,
    string? GroupChar = null,
    IReadOnlyList<string>? TrueValues = null,
    IReadOnlyList<string>? FalseValues = null);

/// <summary>
/// A row of the file that was not loaded, with why. <see cref="Row"/> is the record's 1-based number in the file, the
/// header included. <see cref="Column"/> is the 1-based column whose cell failed its type, or 0 when the whole row
/// didn't fit, such as one with the wrong number of fields. Rejected rows are listed, never silently dropped or loaded
/// with the bad cell set to empty.
/// </summary>
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

/// <summary>One page of a container's tables and how many the container holds in all.</summary>
public sealed record TablePage(IReadOnlyList<TableInfo> Items, int Total);
