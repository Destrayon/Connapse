using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Connapse.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Connapse.Storage.Tables;

/// <summary>
/// The database login agents' table queries run on (CON-303), and its connection pool. Grants are the security
/// boundary: the login itself holds nothing beyond <c>PUBLIC</c>'s rights. Each container has a NOLOGIN reader role
/// (CON-324) holding <c>USAGE</c> on the container's schema, <c>SELECT</c> on its tables and <c>EXECUTE</c> on the
/// token lookup, granted to the login <c>WITH SET TRUE, INHERIT FALSE</c>: a query runs under <c>SET LOCAL ROLE</c>
/// of its own container's role, so even one that escaped read-only mode could not write, read another container's
/// tables or Connapse's own, read server files or run programs. Every table forces row-level security, so the rows
/// a query sees are those of the documents its request token lists (see <see cref="IssueTokenAsync"/>). The login is
/// created lazily by Connapse's main login, under an advisory lock so instances don't race:
/// <list type="bullet">
/// <item>Its name comes from the database name, because roles are cluster-wide and two Connapse databases on one
/// server must not share a reader.</item>
/// <item>Its password is derived from the main login's password, so every instance agrees on it and nothing new is
/// stored. <c>Tables:ReaderPassword</c> overrides it, and is required when the main login has no password.</item>
/// <item>It is refused (fail closed) if a role of that name already has superuser, replication, bypass-RLS,
/// create-role or create-database rights, or belongs to any role other than a container reader role it may only
/// <c>SET ROLE</c> to.</item>
/// <item>Its defaults include a statement timeout, <c>work_mem</c> and read-only transactions, backing up the
/// per-query <c>SET LOCAL</c>s. <c>temp_file_limit</c> needs superuser (or a <c>SET</c> grant) to set per role, which
/// Azure Flexible Server's admin may lack; then a warning asks the operator to set the server parameter.</item>
/// <item>The server must be PostgreSQL 16 or later, for per-grant <c>SET</c> and <c>INHERIT</c> options. Below
/// 16.15, 17.11 or 18.6 a warning names the row-security fixes it lacks (CVE-2026-14666, CVE-2026-14668), and
/// <see cref="RowSecurityPatched"/> is false.</item>
/// <item><c>TEMPORARY</c> on the database is revoked from <c>PUBLIC</c>, so no reader can make temporary objects; the
/// main login keeps it by an explicit grant. Where the main login may not do that, a warning says so.</item>
/// </list>
/// A container's role and grants are made on its first query in this process: <c>USAGE</c> on its schema,
/// <c>SELECT</c> on its tables, and default privileges so tables the main login creates there later are readable too.
/// </summary>
public sealed partial class TableReaderLogin : IAsyncDisposable
{
    internal const string StatementTimeout = "5s";
    internal const int StatementTimeoutSeconds = 5;
    internal const string LockTimeout = "1s";
    internal const string IdleInTransactionTimeout = "10s";
    internal const string WorkMem = "16MB";
    internal const string TempFileLimit = "256MB";

    private const int LockClass = 303;
    private const string RolePrefix = "connapse_tables_reader_";
    private const string InsufficientPrivilege = "42501";

    private readonly NpgsqlDataSource _admin;
    private readonly string? _connectionString;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TableReaderLogin> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _grantedSchemas = new(StringComparer.Ordinal);
    private NpgsqlDataSource? _reader;
    private string? _database;

    public TableReaderLogin(NpgsqlDataSource admin, string? connectionString, IConfiguration configuration, ILogger<TableReaderLogin> logger)
    {
        _admin = admin;
        _connectionString = connectionString;
        _configuration = configuration;
        _logger = logger;
    }

    [GeneratedRegex("[^a-z0-9_]")]
    private static partial Regex UnsafeCharacters();

    /// <summary>The reader role's name, from <c>Tables:ReaderRole</c> or the database name.</summary>
    public string RoleName => _configuration["Tables:ReaderRole"] is { Length: > 0 } configured
        ? configured
        : DefaultRoleName(new NpgsqlConnectionStringBuilder(_connectionString).Database ?? "connapse");

    internal static string DefaultRoleName(string database)
    {
        string name = RolePrefix + UnsafeCharacters().Replace(database.ToLowerInvariant(), "_");
        return name.Length <= 63 ? name : name[..63];
    }

