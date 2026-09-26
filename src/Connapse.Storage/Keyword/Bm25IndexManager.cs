using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
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
            bool offered = await ScalarAsync(connection,
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
    /// The name of the owner's BM25 index, creating it when missing; null when BM25 is unavailable.
    /// </summary>
    public async Task<string?> GetIndexNameAsync(Guid ownerId, CancellationToken ct = default)
    {
        if (state.Known.TryGetValue(ownerId, out string? cached))
            return cached;
        if (!await EnsureExtensionAsync(ct))
            return null;

        await state.CreateLock.WaitAsync(ct);
        try
        {
            if (state.Known.TryGetValue(ownerId, out cached))
                return cached;

            string name = IndexName(ownerId);
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            DbConnection connection = await OpenAsync(context, ct);

            // DDL takes no parameters. The owner id is a Guid formatted as hex and dashes, and
            // the index name is derived from it the same way, so neither can carry SQL.
            await ExecuteAsync(connection,
                $"CREATE INDEX IF NOT EXISTS {name} ON chunks USING bm25 (content) " +
                $"WITH (text_config = 'english') WHERE owner_id = '{ownerId:D}'", ct);

            state.Known[ownerId] = name;
            logger.LogInformation("BM25 index {IndexName} ready for owner {OwnerId}", name, ownerId);
            return name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not create the BM25 index for owner {OwnerId}; using ts_rank", ownerId);
            return null;
        }
        finally
        {
            state.CreateLock.Release();
        }
    }

    internal static string IndexName(Guid ownerId) => IndexPrefix + ownerId.ToString("N");

    private static async Task<DbConnection> OpenAsync(KnowledgeDbContext context, CancellationToken ct)
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
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> ScalarAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct) is true;
    }
}

/// <summary>
/// What <see cref="Bm25IndexManager"/> has learned about the database, shared across scopes: whether
/// the extension is usable, which owner indexes exist, and a lock so two searches never build the
/// same index at once. A singleton per application, so it never outlives the database it describes.
/// </summary>
public sealed class Bm25IndexState
{
    internal ConcurrentDictionary<Guid, string> Known { get; } = new();
    internal SemaphoreSlim CreateLock { get; } = new(1, 1);
    internal bool? Available { get; set; }
}
