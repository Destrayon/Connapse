using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.Keyword;

/// <summary>
/// Manages pg_textsearch BM25 indexes on chunks.content, one partial index per owner (container or
/// source). BM25's IDF comes from whatever an index covers, so one index per owner keeps each
/// container's term statistics its own, the same isolation vectors and documents already have.
/// The extension is optional: on an image without it, <see cref="GetIndexNameAsync"/> returns null
/// and keyword search keeps ranking with ts_rank.
/// </summary>
public class Bm25IndexManager(
    IDbContextFactory<KnowledgeDbContext> contextFactory,
    Bm25IndexState state,
    ILogger<Bm25IndexManager> logger)
{
    private const string IndexPrefix = "idx_chunks_bm25_";

    /// <summary>
    /// Owners with at most this many chunks get their index built during the search that first
    /// needs it (about a second per few thousand chunks); larger ones build in the background.
    /// </summary>
    internal const int SyncBuildLimit = 20_000;

    /// <summary>
    /// Installs the extension when the server offers it and it is preloaded. Safe to call repeatedly.
    /// </summary>
    public async Task<bool> EnsureExtensionAsync(CancellationToken ct = default)
    {
        if (state.Available is { } known)
            return known;

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        DbConnection connection = await OpenAsync(context, ct);
        try
        {
            bool offered = await ScalarAsync<bool>(connection,
                "SELECT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'pg_textsearch')", ct);
            if (offered)
                await ExecuteAsync(connection, "CREATE EXTENSION IF NOT EXISTS pg_textsearch", ct);
            state.Available = offered;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Offered but not in shared_preload_libraries, or no permission to create it.
            logger.LogWarning(ex, "pg_textsearch is installed but could not be enabled; keyword search will use ts_rank");
            state.Available = false;
        }

        return state.Available.Value;
    }

    /// <summary>
    /// The name of the owner's BM25 index when it is ready; null when BM25 is unavailable or the
    /// index is still being built in the background, in which case the caller ranks with ts_rank.
    /// </summary>
    public async Task<string?> GetIndexNameAsync(Guid ownerId, CancellationToken ct = default)
    {
        if (state.Known.ContainsKey(ownerId))
            return IndexName(ownerId);
        if (state.Building.ContainsKey(ownerId) || !await EnsureExtensionAsync(ct))
            return null;

        string name = IndexName(ownerId);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            DbConnection connection = await OpenAsync(context, ct);

            if (await IsValidAsync(connection, name, ct) == true)
            {
                state.Known[ownerId] = true;
                return name;
            }

            long chunks = await ScalarAsync<long>(connection,
                $"SELECT count(*) FROM chunks WHERE owner_id = '{ownerId:D}'", ct);
            if (chunks > SyncBuildLimit && state.StartBackgroundBuild(ownerId, name))
            {
                logger.LogInformation(
                    "Building BM25 index {IndexName} for owner {OwnerId} ({Chunks} chunks) in the background; using ts_rank until it is ready",
                    name, ownerId, chunks);
                return null;
            }

            await BuildAsync(connection, name, ownerId, ct);
            state.Known[ownerId] = true;
            logger.LogInformation("BM25 index {IndexName} ready for owner {OwnerId}", name, ownerId);
            return name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not create the BM25 index for owner {OwnerId}; using ts_rank", ownerId);
            return null;
        }
    }

    internal static string IndexName(Guid ownerId) => IndexPrefix + ownerId.ToString("N");

    /// <summary>
    /// Builds the owner's index without blocking writes to chunks. A concurrent build that failed
    /// leaves an invalid index behind, which IF NOT EXISTS would otherwise keep forever.
    /// DDL takes no parameters: the owner id is a Guid formatted as hex and dashes, and the index
    /// name is derived from it the same way, so neither can carry SQL.
    /// </summary>
    internal static async Task BuildAsync(DbConnection connection, string name, Guid ownerId, CancellationToken ct)
    {
        if (await IsValidAsync(connection, name, ct) == false)
            await ExecuteAsync(connection, $"DROP INDEX CONCURRENTLY IF EXISTS {name}", ct);

        await ExecuteAsync(connection,
            $"CREATE INDEX CONCURRENTLY IF NOT EXISTS {name} ON chunks USING bm25 (content) " +
            $"WITH (text_config = 'english') WHERE owner_id = '{ownerId:D}'", ct);
    }

    /// <summary>True when the index exists and is usable, false when a failed build left it invalid, null when absent.</summary>
    private static async Task<bool?> IsValidAsync(DbConnection connection, string name, CancellationToken ct)
    {
        await using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT i.indisvalid FROM pg_class c JOIN pg_index i ON i.indexrelid = c.oid WHERE c.relname = @name";
        DbParameter parameter = cmd.CreateParameter();
        parameter.ParameterName = "name";
        parameter.Value = name;
        cmd.Parameters.Add(parameter);
        return await cmd.ExecuteScalarAsync(ct) as bool?;
    }

    internal static async Task<DbConnection> OpenAsync(KnowledgeDbContext context, CancellationToken ct)
    {
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        // An index build can outlast the default 30 s command timeout.
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(DbConnection connection, string sql, CancellationToken ct)
    {
        await using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }
}

/// <summary>
/// What <see cref="Bm25IndexManager"/> has learned about the database, shared across scopes: whether
/// the extension is usable, which owner indexes are ready, and which are being built in the
/// background. A singleton per application, so it never outlives the database it describes.
/// </summary>
public sealed class Bm25IndexState(IServiceScopeFactory? scopes = null, ILogger<Bm25IndexState>? logger = null)
{
    internal ConcurrentDictionary<Guid, bool> Known { get; } = new();
    internal ConcurrentDictionary<Guid, Task> Building { get; } = new();
    internal bool? Available { get; set; }

    /// <summary>
    /// Starts building the owner's index on its own scope, so it outlives the search that asked.
    /// False when there is no scope factory to build with (the caller then builds inline).
    /// </summary>
    internal bool StartBackgroundBuild(Guid ownerId, string name)
    {
        if (scopes is null)
            return false;

        Building.GetOrAdd(ownerId, _ => Task.Run(async () =>
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                await using KnowledgeDbContext context = await scope.ServiceProvider
                    .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
                await Bm25IndexManager.BuildAsync(await Bm25IndexManager.OpenAsync(context, default), name, ownerId, default);
                Known[ownerId] = true;
                logger?.LogInformation("BM25 index {IndexName} ready for owner {OwnerId}", name, ownerId);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Background build of BM25 index {IndexName} failed; keyword search keeps using ts_rank", name);
            }
            finally
            {
                Building.TryRemove(ownerId, out Task? _);
            }
        }));
        return true;
    }
}
