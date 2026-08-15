using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace Connapse.Storage.Vectors;

/// <summary>
/// pgvector-backed vector store implementation.
/// Supports cosine similarity search and CRUD operations for chunk vectors.
/// </summary>
public class PgVectorStore : IVectorStore
{
    private readonly KnowledgeDbContext _context;
    private readonly ILogger<PgVectorStore> _logger;

    public PgVectorStore(
        KnowledgeDbContext context,
        ILogger<PgVectorStore> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Inserts a new chunk embedding or updates an existing one, storing the embedding and related identifiers/metadata in the database.
    /// </summary>
    /// <param name="id">Chunk identifier; must be a valid GUID string.</param>
    /// <param name="vector">Embedding values; must not be null or empty.</param>
    /// <param name="metadata">Metadata containing required keys:
    /// - "documentId": a GUID string identifying the document,
    /// - "modelId": the model identifier string.
    /// - "ownerId": a GUID string identifying the owning container or source.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when:
    /// - <paramref name="id"/> is not a valid GUID,
    /// - <paramref name="vector"/> is null or empty,
    /// - <paramref name="metadata"/> does not contain a valid "documentId",
    /// - <paramref name="metadata"/> does not contain a "modelId".</exception>
    public async Task UpsertAsync(
        string id,
        float[] vector,
        Dictionary<string, string> metadata,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var chunkId))
        {
            throw new ArgumentException("ID must be a valid GUID", nameof(id));
        }

        if (vector == null || vector.Length == 0)
        {
            throw new ArgumentException("Vector cannot be null or empty", nameof(vector));
        }

        // Extract documentId and modelId from metadata
        if (!metadata.TryGetValue("documentId", out var documentIdStr) ||
            !Guid.TryParse(documentIdStr, out var documentId))
        {
            throw new ArgumentException("Metadata must contain a valid 'documentId'", nameof(metadata));
        }

        if (!metadata.TryGetValue("modelId", out var modelId))
        {
            throw new ArgumentException("Metadata must contain a 'modelId'", nameof(metadata));
        }

        // Demand an explicit owner. Defaulting to Guid.Empty here fails silently: the vector
        // is written, but no owner-scoped query can ever match it, so the content becomes
        // unreachable rather than visibly broken.
        if (!metadata.TryGetValue("ownerId", out var ownerIdStr) || !Guid.TryParse(ownerIdStr, out var ownerId))
        {
            throw new ArgumentException("Metadata must contain a valid 'ownerId'", nameof(metadata));
        }

        var existing = await _context.ChunkVectors
            .FirstOrDefaultAsync(cv => cv.ChunkId == chunkId, ct);

        if (existing != null)
        {
            // Update existing vector
            existing.Embedding = new Vector(vector);
            existing.ModelId = modelId;
            existing.DocumentId = documentId;
            existing.OwnerId = ownerId;
        }
        else
        {
            // Insert new vector
            var entity = new ChunkVectorEntity
            {
                ChunkId = chunkId,
                DocumentId = documentId,
                OwnerId = ownerId,
                Embedding = new Vector(vector),
                ModelId = modelId
            };

            _context.ChunkVectors.Add(entity);
        }

        await _context.SaveChangesAsync(ct);

