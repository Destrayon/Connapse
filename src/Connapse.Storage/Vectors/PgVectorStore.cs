using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly IOptionsMonitor<SearchSettings> _searchSettings;
    private readonly ILogger<PgVectorStore> _logger;

    public PgVectorStore(
        KnowledgeDbContext context,
        IOptionsMonitor<SearchSettings> searchSettings,
        ILogger<PgVectorStore> logger)
    {
        _context = context;
        _searchSettings = searchSettings;
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
        Dictionary<string, string>? filters,
        SearchScopes scopes,
        CancellationToken ct = default)
    {
        if (queryVector == null || queryVector.Length == 0)
        {
            throw new ArgumentException("Query vector cannot be null or empty", nameof(queryVector));
        }

        // Build WHERE clause and named parameters for filters
        // A source whose remote revoked access (a public repository gone private) is left out,
        // whatever else the caller asked for.
        var whereClauses = new List<string> { "NOT EXISTS (SELECT 1 FROM sources s WHERE s.id = d.source_id AND s.access_revoked_at IS NOT NULL)" };
        // Filters on chunk_vectors alone: the neighbours-first query applies them where the
        // per-container index can serve the ORDER BY; the rest (on documents) apply afterwards.
        var vectorClauses = new List<string>();
        bool hasContainer = false, hasModel = false;
        var vectorParam = new NpgsqlParameter("@queryVector", new Vector(queryVector));
        var topKParam = new NpgsqlParameter("@topK", NpgsqlDbType.Integer) { Value = topK };
        var parameters = new List<NpgsqlParameter> { vectorParam, topKParam };

        if (filters != null)
        {
            if (filters.TryGetValue("documentId", out var documentIdStr) &&
                Guid.TryParse(documentIdStr, out var documentId))
            {
                vectorClauses.Add("cv.document_id = @documentId");
                parameters.Add(new NpgsqlParameter("@documentId", NpgsqlDbType.Uuid) { Value = documentId });
            }

            if (filters.TryGetValue("containerId", out var containerIdStr) &&
                Guid.TryParse(containerIdStr, out var containerId))
            {
                vectorClauses.Add("cv.owner_id = @containerId");
                hasContainer = true;
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
                vectorClauses.Add("cv.model_id = @modelId");
                hasModel = true;
                parameters.Add(new NpgsqlParameter("@modelId", NpgsqlDbType.Text) { Value = modelId });
            }
        }

        // Permission filtering. Pushed into the query rather than applied to its results: a
        // post-filter would take the top K and then remove most of them, so a user with narrow
        // access would get a handful of hits for a query that had plenty — worse answers for
        // being less privileged, with nothing on screen to say so.
        if (!scopes.IsUnrestricted)
        {
            if (scopes.IsEmpty)
            {
                // No grants (no principal, no access, or the resolver failed) says nothing about
                // documents the cloud has no opinion on. Only cloud-governed documents — those
                // with a resource_uri — are excluded; everything else (uploads, SFTP, ...) still
                // shows. This also means a resolver outage degrades to "you see the non-cloud
                // documents", not "you see nothing".
                whereClauses.Add("d.resource_uri IS NULL");
            }
            else
            {
                // A document with no resource URI has no cloud coordinate to check permissions
                // against — uploads and connectors that never report one (SFTP, filesystem,
                // MinIO) are like this by design, not by accident. Such a document falls back to
                // Connapse's own access control (container/source reachability) instead of cloud
                // grants, so it is admitted unconditionally here. Cloud scope filtering only
                // narrows the subset of documents that do carry a cloud address.
                var ors = new List<string>();
                for (int i = 0; i < scopes.Matches.Count; i++)
                {
                    GrantMatch match = scopes.Matches[i];

                    // An object-scoped grant is one object. As a prefix it would also admit
                    // "report.pdf.bak", which is a different object nobody named.
                    ors.Add(match.IsExact
                        ? $"d.resource_uri = @scope{i}"
                        : $"d.resource_uri LIKE @scope{i} ESCAPE '{SearchScopes.LikeEscape}'");

                    parameters.Add(new NpgsqlParameter($"@scope{i}", NpgsqlDbType.Text)
                    {
                        Value = match.IsExact ? match.Value : SearchScopes.ToLikePattern(match.Value)
                    });
                }

                whereClauses.Add($"(d.resource_uri IS NULL OR ({string.Join(" OR ", ors)}))");
            }
        }

        var dims = queryVector.Length;
        var select = $@"
                cv.chunk_id as ""ChunkId"",
                cv.document_id as ""DocumentId"",
                cv.owner_id as ""ContainerId"",
                (cv.embedding::vector({dims}) <=> @queryVector) as ""Distance"",
                c.content as ""Content"",
                c.chunk_index as ""ChunkIndex"",
                d.file_name as ""FileName"",
                d.content_type as ""ContentType"",
                d.path as ""Path""";

        // Exact search: every filter applied before the LIMIT. Always complete, and fast for small
        // containers, but it reads every candidate row, so it takes seconds on large ones (#571).
        // The dimension cast is needed because the embedding column is unconstrained.
        var exactSql = $@"
            SELECT {select}
            FROM chunk_vectors cv
            INNER JOIN chunks c ON cv.chunk_id = c.id
            INNER JOIN documents d ON cv.document_id = d.id
            WHERE {string.Join(" AND ", vectorClauses.Concat(whereClauses))}
            ORDER BY ""Distance"" ASC
            LIMIT @topK";

        List<VectorSearchRow> results;
        if (hasContainer && hasModel && dims <= VectorIndexMaxDimensions)
        {
            // Neighbours first (#571): the nearest vectors of one container and model, where that
            // container's HNSW index (if it is large enough to have one) serves the ORDER BY, then
            // the joins and document filters over an over-fetched list, re-sorted by exact distance.
            SearchSettings settings = _searchSettings.CurrentValue;
            int inner = Math.Max(topK * 4, 100);
            parameters.Add(new NpgsqlParameter("@inner", NpgsqlDbType.Integer) { Value = inner });
            var neighboursSql = $@"
                WITH nn AS MATERIALIZED (
                    SELECT cv.chunk_id
                    FROM chunk_vectors cv
                    WHERE {string.Join(" AND ", vectorClauses)}
                    ORDER BY cv.embedding::halfvec({dims}) <=> @queryVector::halfvec({dims})
                    LIMIT @inner)
                SELECT {select}
                FROM nn
                INNER JOIN chunk_vectors cv ON cv.chunk_id = nn.chunk_id
                INNER JOIN chunks c ON cv.chunk_id = c.id
                INNER JOIN documents d ON cv.document_id = d.id
                WHERE {string.Join(" AND ", whereClauses)}
                ORDER BY ""Distance"" ASC
                LIMIT @topK";

            results = await WithIndexSearchSettingsAsync(
                Math.Max(settings.VectorIndexEfSearch, inner),
                () => _context.Database.SqlQueryRaw<VectorSearchRow>(neighboursSql, parameters.ToArray()).ToListAsync(ct),
                ct);

            // Document filters (permissions, path, revoked sources) can leave fewer than topK of the
            // over-fetched neighbours; the exact query applies them before the LIMIT, so it finds
            // the rest. A container with fewer than topK vectors ends up here too, cheaply.
            if (results.Count < topK)
                results = await _context.Database
                    .SqlQueryRaw<VectorSearchRow>(exactSql, CloneParameters(parameters))
                    .ToListAsync(ct);
        }
        else
        {
            results = await _context.Database
                .SqlQueryRaw<VectorSearchRow>(exactSql, parameters.ToArray())
                .ToListAsync(ct);
        }

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

    /// <summary>pgvector indexes halfvec up to 4,000 dimensions; beyond that, search stays exact.</summary>
    private const int VectorIndexMaxDimensions = 4_000;

    /// <summary>
    /// Runs a query with pgvector's HNSW search settings for this statement only: <paramref name="efSearch"/>
    /// candidates, and iterative scans so an index scan keeps going until the LIMIT is met. Inside a
    /// caller's transaction the defaults stand, because SET LOCAL would outlive this query.
    /// </summary>
    private async Task<T> WithIndexSearchSettingsAsync<T>(int efSearch, Func<Task<T>> query, CancellationToken ct)
    {
        if (_context.Database.CurrentTransaction is not null)
            return await query();

        await using var transaction = await _context.Database.BeginTransactionAsync(ct);
        await _context.Database.ExecuteSqlRawAsync(
            $"SET LOCAL hnsw.ef_search = {Math.Clamp(efSearch, 1, 1000)}; SET LOCAL hnsw.iterative_scan = relaxed_order;", ct);
        T result = await query();
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>An NpgsqlParameter belongs to one command; a second query needs its own copies.</summary>
    private static object[] CloneParameters(List<NpgsqlParameter> parameters) =>
        parameters.Select(p => (object)p.Clone()).ToArray();

    public async Task<IReadOnlyDictionary<string, float>> ScoreChunksAsync(
        float[] queryVector,
        IReadOnlyCollection<string> chunkIds,
        string modelId,
        CancellationToken ct = default)
    {
        if (queryVector == null || queryVector.Length == 0)
            throw new ArgumentException("Query vector cannot be null or empty", nameof(queryVector));

        Guid[] ids = chunkIds
            .Select(id => Guid.TryParse(id, out Guid g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, float>();

        int dims = queryVector.Length;
        string sql = $@"
            SELECT
                cv.chunk_id as ""ChunkId"",
                (cv.embedding::vector({dims}) <=> @queryVector) as ""Distance""
            FROM chunk_vectors cv
            WHERE cv.chunk_id = ANY(@chunkIds)
              AND cv.model_id = @modelId";

        List<ChunkDistanceRow> rows = await _context.Database
            .SqlQueryRaw<ChunkDistanceRow>(
                sql,
                new NpgsqlParameter("@queryVector", new Vector(queryVector)),
                new NpgsqlParameter("@chunkIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids },
                new NpgsqlParameter("@modelId", NpgsqlDbType.Text) { Value = modelId })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.ChunkId.ToString(), r => (float)(1.0 - r.Distance));
    }

    private record ChunkDistanceRow(Guid ChunkId, double Distance);

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
