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
    /// <summary>
    /// Retrieve cached embedding vectors for the provided content chunks that match the specified model and dimensionality.
    /// </summary>
    /// <param name="chunkContents">The original text chunks to look up in the cache.</param>
    /// <param name="modelId">The embedding model identifier used to filter cached entries.</param>
    /// <param name="dimensions">The embedding vector dimensionality used to filter cached entries.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>An IReadOnlyList aligned to <paramref name="chunkContents"/> where each element is the cached embedding array for that chunk, or <c>null</c> if no cached embedding was found.</returns>
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

    /// <summary>
    /// Computes a lowercase hex SHA-256 hash of the given content string.
    /// <summary>
    /// Compute the SHA-256 hash of the provided text and return it as a lowercase hexadecimal string.
    /// </summary>
    /// <param name="content">The input text to hash.</param>
    /// <returns>The SHA-256 hash of <paramref name="content"/>, encoded as a lowercase hex string.</returns>
    public static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
