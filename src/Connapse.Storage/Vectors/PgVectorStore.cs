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

        // Extract containerId from metadata
        Guid containerId = Guid.Empty;
        if (metadata.TryGetValue("containerId", out var containerIdStr))
        {
            Guid.TryParse(containerIdStr, out containerId);
        }

        var existing = await _context.ChunkVectors
            .FirstOrDefaultAsync(cv => cv.ChunkId == chunkId, ct);

        if (existing != null)
        {
            // Update existing vector
            existing.Embedding = new Vector(vector);
            existing.ModelId = modelId;
            existing.DocumentId = documentId;
            existing.ContainerId = containerId;
        }
        else
        {
            // Insert new vector
            var entity = new ChunkVectorEntity
            {
                ChunkId = chunkId,
                DocumentId = documentId,
                ContainerId = containerId,
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
                whereClauses.Add("cv.container_id = @containerId");
                parameters.Add(new NpgsqlParameter("@containerId", NpgsqlDbType.Uuid) { Value = containerId });
            }

            if (filters.TryGetValue("pathPrefix", out var pathPrefix) &&
                !string.IsNullOrWhiteSpace(pathPrefix))
            {
                whereClauses.Add("d.path LIKE @pathPrefix");
                parameters.Add(new NpgsqlParameter("@pathPrefix", NpgsqlDbType.Text) { Value = pathPrefix + "%" });
            }
        }

        var whereClause = string.Join(" AND ", whereClauses);

        // Use raw SQL to leverage pgvector's <=> cosine distance operator
        // Named parameters avoid positional binding issues with the Vector type
        var sql = $@"
            SELECT
                cv.chunk_id as ""ChunkId"",
                cv.document_id as ""DocumentId"",
                cv.container_id as ""ContainerId"",
                (cv.embedding <=> @queryVector) as ""Distance"",
                c.content as ""Content"",
                c.chunk_index as ""ChunkIndex"",
                d.file_name as ""FileName"",
                d.content_type as ""ContentType""
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
                { "chunkIndex", r.ChunkIndex.ToString() }
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
        string? ContentType);

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
}
