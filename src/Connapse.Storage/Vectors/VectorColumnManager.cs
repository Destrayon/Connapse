using System.Data;
using System.Data.Common;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.Vectors;

/// <summary>
/// Manages partial IVFFlat indexes on the chunk_vectors.embedding column, one per model_id.
/// The embedding column is unconstrained (<c>vector</c> without dimensions), so each model's
/// vectors get their own typed partial index for efficient cosine-similarity search.
/// </summary>
public class VectorColumnManager(
    IDbContextFactory<KnowledgeDbContext> contextFactory,
    ILogger<VectorColumnManager> logger)
{
    /// <summary>
    /// Minimum number of vectors for a model before an IVFFlat index is created.
    /// Below this threshold, pgvector uses exact sequential scan which is fast enough.
    /// </summary>
    private const int MinVectorsForIndex = 10;

    /// <summary>
    /// Ensures a partial IVFFlat index exists for every distinct model_id in chunk_vectors.
    /// Safe to call multiple times (idempotent). Drops orphaned indexes for models that
    /// no longer have any vectors.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        // 1. Get all distinct model_id values with their dimensions and row counts
        var models = await GetModelInfoAsync(connection, ct);

        // 2. Get existing partial indexes (convention: idx_cv_emb_*)
        var existingIndexes = await GetExistingPartialIndexNamesAsync(connection, ct);

        // 3. Create missing indexes for models with enough vectors
        foreach (var model in models)
        {
            var indexName = GetIndexName(model.ModelId);

            if (existingIndexes.Contains(indexName))
            {
                logger.LogDebug(
                    "Partial index {IndexName} already exists for model {ModelId}",
                    indexName, model.ModelId);
                continue;
            }

            if (model.RowCount < MinVectorsForIndex)
            {
                logger.LogDebug(
                    "Skipping partial index for model {ModelId}: only {Count} vectors (minimum {Min})",
                    model.ModelId, model.RowCount, MinVectorsForIndex);
                continue;
            }

            await CreatePartialIndexAsync(connection, indexName, model, ct);
        }

        // 4. Drop orphaned indexes (model_id no longer in table)
        var activeIndexNames = models
            .Select(m => GetIndexName(m.ModelId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var orphan in existingIndexes.Except(activeIndexNames, StringComparer.OrdinalIgnoreCase))
        {
            await DropIndexAsync(connection, orphan, ct);
        }
    }

    /// <summary>
    /// Generates a deterministic, PostgreSQL-safe index name from a model_id.
    /// Convention: idx_cv_emb_{sanitized_model_id}, truncated to 63 chars (PG identifier limit).
    /// </summary>
    internal static string GetIndexName(string modelId)
    {
        var sanitized = new string(
            modelId.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());

        // Collapse consecutive underscores and trim
        while (sanitized.Contains("__"))
            sanitized = sanitized.Replace("__", "_");
        sanitized = sanitized.Trim('_');

        var name = $"idx_cv_emb_{sanitized}";
        return name.Length > 63 ? name[..63] : name;
    }

    private async Task CreatePartialIndexAsync(
        DbConnection connection, string indexName, ModelInfo model, CancellationToken ct)
    {
        var lists = Math.Clamp((int)(model.RowCount / 1000), 1, 100);
        var sanitizedModelId = SanitizeForSql(model.ModelId);

        // Use raw ADO.NET — CREATE INDEX CONCURRENTLY cannot run inside a transaction,
        // and EF's ExecuteSqlRawAsync may wrap in an implicit transaction.
        var sql = $"CREATE INDEX CONCURRENTLY IF NOT EXISTS {indexName} " +
                  $"ON chunk_vectors " +
                  $"USING ivfflat ((embedding::vector({model.Dimensions})) vector_cosine_ops) " +
                  $"WITH (lists = {lists}) " +
                  $"WHERE (model_id = '{sanitizedModelId}')";

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);

            logger.LogInformation(
                "Created partial IVFFlat index {IndexName} for model {ModelId} " +
                "(dims={Dimensions}, lists={Lists}, rows={Rows})",
                indexName, model.ModelId, model.Dimensions, lists, model.RowCount);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to create partial index {IndexName} for model {ModelId}",
                indexName, model.ModelId);
        }
    }

    private async Task DropIndexAsync(DbConnection connection, string indexName, CancellationToken ct)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"DROP INDEX IF EXISTS {indexName}";
            await cmd.ExecuteNonQueryAsync(ct);

            logger.LogInformation("Dropped orphaned partial index {IndexName}", indexName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to drop orphaned index {IndexName}", indexName);
        }
    }

    private static async Task<List<ModelInfo>> GetModelInfoAsync(
        DbConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT model_id, vector_dims(embedding) AS dims, COUNT(*) AS cnt " +
            "FROM chunk_vectors " +
            "GROUP BY model_id, vector_dims(embedding)";

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var models = new List<ModelInfo>();

        while (await reader.ReadAsync(ct))
        {
            models.Add(new ModelInfo(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt64(2)));
        }

        return models;
    }

    private static async Task<HashSet<string>> GetExistingPartialIndexNamesAsync(
        DbConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT indexname FROM pg_indexes " +
            "WHERE tablename = 'chunk_vectors' AND indexname LIKE 'idx_cv_emb_%'";

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (await reader.ReadAsync(ct))
        {
            indexes.Add(reader.GetString(0));
        }

        return indexes;
    }

    /// <summary>
    /// Sanitizes a model_id value for safe inclusion in a SQL WHERE clause literal.
    /// Allows only alphanumeric, hyphens, underscores, and dots.
    /// </summary>
    private static string SanitizeForSql(string value) =>
        new(value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());

    private record ModelInfo(string ModelId, int Dimensions, long RowCount);
}
