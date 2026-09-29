using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Connapse.Core;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Vectors;

/// <summary>
/// Keeps one approximate (HNSW) index per large container and embedding model (#571). Every
/// vector search is restricted to one container, so a shared per-model index would be searched
/// and then filtered down to a sliver — the pattern that loses recall — and the planner ignored
/// it anyway. Small containers need no index: exact search over them is fast. Each index covers
/// only its container's rows (a partial index) and is built over half-precision vectors, which
/// halves its size and build time without measurable recall loss.
/// </summary>
public class VectorColumnManager(
    IDbContextFactory<KnowledgeDbContext> contextFactory,
    IOptionsMonitor<SearchSettings> searchSettings,
    ILogger<VectorColumnManager> logger)
{
    internal const string IndexPrefix = "ix_cv_hnsw_";

    /// <summary>The shared per-model IVFFlat indexes earlier versions built; dropped on sight.</summary>
    private const string LegacyPrefix = "idx_cv_emb_";

    /// <summary>pgvector indexes halfvec up to 4,000 dimensions.</summary>
    private const int MaxIndexedDimensions = 4_000;

    /// <summary>One process at a time maintains indexes (several app instances share a database).</summary>
    private const long AdvisoryLockKey = 571_000_571;

    /// <summary>
    /// Brings the indexes in line with the data: builds one for every (container, model) with at
    /// least <see cref="SearchSettings.VectorIndexMinVectors"/> vectors, drops those whose container
    /// shrank below half that (so a container near the line doesn't flap) or is gone, drops invalid
    /// leftovers of interrupted builds, and drops the legacy shared indexes. Builds run one at a time
    /// with <c>CONCURRENTLY</c>, so writes continue while a large index builds. Idempotent.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        if (!await ScalarAsync<bool>(connection, $"SELECT pg_try_advisory_lock({AdvisoryLockKey})", ct))
        {
            logger.LogDebug("Another process is maintaining vector indexes; skipping");
            return;
        }

        try
        {
            int threshold = searchSettings.CurrentValue.VectorIndexMinVectors;
            List<VectorGroup> groups = await GetGroupsAsync(connection, ct);
            Dictionary<string, bool> existing = await GetIndexesAsync(connection, ct);

            foreach ((string name, _) in existing.Where(e => e.Key.StartsWith(LegacyPrefix, StringComparison.Ordinal)))
                await DropIndexAsync(connection, name, "legacy shared index", ct);

            Dictionary<string, VectorGroup> byName = groups.ToDictionary(g => GetIndexName(g.OwnerId, g.ModelId));
            foreach ((string name, bool valid) in existing.Where(e => e.Key.StartsWith(IndexPrefix, StringComparison.Ordinal)))
            {
                if (!valid)
                    await DropIndexAsync(connection, name, "invalid (interrupted build)", ct);
                else if (!byName.TryGetValue(name, out VectorGroup? group) || group.Count < threshold / 2)
                    await DropIndexAsync(connection, name, "container no longer large enough", ct);
            }

            foreach ((string name, VectorGroup group) in byName.OrderBy(g => g.Value.Count))
            {
                bool wanted = group.Count >= threshold && group.Dimensions <= MaxIndexedDimensions;
                if (wanted && !(existing.TryGetValue(name, out bool valid) && valid))
                    await CreateIndexAsync(connection, name, group, ct);
            }
        }
        finally
        {
            VectorIndexCatalog.Invalidate();
            await ScalarAsync<bool>(connection, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }

    /// <summary><c>ix_cv_hnsw_{container}_{hash of model id}</c> — fixed length, safe for any model id.</summary>
    internal static string GetIndexName(Guid ownerId, string modelId) =>
        $"{IndexPrefix}{ownerId:N}_{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(modelId)))[..8]}";

    /// <summary>The partial-index predicate; the model id is a quoted literal, never interpolated raw.</summary>
    internal static string Predicate(Guid ownerId, string modelId) =>
        $"owner_id = '{ownerId}'::uuid AND model_id = '{modelId.Replace("'", "''")}'";

    private async Task CreateIndexAsync(DbConnection connection, string name, VectorGroup group, CancellationToken ct)
    {
        // CONCURRENTLY can't run in a transaction; a large build takes minutes, so no timeout. A
        // serial build keeps its working memory local: parallel builds put the graph in shared
        // memory, which Docker limits to 64 MB by default and the build then fails.
        string sql =
            $"CREATE INDEX CONCURRENTLY IF NOT EXISTS {name} ON chunk_vectors "
            + $"USING hnsw ((embedding::halfvec({group.Dimensions})) halfvec_cosine_ops) "
            + $"WITH (m = 16, ef_construction = 200) WHERE {Predicate(group.OwnerId, group.ModelId)}";
        try
        {
            await ExecuteAsync(connection, "SET max_parallel_maintenance_workers = 0; SET maintenance_work_mem = '1GB';", ct);
            DateTime started = DateTime.UtcNow;
            await ExecuteAsync(connection, sql, ct);
            logger.LogInformation(
                "Built vector index {IndexName} for container {ContainerId}, model {ModelId}: {Count} vectors in {Seconds:F0}s",
                name, group.OwnerId, group.ModelId, group.Count, (DateTime.UtcNow - started).TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to build vector index {IndexName}; exact search continues for container {ContainerId}",
                name, group.OwnerId);
        }
        finally
        {
            await ExecuteAsync(connection, "RESET max_parallel_maintenance_workers; RESET maintenance_work_mem;", CancellationToken.None);
        }
    }

    private async Task DropIndexAsync(DbConnection connection, string name, string reason, CancellationToken ct)
    {
        try
        {
            await ExecuteAsync(connection, $"DROP INDEX CONCURRENTLY IF EXISTS {name}", ct);
            logger.LogInformation("Dropped vector index {IndexName}: {Reason}", name, reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to drop vector index {IndexName}", name);
        }
    }

    private static async Task<List<VectorGroup>> GetGroupsAsync(DbConnection connection, CancellationToken ct)
    {
        await using DbCommand cmd = connection.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText =
            "SELECT owner_id, model_id, vector_dims(embedding) AS dims, COUNT(*) AS cnt "
            + "FROM chunk_vectors GROUP BY owner_id, model_id, vector_dims(embedding)";
        List<VectorGroup> groups = [];
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            groups.Add(new VectorGroup(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3)));
        return groups;
    }

    /// <summary>Existing index names with whether each is valid (an interrupted CONCURRENTLY build leaves an invalid one).</summary>
    private static async Task<Dictionary<string, bool>> GetIndexesAsync(DbConnection connection, CancellationToken ct)
    {
        await using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT c.relname, i.indisvalid FROM pg_index i "
            + "JOIN pg_class c ON c.oid = i.indexrelid JOIN pg_class t ON t.oid = i.indrelid "
            + $"WHERE t.relname = 'chunk_vectors' AND (c.relname LIKE '{IndexPrefix}%' OR c.relname LIKE '{LegacyPrefix}%')";
        Dictionary<string, bool> indexes = new(StringComparer.Ordinal);
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            indexes[reader.GetString(0)] = reader.GetBoolean(1);
        return indexes;
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using DbCommand cmd = connection.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(DbConnection connection, string sql, CancellationToken ct)
    {
        await using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private sealed record VectorGroup(Guid OwnerId, string ModelId, int Dimensions, long Count);
}

/// <summary>
/// Which per-container vector indexes exist and are ready, refreshed at most once a minute: search
/// asks on every query, and a new index only needs to be noticed eventually.
/// </summary>
internal static class VectorIndexCatalog
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);
    private static (DateTime At, HashSet<string> Names) _ready = (DateTime.MinValue, []);

    public static async Task<bool> IsIndexedAsync(KnowledgeDbContext context, Guid ownerId, string modelId, CancellationToken ct)
    {
        (DateTime at, HashSet<string> names) = _ready;
        if (DateTime.UtcNow - at > Lifetime)
        {
            List<string> valid = await context.Database.SqlQueryRaw<string>(
                    "SELECT c.relname AS \"Value\" FROM pg_index i "
                    + "JOIN pg_class c ON c.oid = i.indexrelid JOIN pg_class t ON t.oid = i.indrelid "
                    + $"WHERE t.relname = 'chunk_vectors' AND c.relname LIKE '{VectorColumnManager.IndexPrefix}%' AND i.indisvalid")
                .ToListAsync(ct);
            names = valid.ToHashSet(StringComparer.Ordinal);
            _ready = (DateTime.UtcNow, names);
        }
        return names.Contains(VectorColumnManager.GetIndexName(ownerId, modelId));
    }

    /// <summary>Forget the cached list, so the next search sees indexes built or dropped just now.</summary>
    public static void Invalidate() => _ready = (DateTime.MinValue, []);
}
