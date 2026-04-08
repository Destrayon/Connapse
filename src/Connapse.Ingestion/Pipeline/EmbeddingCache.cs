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
    public async Task<IReadOnlyList<float[]?>> GetCachedEmbeddingsAsync(
        IReadOnlyList<string> chunkContents,
        string modelId,
        int dimensions,
        CancellationToken ct = default)
    {
        var hashes = chunkContents.Select(ComputeHash).ToList();

        var cached = await context.ChunkVectors
            .Where(cv => hashes.Contains(cv.ContentHash!)
                         && cv.ModelId == modelId
                         && cv.Dimensions == dimensions)
            .Select(cv => new { cv.ContentHash, cv.Embedding })
            .ToDictionaryAsync(cv => cv.ContentHash!, cv => cv.Embedding.Memory.ToArray(), ct);

        return hashes.Select(h => cached.TryGetValue(h, out var v) ? v : null).ToList();
    }

    public static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
