using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace Connapse.Ingestion.Pipeline;

/// <summary>
/// Checks whether a chunk's content has already been embedded with the
/// current model and dimensions. Avoids redundant embedding API calls.
/// Cache key: SHA-256(content) + model_id + dimensions.
/// </summary>
public class EmbeddingCache(KnowledgeDbContext context)
{
    /// <summary>
    /// Returns cached embedding vectors for each chunk, or null where no cache hit exists.
    /// </summary>
    public async Task<IReadOnlyList<float[]?>> GetCachedEmbeddingsAsync(
        IReadOnlyList<string> chunkContents,
        string modelId,
        int dimensions,
        CancellationToken ct = default)
    {
        var hashes = chunkContents.Select(ComputeHash).ToList();

        var rows = await context.ChunkVectors
            .Where(cv => hashes.Contains(cv.ContentHash!)
                         && cv.ModelId == modelId
                         && cv.Dimensions == dimensions)
            .Select(cv => new { cv.ContentHash, cv.Embedding })
            .ToListAsync(ct);

        return Match(hashes, rows.Select(cv => (cv.ContentHash!, cv.Embedding.Memory.ToArray())), dimensions);
    }

    /// <summary>
    /// The cached vector for each hash, or null. A vector whose length is not <paramref name="dimensions"/> is
    /// a miss: before #666 a model could return its native size while the row was labelled with the setting.
    /// </summary>
    public static IReadOnlyList<float[]?> Match(
        IReadOnlyList<string> hashes, IEnumerable<(string Hash, float[] Vector)> rows, int dimensions)
    {
        var cached = rows
            .Where(r => dimensions <= 0 || r.Vector.Length == dimensions)
            .GroupBy(r => r.Hash)
            .ToDictionary(g => g.Key, g => g.First().Vector);

        return hashes.Select(h => cached.TryGetValue(h, out var v) ? v : null).ToList();
    }

    /// <summary>
    /// Computes a lowercase hex SHA-256 hash of the given content string.
    /// </summary>
    public static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
