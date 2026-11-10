using System.Data.Common;
using System.Text.RegularExpressions;
using Connapse.Core;
using NpgsqlTypes;

namespace Connapse.Storage.Tables;

/// <summary>
/// Every piece of DDL for containers' tables (CON-301), the way <c>VectorColumnManager</c> owns the search columns'.
/// Each container that holds tables gets a schema of its own, <c>tables_&lt;container id&gt;</c>, holding one real table per
/// catalog entry. Table and column names are generated (<c>t_&lt;id&gt;</c>, <c>c_&lt;n&gt;</c>), never taken from
/// a file, and are checked against <see cref="SafeIdentifier"/> before they go into SQL, since identifiers can't be
/// query parameters.
/// </summary>
internal static partial class TableDdl
{
    internal const string SchemaPrefix = "tables_";
    internal const string TablePrefix = "t_";
    internal const string ColumnPrefix = "c_";

    /// <summary>PostgreSQL's limit on a table's columns.</summary>
    internal const int MaxColumns = 1600;

    /// <summary>First key of the two-key advisory locks taken per container; the second is a hash of its id.</summary>
    private const int LockClass = 301;

    /// <summary>How long a container delete waits for the locks on its tables before giving up.</summary>
    private const string DropLockTimeout = "5s";

    [GeneratedRegex("^[a-z0-9_]{1,63}$")]
    private static partial Regex SafeIdentifier();

    internal static string SchemaName(Guid containerId) => SchemaPrefix + containerId.ToString("N");

    internal static string NewTableName() => TablePrefix + Guid.NewGuid().ToString("N");

    /// <summary>The column holding the <paramref name="index"/>th (from 0) field: <c>c_1</c> for the first.</summary>
    internal static string ColumnName(int index) =>
        index >= 0 ? ColumnPrefix + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Quotes a generated name, refusing anything outside <c>[a-z0-9_]</c>.</summary>
    internal static string Quote(string identifier) =>
        SafeIdentifier().IsMatch(identifier)
            ? "\"" + identifier + "\""
            : throw new ArgumentException($"'{identifier}' is not a generated table-layer name.", nameof(identifier));

    internal static string QualifiedName(Guid containerId, string tableName) =>
        Quote(SchemaName(containerId)) + "." + Quote(tableName);

    internal static string SqlType(TableColumnType type) => type switch
    {
        TableColumnType.Text => "text",
        TableColumnType.WholeNumber => "bigint",
        TableColumnType.Number => "numeric",
        TableColumnType.Boolean => "boolean",
        TableColumnType.Date => "date",
        TableColumnType.DateTime => "timestamp",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    internal static NpgsqlDbType CopyType(TableColumnType type) => type switch
    {
        TableColumnType.Text => NpgsqlDbType.Text,
        TableColumnType.WholeNumber => NpgsqlDbType.Bigint,
        TableColumnType.Number => NpgsqlDbType.Numeric,
        TableColumnType.Boolean => NpgsqlDbType.Boolean,
        TableColumnType.Date => NpgsqlDbType.Date,
        TableColumnType.DateTime => NpgsqlDbType.Timestamp,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    internal static string CreateTableSql(Guid containerId, string tableName, IReadOnlyList<TableColumn> columns) =>
        $"CREATE TABLE {QualifiedName(containerId, tableName)} ("
        + string.Join(", ", columns.Select((c, i) => Quote(ColumnName(i)) + " " + SqlType(c.Type)))
        + ")";

    internal static string CopySql(Guid containerId, string tableName, int columnCount) =>
        $"COPY {QualifiedName(containerId, tableName)} ("
        + string.Join(", ", Enumerable.Range(0, columnCount).Select(i => Quote(ColumnName(i))))
        + ") FROM STDIN (FORMAT BINARY)";

    /// <summary>
    /// Creates the container's schema if it isn't there. Runs inside the caller's transaction, so a container insert
    /// that rolls back takes its schema with it.
    /// </summary>
    internal static Task CreateSchemaAsync(DbConnection connection, Guid containerId, CancellationToken ct) =>
        ExecuteAsync(connection, $"CREATE SCHEMA IF NOT EXISTS {Quote(SchemaName(containerId))}", ct);

    /// <summary>
    /// Drops the container's schema and every table in it, inside the caller's transaction. Waits for loads into the
    /// container to finish first, and gives up after <see cref="DropLockTimeout"/> rather than queue readers behind it.
    /// </summary>
    internal static async Task DropSchemaAsync(DbConnection connection, Guid containerId, CancellationToken ct)
    {
        await LockContainerAsync(connection, containerId, shared: false, ct);
        await ExecuteAsync(connection, $"SET LOCAL lock_timeout = '{DropLockTimeout}'", ct);
        await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {Quote(SchemaName(containerId))} CASCADE", ct);
    }

    /// <summary>
    /// Takes the container's transaction-scoped advisory lock: shared for a load, exclusive for dropping the
    /// container, so a load never puts a table in a schema that is being dropped.
    /// </summary>
    internal static Task LockContainerAsync(DbConnection connection, Guid containerId, bool shared, CancellationToken ct)
    {
        string function = shared ? "pg_advisory_xact_lock_shared" : "pg_advisory_xact_lock";
        return ExecuteAsync(connection, $"SELECT {function}({LockClass}, hashtext('{containerId:N}'))", ct);
    }

    internal static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
