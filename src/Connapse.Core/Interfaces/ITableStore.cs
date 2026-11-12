namespace Connapse.Core.Interfaces;

/// <summary>
/// The tables of a documents container (CON-E6): real PostgreSQL tables in the container's own schema, with a catalog
/// entry each, held beside the container's files (CON-319). A container holds any number of them. Loads throw
/// <see cref="InvalidOperationException"/> when the container doesn't exist or is a memory container.
/// </summary>
public interface ITableStore
{
    /// <summary>Loads <paramref name="load"/> into a new table. The table appears only once every row is in.</summary>
    Task<TableInfo> CreateAsync(Guid containerId, TableLoad load, CancellationToken ct = default);

    /// <summary>
    /// Replaces a table's rows, columns and description with <paramref name="load"/>. The new rows load into a new
    /// table that is swapped in when complete, so a reader sees the old table or the new one, never part of a load.
    /// Null when the table doesn't exist.
    /// </summary>
    Task<TableInfo?> ReloadAsync(Guid containerId, Guid tableId, TableLoad load, CancellationToken ct = default);

    Task<TableInfo?> GetAsync(Guid containerId, Guid tableId, CancellationToken ct = default);

    /// <summary>The container's tables by name; empty for a container that has none or doesn't exist.</summary>
    Task<IReadOnlyList<TableInfo>> ListAsync(Guid containerId, CancellationToken ct = default);

    /// <summary>
    /// One page of the container's tables, in the same order as <see cref="ListAsync(Guid, CancellationToken)"/>,
    /// with how many it holds in all, so a caller can go through any number of them.
    /// </summary>
    Task<TablePage> ListAsync(Guid containerId, int skip, int take, CancellationToken ct = default);

    /// <summary>The table loaded from <paramref name="documentId"/>, or null when that document has none.</summary>
    Task<TableInfo?> FindByDocumentAsync(Guid containerId, Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// Hides the named columns of a table from every caller (CON-304), replacing the ones hidden before; an empty list
    /// shows them all. Names are the columns' original headers and stay hidden across reloads. Null when the table
    /// doesn't exist; throws <see cref="ArgumentException"/> for a name the table has no column for.
    /// </summary>
    Task<TableInfo?> SetHiddenColumnsAsync(Guid containerId, Guid tableId, IReadOnlyList<string> columns, CancellationToken ct = default);

    /// <summary>Removes a table from the catalog at once; its rows are dropped a little later. False when it doesn't exist.</summary>
    Task<bool> DeleteAsync(Guid containerId, Guid tableId, CancellationToken ct = default);

    /// <summary>Every table loaded from <paramref name="documentId"/>, in the order they sit in it.</summary>
    Task<IReadOnlyList<TableInfo>> ListByDocumentAsync(Guid containerId, Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// Hides a whole table from every caller (CON-312), or shows it again. A hidden table stays hidden across
    /// re-ingests. Null when the table doesn't exist.
    /// </summary>
    Task<TableInfo?> SetHiddenAsync(Guid containerId, Guid tableId, bool hidden, CancellationToken ct = default);

    /// <summary>
    /// Makes the tables found inside <paramref name="documentId"/> (CON-312) match <paramref name="loads"/>, each of
    /// which has a <see cref="TableLoad.Placement"/>, in one transaction. A load whose placement matches one of the
    /// document's tables reloads it, keeping its id and hides; a load matching none is created; a table no load matches
    /// is deleted. The document's other tables, such as one loaded from a CSV file, aren't touched. When anything fails
    /// nothing changes. Returns the tables in load order.
    /// </summary>
    Task<IReadOnlyList<TableInfo>> SyncDocumentTablesAsync(
        Guid containerId, Guid documentId, IReadOnlyList<TableLoad> loads, CancellationToken ct = default);
}

/// <summary>
/// The links from a document's passages to the tables found in them (CON-312). A link is removed with its table, its
/// passage or its document.
/// </summary>
public interface ITablePassageLinks
{
    /// <summary>Replaces every link from <paramref name="documentId"/>'s passages with <paramref name="links"/>.</summary>
    Task ReplaceAsync(Guid containerId, Guid documentId, IReadOnlyList<TablePassageLink> links, CancellationToken ct = default);

    /// <summary>The links from each of <paramref name="chunkIds"/> that has any, each passage's in the order they sit in it.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<TablePassageLink>>> ForChunksAsync(
        IReadOnlyCollection<Guid> chunkIds, CancellationToken ct = default);
}
