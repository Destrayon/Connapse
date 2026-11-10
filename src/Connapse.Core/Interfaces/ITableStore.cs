namespace Connapse.Core.Interfaces;

/// <summary>
/// The tables of a tables container (CON-E6): real PostgreSQL tables in the container's own schema, with a catalog
/// entry each. Throws <see cref="InvalidOperationException"/> when the container doesn't exist or isn't a tables
/// container.
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

    /// <summary>Removes a table from the catalog at once; its rows are dropped a little later. False when it doesn't exist.</summary>
    Task<bool> DeleteAsync(Guid containerId, Guid tableId, CancellationToken ct = default);
}