    /// <summary>The reader's password: <c>Tables:ReaderPassword</c>, or an HMAC of the role name keyed by the main password.</summary>
    internal static string DerivePassword(string mainPassword, string roleName) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(mainPassword), Encoding.UTF8.GetBytes("connapse-tables-reader:" + roleName)));

    /// <summary>Opens a pooled connection as the reader, creating the role and its pool first if needed.</summary>
    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        NpgsqlDataSource reader = _reader ?? await ProvisionAsync(ct);
        return await reader.OpenConnectionAsync(ct);
    }

    /// <summary>Whether the server has the row-security fixes of 16.15, 17.11 or 18.6 (known once provisioned).</summary>
    public bool RowSecurityPatched { get; private set; }

    /// <summary>The first patched minor release of each supported major version.</summary>
    private static readonly Dictionary<int, int> PatchedMinor = new() { [16] = 15, [17] = 11, [18] = 6 };

    /// <summary>Whether a server version has the row-security fixes; versions past 18 are assumed to.</summary>
    internal static bool IsRowSecurityPatched(Version version) =>
        PatchedMinor.TryGetValue(version.Major, out int minor) ? version.Minor >= minor : version.Major > 18;

    /// <summary>The NOLOGIN role a container's queries run as. Known once the login is provisioned.</summary>
    internal string ContainerRoleName(Guid containerId) => TableDdl.ContainerRoleName(
        _database ?? throw new InvalidOperationException("The tables reader isn't provisioned yet."), containerId);

    /// <summary>
    /// Gives a container's tables schema its reader role, once per schema per process: creates the NOLOGIN role (or
    /// refuses one of that name with any other rights), grants it to the login for <c>SET ROLE</c> only, and grants it
    /// <c>USAGE</c> on the schema, <c>SELECT</c> on its tables now and later, and the token lookup. Any table in the
    /// schema without forced row-level security gets it with no policy, so it shows no rows until it is reloaded.
    /// Grants the login held directly before CON-324 are revoked.
    /// </summary>
    public async Task EnsureGrantsAsync(Guid containerId, string schema, CancellationToken ct)
    {
        if (_grantedSchemas.ContainsKey(schema))
            return;
        if (schema != TableDdl.SchemaName(containerId))
            throw new InvalidOperationException($"Schema {schema} doesn't belong to container {containerId}.");

        if (_reader is null)
            await ProvisionAsync(ct);

        string login = TableDdl.Quote(RoleName);
        string quotedSchema = TableDdl.Quote(schema);
        await using NpgsqlConnection connection = await _admin.OpenConnectionAsync(ct);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);
        // Shared container lock: never grant on a schema that is being dropped. Then an exclusive lock per schema,
        // because concurrent GRANTs on one object fail with "tuple concurrently updated".
        await TableDdl.LockContainerAsync(connection, containerId, shared: true, ct);
        await ExecuteAsync(connection, transaction, "SELECT pg_catalog.pg_advisory_xact_lock($1, pg_catalog.hashtext($2))", ct, LockClass, schema);

        string role = await EnsureContainerRoleAsync(connection, transaction, containerId, ct);
        await ExecuteAsync(connection, transaction, $"GRANT USAGE ON SCHEMA {quotedSchema} TO {role}", ct);
        await ExecuteAsync(connection, transaction, $"GRANT SELECT ON ALL TABLES IN SCHEMA {quotedSchema} TO {role}", ct);
        await ExecuteAsync(connection, transaction, $"ALTER DEFAULT PRIVILEGES IN SCHEMA {quotedSchema} GRANT SELECT ON TABLES TO {role}", ct);
        await ExecuteAsync(connection, transaction, $"GRANT USAGE ON SCHEMA {TableDdl.Quote(TableDdl.AuthSchema)} TO {role}", ct);
        await ExecuteAsync(connection, transaction, $"GRANT EXECUTE ON FUNCTION {TableDdl.ReadableDocumentsFunction} TO {role}", ct);

        await ExecuteAsync(connection, transaction, $"REVOKE ALL ON ALL TABLES IN SCHEMA {quotedSchema} FROM {login}", ct);
        await ExecuteAsync(connection, transaction, $"ALTER DEFAULT PRIVILEGES IN SCHEMA {quotedSchema} REVOKE ALL ON TABLES FROM {login}", ct);
        await ExecuteAsync(connection, transaction, $"REVOKE ALL ON SCHEMA {quotedSchema} FROM {login}", ct);

        List<string> unprotected = [];
        await using (NpgsqlCommand tables = new(
            "SELECT c.relname FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
            + "WHERE n.nspname = $1 AND c.relkind = 'r' AND NOT (c.relrowsecurity AND c.relforcerowsecurity)", connection, transaction))
        {
            tables.Parameters.Add(new NpgsqlParameter { Value = schema });
            await using NpgsqlDataReader row = await tables.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct))
                unprotected.Add(row.GetString(0));
        }
        foreach (string table in unprotected)
        {
            string name = quotedSchema + "." + TableDdl.Quote(table);
            await ExecuteAsync(connection, transaction, $"ALTER TABLE {name} ENABLE ROW LEVEL SECURITY", ct);
            await ExecuteAsync(connection, transaction, $"ALTER TABLE {name} FORCE ROW LEVEL SECURITY", ct);
        }
        if (unprotected.Count > 0)
            _logger.LogWarning(
                "{Count} tables in {Schema} had no row-level security; they now show no rows until they are reloaded", unprotected.Count, schema);

        await transaction.CommitAsync(ct);
        _grantedSchemas[schema] = true;
    }

    /// <summary>
    /// Gives a memory container its reader role (CON-309), once per container per process: the same NOLOGIN role a
    /// documents container gets, granted to the login for <c>SET ROLE</c> only, with <c>SELECT</c> on the memory view
    /// and nothing on <c>memory_facts</c> itself. The view shows only what the request's memory token allows.
    /// </summary>
    public async Task EnsureMemoryGrantsAsync(Guid containerId, CancellationToken ct)
    {
        string key = MemoryGrantKey(containerId);
        if (_grantedSchemas.ContainsKey(key))
            return;
        if (_reader is null)
            await ProvisionAsync(ct);

        string memory = TableDdl.Quote(TableDdl.MemorySchema);
        await using NpgsqlConnection connection = await _admin.OpenConnectionAsync(ct);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);
        await TableDdl.LockContainerAsync(connection, containerId, shared: true, ct);
        // Every memory container's grants change the one view's privileges, so they take one lock between them.
        await ExecuteAsync(connection, transaction, "SELECT pg_catalog.pg_advisory_xact_lock($1, pg_catalog.hashtext($2))", ct, LockClass, TableDdl.MemorySchema);

        string role = await EnsureContainerRoleAsync(connection, transaction, containerId, ct);
        await ExecuteAsync(connection, transaction, $"GRANT USAGE ON SCHEMA {memory} TO {role}", ct);
        await ExecuteAsync(connection, transaction, $"GRANT SELECT ON {memory}.{TableDdl.Quote(TableDdl.MemoryView)} TO {role}", ct);
        await ExecuteAsync(connection, transaction, $"GRANT USAGE ON SCHEMA {TableDdl.Quote(TableDdl.AuthSchema)} TO {role}", ct);
        await ExecuteAsync(connection, transaction, $"GRANT EXECUTE ON FUNCTION {TableDdl.ReadableDocumentsFunction} TO {role}", ct);
        await transaction.CommitAsync(ct);
        _grantedSchemas[key] = true;
    }

    /// <summary>The grant cache's key for a memory container; it never equals a tables schema's name.</summary>
    public static string MemoryGrantKey(Guid containerId) => TableDdl.MemorySchema + ":" + containerId.ToString("N");

    /// <summary>
    /// Creates the container's NOLOGIN reader role, or refuses one of that name with any other rights or memberships,
    /// and grants it to the login for <c>SET ROLE</c> only. Returns the quoted role name.
    /// </summary>
    private async Task<string> EnsureContainerRoleAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid containerId, CancellationToken ct)
    {
        string login = TableDdl.Quote(RoleName);
        string roleName = ContainerRoleName(containerId);
        string role = TableDdl.Quote(roleName);
        bool exists = false;
        await using (NpgsqlCommand check = new(
            "SELECT r.rolcanlogin OR r.rolsuper OR r.rolreplication OR r.rolbypassrls OR r.rolcreaterole OR r.rolcreatedb, "
            + "EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member = r.oid) "
            + "FROM pg_catalog.pg_roles r WHERE r.rolname = $1", connection, transaction))
        {
            check.Parameters.Add(new NpgsqlParameter { Value = roleName });
            await using NpgsqlDataReader row = await check.ExecuteReaderAsync(ct);
            if (await row.ReadAsync(ct))
            {
                if (row.GetBoolean(0) || row.GetBoolean(1))
                    throw new InvalidOperationException(
                        $"Role {roleName} exists with more rights than reading one container's tables; Connapse won't use it.");
                exists = true;
            }
        }

        if (!exists)
            await ExecuteAsync(connection, transaction,
                $"CREATE ROLE {role} WITH NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS", ct);
        await ExecuteAsync(connection, transaction, $"GRANT {role} TO {login} WITH SET TRUE, INHERIT FALSE", ct);
        return role;
    }

    /// <summary>
    /// Writes a memory request token (CON-309) for one memory container: the document and link-node refs
    /// <paramref name="viewer"/> can read and who the caller is, kept in <c>tables_auth.memory_requests</c>. The memory
    /// view shows the container's facts by memory's own rule against it; the table lookup knows nothing of it.
    /// </summary>
    internal async Task<string> IssueMemoryTokenAsync(Guid containerId, MemoryViewer viewer, TimeSpan lifetime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (_reader is null)
            await ProvisionAsync(ct);

        string[] documents = [.. viewer.Readable.Where(s => s.Kind == MemorySourceKind.Document).Select(s => s.Ref).Distinct(StringComparer.Ordinal)];
        string[] nodes = [.. viewer.Readable.Where(s => s.Kind == MemorySourceKind.Node).Select(s => s.Ref).Distinct(StringComparer.Ordinal)];
        string token = RandomNumberGenerator.GetHexString(32, lowercase: true);
        await using NpgsqlConnection connection = await _admin.OpenConnectionAsync(ct);
        await using NpgsqlBatch batch = new(connection)
        {
            BatchCommands =
            {
                new NpgsqlBatchCommand("DELETE FROM tables_auth.memory_requests WHERE expires_at < pg_catalog.statement_timestamp()"),
                new NpgsqlBatchCommand(
                    "INSERT INTO tables_auth.memory_requests (token, container_id, documents, nodes, user_id, agent_id, expires_at) "
                    + "VALUES ($1, $2, $3, $4, $5, $6, pg_catalog.statement_timestamp() + $7)")
                {
                    Parameters =
                    {
                        new NpgsqlParameter { Value = token },
                        new NpgsqlParameter { Value = containerId },
                        new NpgsqlParameter { Value = documents },
                        new NpgsqlParameter { Value = nodes },
                        new NpgsqlParameter { Value = (object?)viewer.Caller.UserId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid },
                        new NpgsqlParameter { Value = (object?)viewer.Caller.AgentId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid },
                        new NpgsqlParameter { Value = lifetime },
                    },
                },
            },
        };
        await batch.ExecuteNonQueryAsync(ct);
        return token;
    }

    /// <summary>
    /// Writes a request token for <paramref name="documents"/>: 128 random bits, kept in <c>tables_auth.requests</c>,
    /// which no reader role can read or write, and valid for <paramref name="lifetime"/>. A query binds it with
    /// <c>set_config('tables_auth.token', …, true)</c>, and every table's policy shows rows only when the token's
    /// documents include the table's source document. Expired tokens are deleted on the way.
    /// </summary>
    internal async Task<string> IssueTokenAsync(IReadOnlySet<Guid> documents, TimeSpan lifetime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (_reader is null)
            await ProvisionAsync(ct);

        string token = RandomNumberGenerator.GetHexString(32, lowercase: true);
        await using NpgsqlConnection connection = await _admin.OpenConnectionAsync(ct);
        await using NpgsqlBatch batch = new(connection)
        {
            BatchCommands =
            {
                new NpgsqlBatchCommand("DELETE FROM tables_auth.requests WHERE expires_at < pg_catalog.statement_timestamp()"),
                new NpgsqlBatchCommand(
                    "INSERT INTO tables_auth.requests (token, documents, expires_at) VALUES ($1, $2, pg_catalog.statement_timestamp() + $3)")
                {
                    Parameters =
                    {
                        new NpgsqlParameter { Value = token },
                        new NpgsqlParameter { Value = documents.ToArray() },
                        new NpgsqlParameter { Value = lifetime },
                    },
                },
            },
        };
        await batch.ExecuteNonQueryAsync(ct);
        return token;
    }

    /// <summary>Deletes a request token once its query is done. A failure is only logged: the token expires anyway.</summary>
    internal async Task RevokeTokenAsync(string token)
    {
        try
        {
            await using NpgsqlConnection connection = await _admin.OpenConnectionAsync(CancellationToken.None);
            await using NpgsqlBatch batch = new(connection)
            {
                BatchCommands =
                {
                    new NpgsqlBatchCommand("DELETE FROM tables_auth.requests WHERE token = $1") { Parameters = { new NpgsqlParameter { Value = token } } },
                    new NpgsqlBatchCommand("DELETE FROM tables_auth.memory_requests WHERE token = $1") { Parameters = { new NpgsqlParameter { Value = token } } },
                },
            };
            await batch.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not delete a table query's request token; it expires on its own");
        }
    }

    /// <summary>Forgets that a schema was granted, after a query found the grant missing (the schema was recreated).</summary>
    public void ForgetGrants(string schema) => _grantedSchemas.TryRemove(schema, out _);

    private async Task<NpgsqlDataSource> ProvisionAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_reader is not null)
                return _reader;

            string roleName = RoleName;
            string role = TableDdl.Quote(roleName);
            string password = _configuration["Tables:ReaderPassword"] is { Length: > 0 } configured
                ? configured
                : new NpgsqlConnectionStringBuilder(_connectionString).Password is { Length: > 0 } main
                    ? DerivePassword(main, roleName)
                    : throw new InvalidOperationException(
                        "Tables queries need a password for the reader login. Set Tables:ReaderPassword, or give the main database login a password.");
            int connectionLimit = _configuration.GetValue<int?>("Tables:ReaderConnectionLimit") ?? 50;

            string database;
            await using (NpgsqlConnection connection = await _admin.OpenConnectionAsync(ct))
            {
                database = connection.Database;
                CheckVersion(connection.PostgreSqlVersion);

                await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);
                await ExecuteAsync(connection, transaction, "SELECT pg_catalog.pg_advisory_xact_lock($1, 0)", ct, LockClass);

                bool exists = false;

                // The only memberships allowed are container reader roles the login may SET ROLE to but not inherit.
                await using (NpgsqlCommand check = new(
                    "SELECT r.rolsuper OR r.rolreplication OR r.rolbypassrls OR r.rolcreaterole OR r.rolcreatedb, "
                    + "EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m JOIN pg_catalog.pg_roles g ON g.oid = m.roleid "
                    + "WHERE m.member = r.oid AND (m.inherit_option OR m.admin_option OR NOT pg_catalog.starts_with(g.rolname, $2))) "
                    + "FROM pg_catalog.pg_roles r WHERE r.rolname = $1", connection, transaction))
                {
                    check.Parameters.Add(new NpgsqlParameter { Value = roleName });
                    check.Parameters.Add(new NpgsqlParameter { Value = TableDdl.ContainerRolePrefix });
                    await using NpgsqlDataReader row = await check.ExecuteReaderAsync(ct);
                    if (await row.ReadAsync(ct))
                    {
                        if (row.GetBoolean(0) || row.GetBoolean(1))
                            throw new InvalidOperationException(
                                $"Role {roleName} exists with more rights than reading tables (or is a member of another role); Connapse won't use it as the tables reader.");
                        exists = true;
                    }
                }

                // The password is hex or operator-supplied; a quoted literal is the only way to set one in DDL.
                string literal = "'" + password.Replace("'", "''", StringComparison.Ordinal) + "'";
                string limit = connectionLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await ExecuteAsync(connection, transaction, exists
                    ? $"ALTER ROLE {role} WITH LOGIN NOINHERIT NOCREATEDB NOCREATEROLE CONNECTION LIMIT {limit} PASSWORD {literal}"
                    : $"CREATE ROLE {role} WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT {limit} PASSWORD {literal}",
                    ct);

                await ExecuteAsync(connection, transaction, $"ALTER ROLE {role} SET statement_timeout = '{StatementTimeout}'", ct);
                await ExecuteAsync(connection, transaction, $"ALTER ROLE {role} SET lock_timeout = '{LockTimeout}'", ct);
                await ExecuteAsync(connection, transaction, $"ALTER ROLE {role} SET idle_in_transaction_session_timeout = '{IdleInTransactionTimeout}'", ct);
                await ExecuteAsync(connection, transaction, $"ALTER ROLE {role} SET work_mem = '{WorkMem}'", ct);
                await ExecuteAsync(connection, transaction, $"ALTER ROLE {role} SET max_parallel_workers_per_gather = {TableQueryRunner.MaxParallelWorkersPerGather}", ct);
                await ExecuteAsync(connection, transaction, $"ALTER ROLE {role} SET default_transaction_read_only = on", ct);

                await transaction.SaveAsync("temp_file_limit", ct);
                try
                {
                    await ExecuteAsync(connection, transaction, $"ALTER ROLE {role} SET temp_file_limit = '{TempFileLimit}'", ct);
                }
                catch (PostgresException ex) when (ex.SqlState == InsufficientPrivilege)
                {
                    await transaction.RollbackAsync("temp_file_limit", ct);
                    _logger.LogWarning(
                        "Could not set temp_file_limit on the tables reader role {Role} ({Message}). Set the server parameter temp_file_limit (for example {Limit}) so one table query can't fill the disk with temporary files.",
                        roleName, ex.MessageText, TempFileLimit);
                }

                await CreateTokenStoreAsync(connection, transaction, ct);
                await RevokeTemporaryAsync(connection, transaction, database, ct);
                await transaction.CommitAsync(ct);
            }
            _database = database;

            NpgsqlConnectionStringBuilder builder = new(_connectionString)
            {
                Username = roleName,
                Password = password,
                MaxPoolSize = _configuration.GetValue<int?>("Tables:ReaderMaxPoolSize") ?? 10,
                ApplicationName = "connapse-tables-reader",
                NoResetOnClose = false,
                CommandTimeout = StatementTimeoutSeconds * 2,
                Options = null,
            };
            _reader = new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
            _logger.LogInformation("Tables reader role {Role} is ready", roleName);
            return _reader;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CheckVersion(Version version)
    {
        if (version.Major < 16)
            throw new InvalidOperationException($"Table queries need PostgreSQL 16 or later; this server is {version}.");
        RowSecurityPatched = IsRowSecurityPatched(version);
        if (!RowSecurityPatched)
            _logger.LogWarning(
                "PostgreSQL {Version} lacks the row-level security fixes for CVE-2026-14666 and CVE-2026-14668. Upgrade to 16.15, 17.11, 18.6 or later.",
                version);
    }

    /// <summary>
    /// Creates or refreshes the token table and its lookup (<see cref="TableDdl.TokenStoreSql"/>), under the same lock
    /// table builds take when they create it first.
    /// </summary>
    private static async Task CreateTokenStoreAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await TableDdl.LockTokenStoreAsync(connection, ct);
        foreach (string sql in TableDdl.TokenStoreSql)
            await ExecuteAsync(connection, transaction, sql, ct);
        foreach (string sql in TableDdl.MemoryStoreSql)
            await ExecuteAsync(connection, transaction, sql, ct);
    }

    /// <summary>Revokes <c>TEMPORARY</c> on the database from <c>PUBLIC</c>, keeping it for the main login.</summary>
    private async Task RevokeTemporaryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string database, CancellationToken ct)
    {
        // A database name can be anything, so it is quoted the general way rather than checked like generated names.
        string quoted = "\"" + database.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        await transaction.SaveAsync("temporary", ct);
        try
        {
            await ExecuteAsync(connection, transaction, $"GRANT TEMPORARY ON DATABASE {quoted} TO CURRENT_USER", ct);
            await ExecuteAsync(connection, transaction, $"REVOKE TEMPORARY ON DATABASE {quoted} FROM PUBLIC", ct);
            await transaction.ReleaseAsync("temporary", ct);
        }
        catch (PostgresException ex) when (ex.SqlState == InsufficientPrivilege)
        {
            await transaction.RollbackAsync("temporary", ct);
            _logger.LogWarning(
                "Could not revoke TEMPORARY on the database from PUBLIC ({Message}), so table readers can create temporary tables. The database owner can run REVOKE TEMPORARY ON DATABASE for PUBLIC.",
                ex.MessageText);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken ct, params object[] values)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        foreach (object value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        await command.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_reader is not null)
            await _reader.DisposeAsync();
        _gate.Dispose();
    }
}
