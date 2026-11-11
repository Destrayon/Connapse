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
}
