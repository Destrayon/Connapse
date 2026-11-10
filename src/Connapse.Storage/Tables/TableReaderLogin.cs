using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Connapse.Storage.Tables;

/// <summary>
/// The database login agents' table queries run on (CON-303), and its connection pool. Grants are the security
/// boundary: the login holds <c>USAGE</c> on the tables schemas and <c>SELECT</c> on their tables and nothing else, so
/// even a query that escaped read-only mode could not write, read Connapse's own tables, read server files or run
/// programs. The role is created lazily by Connapse's main login, under an advisory lock so instances don't race:
/// <list type="bullet">
/// <item>Its name comes from the database name, because roles are cluster-wide and two Connapse databases on one
/// server must not share a reader.</item>
/// <item>Its password is derived from the main login's password, so every instance agrees on it and nothing new is
/// stored. <c>Tables:ReaderPassword</c> overrides it, and is required when the main login has no password.</item>
/// <item>It is refused (fail closed) if a role of that name already has superuser, replication, bypass-RLS,
/// create-role or create-database rights, or belongs to any other role.</item>
/// <item>Its defaults include a statement timeout, <c>work_mem</c> and read-only transactions, backing up the
/// per-query <c>SET LOCAL</c>s. <c>temp_file_limit</c> needs superuser (or a <c>SET</c> grant) to set per role, which
/// Azure Flexible Server's admin may lack; then a warning asks the operator to set the server parameter.</item>
/// </list>
/// A container's grants are given on its first query in this process: <c>USAGE</c> on its schema, <c>SELECT</c> on
/// its tables, and default privileges so tables the main login creates there later are readable too.
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

    /// <summary>Grants the reader read access to a tables schema, once per schema per process.</summary>
    public async Task EnsureGrantsAsync(Guid containerId, string schema, CancellationToken ct)
    {
        if (_grantedSchemas.ContainsKey(schema))
            return;
        if (schema != TableDdl.SchemaName(containerId))
            throw new InvalidOperationException($"Schema {schema} doesn't belong to container {containerId}.");

        if (_reader is null)
            await ProvisionAsync(ct);

        string role = TableDdl.Quote(RoleName);
        string quotedSchema = TableDdl.Quote(schema);
        await using NpgsqlConnection connection = await _admin.OpenConnectionAsync(ct);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);
        // Shared container lock: never grant on a schema that is being dropped. Then an exclusive lock per schema,
        // because concurrent GRANTs on one object fail with "tuple concurrently updated".
        await TableDdl.LockContainerAsync(connection, containerId, shared: true, ct);
        await ExecuteAsync(connection, transaction, "SELECT pg_catalog.pg_advisory_xact_lock($1, pg_catalog.hashtext($2))", ct, LockClass, schema);
        await ExecuteAsync(connection, transaction, $"GRANT USAGE ON SCHEMA {quotedSchema} TO {role}", ct);
        await ExecuteAsync(connection, transaction, $"GRANT SELECT ON ALL TABLES IN SCHEMA {quotedSchema} TO {role}", ct);
        await ExecuteAsync(connection, transaction, $"ALTER DEFAULT PRIVILEGES IN SCHEMA {quotedSchema} GRANT SELECT ON TABLES TO {role}", ct);
        await transaction.CommitAsync(ct);

        _grantedSchemas[schema] = true;
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

            await using (NpgsqlConnection connection = await _admin.OpenConnectionAsync(ct))
            {
                await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);
                await ExecuteAsync(connection, transaction, "SELECT pg_catalog.pg_advisory_xact_lock($1, 0)", ct, LockClass);

                bool exists = false;

                await using (NpgsqlCommand check = new(
                    "SELECT r.rolsuper OR r.rolreplication OR r.rolbypassrls OR r.rolcreaterole OR r.rolcreatedb, "
                    + "EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member = r.oid) "
                    + "FROM pg_catalog.pg_roles r WHERE r.rolname = $1", connection, transaction))
                {
                    check.Parameters.Add(new NpgsqlParameter { Value = roleName });
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

                await transaction.CommitAsync(ct);
            }

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

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct, params object[] values)
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