        _logger.LogDebug(
            "Upserted vector for chunk {ChunkId} with dimension {Dimension}",
            id,
            vector.Length);
    }

    /// <summary>
    /// Adds a batch of chunk vector records to the database and persists them in a single save operation.
    /// </summary>
    /// <param name="items">
    /// A list of tuples each containing:
    /// - Id: the chunk identifier as a GUID string.
    /// - Vector: the embedding vector for the chunk.
    /// - Metadata: required and optional metadata for the chunk.
    /// Required metadata keys: "documentId" (GUID string) and "modelId" (string).
    /// Optional metadata keys:
    /// Also required: "ownerId" (GUID string). Optional keys:
    /// - "contentHash" (string),
    /// - "dimensions" (integer string; used only if parses to an int > 0).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when an item Id is not a valid GUID, when "documentId" is missing or not a valid GUID, or when "modelId" is missing.
    /// <summary>
    /// Adds a batch of chunk vector entities from the provided items and saves them to the database with a single commit.
    /// </summary>
    /// <param name="items">
    /// A list of tuples where each tuple contains:
    /// - Id: a string representation of the chunk GUID.
    /// - Vector: the embedding values for the chunk.
    /// - Metadata: a dictionary that must include "documentId" (GUID string), "modelId", and "ownerId" (GUID string); may include "contentHash" and "dimensions" (positive integer string).
    /// </param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    /// <exception cref="ArgumentException">
    /// Thrown if any item's Id is not a valid GUID, if "documentId" is missing or not a valid GUID, or if "modelId" is missing in an item's metadata.
    /// </exception>
    public async Task UpsertBatchAsync(
        IReadOnlyList<(string Id, float[] Vector, Dictionary<string, string> Metadata)> items,
        CancellationToken ct = default)
    {
        if (items.Count == 0)
            return;

        foreach (var (id, vector, metadata) in items)
        {
            if (!Guid.TryParse(id, out var chunkId))
                throw new ArgumentException($"ID '{id}' must be a valid GUID", nameof(items));

            if (!metadata.TryGetValue("documentId", out var documentIdStr) ||
                !Guid.TryParse(documentIdStr, out var documentId))
                throw new ArgumentException("Each item's metadata must contain a valid 'documentId'", nameof(items));

            if (!metadata.TryGetValue("modelId", out var modelId))
                throw new ArgumentException("Each item's metadata must contain a 'modelId'", nameof(items));

            // Demand an explicit owner. Defaulting to Guid.Empty here fails silently: the
            // vector is written, but no owner-scoped query can ever match it, so the content
            // is unreachable rather than visibly broken.
            if (!metadata.TryGetValue("ownerId", out var ownerIdStr) || !Guid.TryParse(ownerIdStr, out var ownerId))
                throw new ArgumentException("Each item's metadata must contain a valid 'ownerId'", nameof(items));

            _context.ChunkVectors.Add(new ChunkVectorEntity
            {
                ChunkId = chunkId,
                DocumentId = documentId,
                OwnerId = ownerId,
                Embedding = new Vector(vector),
                ModelId = modelId,
                ContentHash = metadata.TryGetValue("contentHash", out var hash) ? hash : null,
                Dimensions = metadata.TryGetValue("dimensions", out var dims)
                    && int.TryParse(dims, out var d) && d > 0 ? d : null,
            });
        }

        // One SaveChangesAsync for all vectors (+ any pending chunk entities added by IngestionPipeline).
        await _context.SaveChangesAsync(ct);

        _logger.LogDebug("Batch-upserted {Count} chunk vectors", items.Count);
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        float[] queryVector,
        int topK,
        Dictionary<string, string>? filters = null,
        CancellationToken ct = default)
    {
        if (queryVector == null || queryVector.Length == 0)
        {
            throw new ArgumentException("Query vector cannot be null or empty", nameof(queryVector));
        }

        // Build WHERE clause and named parameters for filters
        var whereClauses = new List<string> { "1=1" };
        var vectorParam = new NpgsqlParameter("@queryVector", new Vector(queryVector));
        var topKParam = new NpgsqlParameter("@topK", NpgsqlDbType.Integer) { Value = topK };
        var parameters = new List<NpgsqlParameter> { vectorParam, topKParam };

        if (filters != null)
        {
            if (filters.TryGetValue("documentId", out var documentIdStr) &&
                Guid.TryParse(documentIdStr, out var documentId))
            {
                whereClauses.Add("cv.document_id = @documentId");
                parameters.Add(new NpgsqlParameter("@documentId", NpgsqlDbType.Uuid) { Value = documentId });
            }

            if (filters.TryGetValue("containerId", out var containerIdStr) &&
                Guid.TryParse(containerIdStr, out var containerId))
            {
                whereClauses.Add("cv.owner_id = @containerId");
                parameters.Add(new NpgsqlParameter("@containerId", NpgsqlDbType.Uuid) { Value = containerId });
            }

            if (filters.TryGetValue("pathPrefix", out var pathPrefix) &&
                !string.IsNullOrWhiteSpace(pathPrefix))
            {
                whereClauses.Add("d.path LIKE @pathPrefix");
                parameters.Add(new NpgsqlParameter("@pathPrefix", NpgsqlDbType.Text) { Value = pathPrefix + "%" });
            }

            if (filters.TryGetValue("modelId", out var modelId) &&
                !string.IsNullOrWhiteSpace(modelId))
            {
                whereClauses.Add("cv.model_id = @modelId");
                parameters.Add(new NpgsqlParameter("@modelId", NpgsqlDbType.Text) { Value = modelId });
            }
        }

        var whereClause = string.Join(" AND ", whereClauses);

        // Dimension cast: the embedding column is unconstrained (vector without dimensions).
        // The cast ensures pgvector can use partial IVFFlat indexes per model_id and that
        // the distance operator works correctly with the query vector's dimension.
        var dims = queryVector.Length;
        var sql = $@"
            SELECT
                cv.chunk_id as ""ChunkId"",
                cv.document_id as ""DocumentId"",
                cv.owner_id as ""ContainerId"",
                (cv.embedding::vector({dims}) <=> @queryVector) as ""Distance"",
                c.content as ""Content"",
                c.chunk_index as ""ChunkIndex"",
                d.file_name as ""FileName"",
                d.content_type as ""ContentType"",
                d.path as ""Path""
            FROM chunk_vectors cv
            INNER JOIN chunks c ON cv.chunk_id = c.id
            INNER JOIN documents d ON cv.document_id = d.id
            WHERE {whereClause}
            ORDER BY ""Distance"" ASC
            LIMIT @topK";

        var results = await _context.Database
            .SqlQueryRaw<VectorSearchRow>(sql, parameters.ToArray())
            .ToListAsync(ct);

        // Convert distance to similarity score (1 - distance)
        // Cosine distance ranges from 0 (identical) to 2 (opposite)
        var searchResults = results.Select(r => new VectorSearchResult(
            r.ChunkId.ToString(),
            (float)(1.0 - r.Distance),
            new Dictionary<string, string>
            {
                { "documentId", r.DocumentId.ToString() },
                { "containerId", r.ContainerId.ToString() },
                { "fileName", r.FileName },
                { "contentType", r.ContentType ?? "" },
                { "content", r.Content },
                { "chunkIndex", r.ChunkIndex.ToString() },
                { "path", r.Path }
            }
        )).ToList();

        _logger.LogDebug(
            "Vector search returned {Count} results for topK={TopK}",
            searchResults.Count,
            topK);

        return searchResults;
    }

    // DTO for raw SQL query result
    private record VectorSearchRow(
        Guid ChunkId,
        Guid DocumentId,
        Guid ContainerId,
        double Distance,
        string Content,
        int ChunkIndex,
        string FileName,
        string? ContentType,
        string Path);

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var chunkId))
        {
            _logger.LogWarning("Invalid chunk ID format: {ChunkId}", id);
            return;
        }

        var entity = await _context.ChunkVectors
            .FirstOrDefaultAsync(cv => cv.ChunkId == chunkId, ct);

        if (entity == null)
        {
            _logger.LogWarning("Chunk vector not found: {ChunkId}", id);
            return;
        }

        _context.ChunkVectors.Remove(entity);
        await _context.SaveChangesAsync(ct);

        _logger.LogDebug("Deleted vector for chunk {ChunkId}", id);
    }

    public async Task DeleteByDocumentIdAsync(string documentId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(documentId, out var guid))
        {
            _logger.LogWarning("Invalid document ID format: {DocumentId}", documentId);
            return;
        }

        var vectors = await _context.ChunkVectors
            .Where(cv => cv.DocumentId == guid)
            .ToListAsync(ct);

        if (vectors.Count == 0)
        {
            _logger.LogDebug("No vectors found for document {DocumentId}", documentId);
            return;
        }

        _context.ChunkVectors.RemoveRange(vectors);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Deleted {Count} vectors for document {DocumentId}",
            vectors.Count,
            documentId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(Guid DocumentId, float[] Embedding)>> GetPooledDocumentEmbeddingsAsync(
        Guid containerId,
        CancellationToken ct = default)
    {
        // Step 1: pick the dominant model_id in the container. ChunkVectorEntity carries
        // OwnerId and ModelId directly — no JOIN needed.
        var modelCounts = await _context.ChunkVectors
            .Where(cv => cv.OwnerId == containerId)
            .GroupBy(cv => cv.ModelId)
            .Select(g => new { ModelId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(ct);

        if (modelCounts.Count == 0)
        {
            return Array.Empty<(Guid, float[])>();
        }

        string dominantModelId = modelCounts[0].ModelId;

        if (modelCounts.Count > 1)
        {
            // The interface contract asks for the count of *documents* excluded due to a
            // non-dominant model. A document is excluded only when it has no chunks of the
            // dominant model — docs with mixed-model chunks still contribute their dominant
            // ones. Compute total vs. dominant-reachable distinct docs (mixed-model path only).
            int totalDocs = await _context.ChunkVectors
                .Where(cv => cv.OwnerId == containerId)
                .Select(cv => cv.DocumentId)
                .Distinct()
                .CountAsync(ct);
            int includedDocs = await _context.ChunkVectors
                .Where(cv => cv.OwnerId == containerId && cv.ModelId == dominantModelId)
                .Select(cv => cv.DocumentId)
                .Distinct()
                .CountAsync(ct);
            int excludedDocuments = totalDocs - includedDocs;

            _logger.LogWarning(
                "GetPooledDocumentEmbeddingsAsync: container {ContainerId} has mixed embedding models. " +
                "Using dominant model '{DominantModelId}' ({DominantVectorCount} vectors). " +
                "Excluding {ExcludedDocumentCount} document(s) with no chunks of the dominant model, " +
                "across {OtherModelCount} other model(s).",
                containerId, dominantModelId, modelCounts[0].Count, excludedDocuments, modelCounts.Count - 1);
        }

        // Step 2: pool per-document. AVG(embedding) returns a vector at the same dimensionality.
        var conn = _context.Database.GetDbConnection();
        bool openedHere = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
            openedHere = true;
        }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT document_id, AVG(embedding)::vector AS pooled
                FROM chunk_vectors
                WHERE owner_id = @cid
                  AND model_id = @model_id
                GROUP BY document_id
                """;

            var p = cmd.Parameters;
            p.Add(new NpgsqlParameter("cid", containerId));
            p.Add(new NpgsqlParameter("model_id", dominantModelId));

            var results = new List<(Guid DocumentId, float[] Embedding)>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                Guid docId = reader.GetGuid(0);
                Vector pooled = reader.GetFieldValue<Vector>(1);
                float[] raw = pooled.ToArray();
                float[] normalized = L2Normalize(raw);
                results.Add((docId, normalized));
            }

            return results;
        }
        finally
        {
            if (openedHere)
            {
                await conn.CloseAsync();
            }
        }
    }

    private static float[] L2Normalize(float[] v)
    {
        double sumSq = 0;
        for (int i = 0; i < v.Length; i++) sumSq += v[i] * v[i];
        double norm = Math.Sqrt(sumSq);
        if (norm < 1e-12) return v; // degenerate; leave as-is rather than div-by-zero
        float[] result = new float[v.Length];
        for (int i = 0; i < v.Length; i++) result[i] = (float)(v[i] / norm);
        return result;
    }
}
