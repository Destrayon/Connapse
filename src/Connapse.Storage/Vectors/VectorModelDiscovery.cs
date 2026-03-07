using System.Data;
using System.Data.Common;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.Vectors;

/// <summary>
/// Discovers which embedding models have vectors in the store.
/// Used to detect legacy vectors after an embedding model change.
/// </summary>
public class VectorModelDiscovery(
    IDbContextFactory<KnowledgeDbContext> contextFactory,
    ILogger<VectorModelDiscovery> logger)
{
    /// <summary>
    /// Returns information about each distinct embedding model in the store,
    /// optionally filtered by container.
    /// </summary>
    public virtual async Task<IReadOnlyList<EmbeddingModelInfo>> GetModelsAsync(
        Guid? containerId = null, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();

        if (containerId.HasValue)
        {
            cmd.CommandText =
                "SELECT model_id, vector_dims(embedding) AS dims, COUNT(*) AS cnt " +
                "FROM chunk_vectors " +
                "WHERE container_id = @containerId " +
                "GROUP BY model_id, vector_dims(embedding) " +
                "ORDER BY cnt DESC";

            var param = cmd.CreateParameter();
            param.ParameterName = "@containerId";
            param.Value = containerId.Value;
            cmd.Parameters.Add(param);
        }
        else
        {
            cmd.CommandText =
                "SELECT model_id, vector_dims(embedding) AS dims, COUNT(*) AS cnt " +
                "FROM chunk_vectors " +
                "GROUP BY model_id, vector_dims(embedding) " +
                "ORDER BY cnt DESC";
        }

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var models = new List<EmbeddingModelInfo>();

        while (await reader.ReadAsync(ct))
        {
            models.Add(new EmbeddingModelInfo(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt64(2)));
        }

        logger.LogDebug(
            "Found {Count} distinct embedding models{ContainerFilter}",
            models.Count,
            containerId.HasValue ? $" for container {containerId}" : "");

        return models;
    }

    /// <summary>
    /// Returns true if vectors exist for any model other than <paramref name="currentModelId"/>.
    /// </summary>
    public async Task<bool> HasLegacyVectorsAsync(
        string currentModelId, Guid? containerId = null, CancellationToken ct = default)
    {
        var models = await GetModelsAsync(containerId, ct);
        return models.Any(m => !string.Equals(m.ModelId, currentModelId, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Information about an embedding model's vectors in the store.
/// </summary>
public record EmbeddingModelInfo(string ModelId, int Dimensions, long VectorCount);
