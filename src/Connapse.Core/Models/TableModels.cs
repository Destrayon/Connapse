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
/// with the bad cell set to empty. <see cref="DocumentId"/> is the source document the row came from in a table merged
/// from several documents (CON-304), so the reject is shown only to callers who may read that document.
/// </summary>
public sealed record TableReject(long Row, int Column, string Value, string Reason, Guid? DocumentId = null);

/// <summary>
/// What to load into a table: its name and columns as the file had them, and its rows. Each row holds one value per
/// column, in column order; null is an empty cell. Values must already have their column's type.
/// <para>
/// A table merged from several documents (CON-304) leaves <see cref="DocumentId"/> null and lists the documents in
/// <see cref="SourceDocuments"/>. Each of its rows then holds one more value after the columns: the id of the document
/// the row came from, which must be one of <see cref="SourceDocuments"/>. Each row is shown only to callers who may
/// read its document.
/// </para>
/// </summary>
public sealed record TableLoad(
    string Name,
    IReadOnlyList<TableColumn> Columns,
    IAsyncEnumerable<IReadOnlyList<object?>> Rows,
    Guid? DocumentId = null,
    string? Description = null,
    bool NeedsReview = false,
    IReadOnlyList<TableReject>? Rejects = null,
    IReadOnlyList<Guid>? SourceDocuments = null)
{
    /// <summary>True when the table is merged from several documents and each row carries its own.</summary>
    public bool IsMerged => SourceDocuments is { Count: > 0 };
}

/// <summary>
/// A table's catalog entry. <see cref="StorageName"/> is the generated name of the PostgreSQL table holding its rows
/// right now; a reload can change it, so a reader resolves it from the catalog each time. <see cref="Version"/>
/// counts loads, starting at 1. <see cref="SourceDocuments"/> lists the documents of a table merged from several
/// (CON-304); <see cref="HiddenColumns"/> names the columns an admin hid, which no caller can see or use, kept by
/// name across reloads. <see cref="Columns"/> lists every column, hidden or not; <see cref="VisibleColumns"/> doesn't.
/// <para>
/// <see cref="Key"/> is the column that identifies a row across syncs (CON-306), or null when the table has none. A
/// table with a key keeps its history: a re-sync closes the versions that changed or went away and adds the new ones,
/// so the table can be read as Connapse saw it at any time from <see cref="HistoryFrom"/> on. A table without a key
/// replaces its rows on each sync and keeps no history.
/// </para>
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
    DateTime LoadedAt,
    string? Key = null,
    DateTime? HistoryFrom = null,
    IReadOnlyList<Guid>? SourceDocuments = null,
    IReadOnlyList<string>? HiddenColumns = null)
{
    /// <summary>True when the table keeps row history, so it can be read as seen at a time and asked what changed.</summary>
    public bool HasHistory => Key is not null && HistoryFrom is not null;

    /// <summary>True when the table is merged from several documents and each row carries its own.</summary>
    public bool IsMerged => SourceDocuments is { Count: > 0 };

    /// <summary>Whether the column at <paramref name="index"/> in <see cref="Columns"/> is hidden.</summary>
    public bool IsHidden(int index) =>
        HiddenColumns is { Count: > 0 } hidden && hidden.Contains(Columns[index].Name, StringComparer.Ordinal);

    /// <summary>The indexes into <see cref="Columns"/> of the columns callers can see, in column order.</summary>
    public IReadOnlyList<int> VisibleColumnIndexes =>
        Enumerable.Range(0, Columns.Count).Where(i => !IsHidden(i)).ToList();

    public IReadOnlyList<TableColumn> VisibleColumns => VisibleColumnIndexes.Select(i => Columns[i]).ToList();

    /// <summary>
    /// True on a listed copy of a merged table the caller may read only part of: its <see cref="RowCount"/> counts the
    /// readable rows alone, and its <see cref="Version"/> and <see cref="LoadedAt"/> aren't the caller's to know, since
    /// they change when rows the caller can't read do.
    /// </summary>
    public bool Partial { get; init; }

    /// <summary>Whether an admin hid <paramref name="column"/>. Only admins are shown hidden columns at all.</summary>
    public bool Hides(string column) => HiddenColumns?.Contains(column) == true;
}

/// <summary>One page of a container's tables and how many the container holds in all.</summary>
public sealed record TablePage(IReadOnlyList<TableInfo> Items, int Total);

/// <summary>
/// What a caller may know about one table (CON-304): its visible columns, and a row count, sample rows and rejected
/// rows worked out from only the rows the caller may read. Nothing here changes when only rows the caller can't read
/// change, so it carries no load time, version or table-wide count.
/// </summary>
public sealed record TableDescription(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<TableColumn> Columns,
    long RowCount,
    TableQueryResult SampleRows,
    bool NeedsReview,
    IReadOnlyList<TableReject> Rejects);
