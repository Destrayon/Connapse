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

    /// <summary>
    /// The column of a merged table (CON-304) holding each row's source document. It never matches a generated field
    /// name (<c>c_&lt;n&gt;</c>), is never listed to callers, and is what the row policy checks.
    /// </summary>
    internal const string DocumentColumn = "_document";

    /// <summary>
    /// The table's DDL. With <paramref name="merged"/> it also gets <see cref="DocumentColumn"/> (CON-304); with
    /// <paramref name="history"/> it gets <see cref="TableHistory.SeenFrom"/> and <see cref="TableHistory.SeenUntil"/>
    /// (CON-306), the until column <c>'infinity'</c> while a version is current.
    /// </summary>
    internal static string CreateTableSql(
        Guid containerId, string tableName, IReadOnlyList<TableColumn> columns, bool history = false, bool merged = false) =>
        $"CREATE TABLE {QualifiedName(containerId, tableName)} ("
        + string.Join(", ", columns.Select((c, i) => Quote(ColumnName(i)) + " " + SqlType(c.Type)))
        + (merged ? ", " + Quote(DocumentColumn) + " uuid NOT NULL" : "")
        + (history
            ? $", {Quote(TableHistory.SeenFrom)} timestamptz NOT NULL, {Quote(TableHistory.SeenUntil)} timestamptz NOT NULL DEFAULT 'infinity'"
            : "")
        + ")";

    /// <summary>
    /// The binary COPY into the table's columns, then with <paramref name="merged"/> its document column and with
    /// <paramref name="history"/> its seen-from column.
    /// </summary>
    internal static string CopySql(Guid containerId, string tableName, int columnCount, bool history = false, bool merged = false) =>
        $"COPY {QualifiedName(containerId, tableName)} ("
        + string.Join(", ", Enumerable.Range(0, columnCount).Select(i => Quote(ColumnName(i))))
        + (merged ? ", " + Quote(DocumentColumn) : "")
        + (history ? ", " + Quote(TableHistory.SeenFrom) : "")
        + ") FROM STDIN (FORMAT BINARY)";

    /// <summary>The schema holding the per-request tokens and the lookup the row policies call (CON-324).</summary>
    internal const string AuthSchema = "tables_auth";

    /// <summary>The lookup the row policies call: the documents the current request's token may read.</summary>
    internal const string ReadableDocumentsFunction = AuthSchema + ".readable_documents()";

    /// <summary>
    /// The token table and its lookup (CON-324). The table is unlogged, since no token needs to outlive a crash, and
    /// nobody but the main login may touch it. The lookup runs as the main login (<c>SECURITY DEFINER</c>, with a fixed
    /// <c>search_path</c>) and returns the documents of the token bound to this transaction, or none when the token is
    /// missing, unknown or expired.
    /// </summary>
    internal static IReadOnlyList<string> TokenStoreSql { get; } =
    [
        $"CREATE SCHEMA IF NOT EXISTS {AuthSchema}",
        $"REVOKE ALL ON SCHEMA {AuthSchema} FROM PUBLIC",
        $"CREATE UNLOGGED TABLE IF NOT EXISTS {AuthSchema}.requests (token text PRIMARY KEY, documents uuid[] NOT NULL, expires_at timestamptz NOT NULL)",
        $"REVOKE ALL ON {AuthSchema}.requests FROM PUBLIC",
        $"CREATE OR REPLACE FUNCTION {ReadableDocumentsFunction} RETURNS uuid[] LANGUAGE sql STABLE SECURITY DEFINER "
            + "SET search_path = pg_catalog, pg_temp AS $$ SELECT coalesce((SELECT r.documents FROM tables_auth.requests r "
            + "WHERE r.token = nullif(pg_catalog.current_setting('tables_auth.token', true), '') "
            + "AND r.expires_at > pg_catalog.statement_timestamp()), '{}'::uuid[]) $$",
        $"REVOKE ALL ON FUNCTION {ReadableDocumentsFunction} FROM PUBLIC",
    ];

    /// <summary>
    /// Takes the transaction lock that serializes creating or replacing the token store, so two connections never
    /// create the schema or the lookup at once.
    /// </summary>
    internal static Task LockTokenStoreAsync(DbConnection connection, CancellationToken ct) =>
        ExecuteAsync(connection, $"SELECT pg_advisory_xact_lock({LockClass}, hashtext('{AuthSchema}'))", ct);

    /// <summary>
    /// Creates the token store if it's missing, inside the caller's transaction. A table's row policy calls the
    /// lookup, so the store must exist before the first table is built, which can be before any query provisions the
    /// reader login. The lock is only taken when the lookup is missing, so loads don't queue behind each other.
    /// </summary>
    internal static async Task EnsureTokenStoreAsync(DbConnection connection, CancellationToken ct)
    {
        if (await TokenStoreExistsAsync(connection, ct))
            return;
        await LockTokenStoreAsync(connection, ct);
        if (await TokenStoreExistsAsync(connection, ct))
            return;
        foreach (string sql in TokenStoreSql)
            await ExecuteAsync(connection, sql, ct);
    }

    private static async Task<bool> TokenStoreExistsAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_catalog.to_regprocedure('{ReadableDocumentsFunction}') IS NOT NULL";
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    internal const string ContainerRolePrefix = "connapse_tr_";

    /// <summary>
    /// The NOLOGIN role a container's queries run as (CON-324). Roles are cluster-wide, so the name carries a hash of
    /// the database's name as well as the container's id: <c>connapse_tr_&lt;8 hex&gt;_&lt;32 hex&gt;</c>, 53 characters.
    /// </summary>
    internal static string ContainerRoleName(string database, Guid containerId) =>
        ContainerRolePrefix
        + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(database)))[..8]
        + "_" + containerId.ToString("N");

    /// <summary>
    /// Turns row-level security on for a freshly loaded table (CON-324). Runs after the rows are copied, because
    /// <c>COPY FROM</c> is refused on a table with row-level security. <c>FORCE</c> holds the owner (Connapse's main
    /// login) to the policy too, so nothing reads the rows without a request token. The one policy shows every row
    /// when the token's documents include the table's source document and none otherwise; a table with no source
    /// document shows none to anyone. The check is a sub-select on its own, so it runs once per query.
    /// </summary>
    /// <remarks>
    /// A merged table (CON-304) is checked row by row instead: a row shows when its <see cref="DocumentColumn"/> is
    /// among the token's documents. The lookup is still a sub-select of its own, cast so PostgreSQL reads it as an
    /// array rather than <c>= ANY (subquery)</c>, so it runs once per query and not once per row. The column is
    /// indexed for the compiler's own filter on it.
    /// </remarks>
    internal static IReadOnlyList<string> ProtectSql(Guid containerId, string tableName, Guid? documentId, bool merged = false)
    {
        string table = QualifiedName(containerId, tableName);
        string check = merged
            ? $"({Quote(DocumentColumn)} = ANY ((SELECT {ReadableDocumentsFunction})::uuid[]))"
            : documentId is Guid document
                ? $"((SELECT '{document:D}'::uuid = ANY ({ReadableDocumentsFunction})))"
                : "(false)";
        List<string> statements =
        [
            $"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY",
            $"ALTER TABLE {table} FORCE ROW LEVEL SECURITY",
            $"CREATE POLICY readable ON {table} AS PERMISSIVE FOR SELECT TO PUBLIC USING {check}",
        ];
        if (merged)
            statements.Add($"CREATE INDEX ON {table} ({Quote(DocumentColumn)})");
        return statements;
    }

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
        await DropContainerRoleAsync(connection, containerId, ct);
    }

    /// <summary>
    /// Drops the container's reader role, if a query ever made one (CON-324). Its grants on the container's schema went
    /// with the schema; the ones on the token lookup are revoked first, since a role holding privileges can't be dropped.
    /// </summary>
    private static async Task DropContainerRoleAsync(DbConnection connection, Guid containerId, CancellationToken ct)
    {
        string name = ContainerRoleName(connection.Database, containerId);
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = $1)";
            var parameter = exists.CreateParameter();
            parameter.Value = name;
            exists.Parameters.Add(parameter);
            if (await exists.ExecuteScalarAsync(ct) is not true)
                return;
        }

        string role = Quote(name);
        await ExecuteAsync(connection, $"REVOKE ALL ON FUNCTION {ReadableDocumentsFunction} FROM {role}", ct);
        await ExecuteAsync(connection, $"REVOKE ALL ON SCHEMA {Quote(AuthSchema)} FROM {role}", ct);
        await ExecuteAsync(connection, $"DROP ROLE {role}", ct);
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
