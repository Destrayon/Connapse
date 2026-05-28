using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Storage.Documents;

/// <summary>
/// PostgreSQL-backed document store implementation.
/// Uses IDbContextFactory to create a short-lived DbContext per operation,
/// preventing concurrent-access exceptions in Blazor Server circuits.
/// </summary>
public class PostgresDocumentStore : IDocumentStore
{
    private readonly IDbContextFactory<KnowledgeDbContext> _factory;
    private readonly IContainerStore _containerStore;
    private readonly ILogger<PostgresDocumentStore> _logger;

    public PostgresDocumentStore(
        IDbContextFactory<KnowledgeDbContext> factory,
        IContainerStore containerStore,
        ILogger<PostgresDocumentStore> logger)
    {
        _factory = factory;
        _containerStore = containerStore;
        _logger = logger;
    }

    public async Task<StoreResult> StoreAsync(Document document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        await using var context = await _factory.CreateDbContextAsync(ct);

        var docId = string.IsNullOrEmpty(document.Id) ? Guid.NewGuid() : Guid.Parse(document.Id);
        var containerId = Guid.Parse(document.ContainerId);
        var metadata = document.Metadata ?? new Dictionary<string, string>();

        // Atomic upsert: INSERT ... ON CONFLICT (container_id, path) DO UPDATE.
        // On conflict, increments generation so stale ingestion jobs can detect they're outdated.
        // Returns the winning row's id and generation.
        var conn = context.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO documents (id, container_id, file_name, content_type, path, content_hash, size_bytes, chunk_count, generation, status, created_at, metadata)
            VALUES (@id, @cid, @fname, @ctype, @path, @hash, @size, 0, 1, 'Pending', @created, @meta::jsonb)
            ON CONFLICT (container_id, path) DO UPDATE SET
                file_name    = EXCLUDED.file_name,
                content_type = EXCLUDED.content_type,
                content_hash = EXCLUDED.content_hash,
                size_bytes   = EXCLUDED.size_bytes,
                generation   = documents.generation + 1,
                status       = 'Pending',
                metadata     = EXCLUDED.metadata
            RETURNING id, generation
            """;

        var p = cmd.Parameters;
        p.Add(new NpgsqlParameter("id", docId));
        p.Add(new NpgsqlParameter("cid", containerId));
        p.Add(new NpgsqlParameter("fname", document.FileName));
        p.Add(new NpgsqlParameter("ctype", document.ContentType));
        p.Add(new NpgsqlParameter("path", document.Path));
        p.Add(new NpgsqlParameter("hash", string.Empty));
        p.Add(new NpgsqlParameter("size", document.SizeBytes));
        p.Add(new NpgsqlParameter("created", document.CreatedAt));
        p.Add(new NpgsqlParameter("meta", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(metadata) });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var winnerId = reader.GetGuid(0);
        int generation = reader.GetInt32(1);

        // Close reader before running EF commands on the same connection
        await reader.CloseAsync();

        // If an existing row was updated (not our new id), purge its stale chunks.
        if (winnerId != docId)
        {
            await context.Chunks
                .Where(c => c.DocumentId == winnerId)
                .ExecuteDeleteAsync(ct);
        }

        _logger.LogInformation(
            "Stored document {DocumentId} gen={Generation} ({FileName}, {SizeBytes} bytes) in container {ContainerId}",
            winnerId,
            generation,
            document.FileName,
            document.SizeBytes,
            containerId);

        return new StoreResult(winnerId.ToString(), generation);
    }

    public async Task<Document?> GetAsync(string documentId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(documentId, out var guid))
        {
            _logger.LogWarning("Invalid document ID format: {DocumentId}", Sanitize(documentId));
            return null;
        }

        await using var context = await _factory.CreateDbContextAsync(ct);

        var entity = await context.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == guid, ct);

        return entity is null ? null : MapToModel(entity);
    }

    public async Task<IReadOnlyList<Document>> ListAsync(
        Guid containerId,
        string? pathPrefix = null,
        int skip = 0,
        int take = 50,
        CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);

        var query = context.Documents
            .AsNoTracking()
            .Where(d => d.ContainerId == containerId);

        if (!string.IsNullOrEmpty(pathPrefix))
        {
            query = query.Where(d => d.Path.StartsWith(pathPrefix));
        }

        var entities = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return entities.Select(MapToModel).ToList();
    }

    public async Task DeleteAsync(string documentId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(documentId, out var guid))
        {
            _logger.LogWarning("Invalid document ID format: {DocumentId}", Sanitize(documentId));
            return;
        }

        await using var context = await _factory.CreateDbContextAsync(ct);

        var entity = await context.Documents
            .FirstOrDefaultAsync(d => d.Id == guid, ct);

        if (entity == null)
        {
            _logger.LogWarning("Document not found: {DocumentId}", Sanitize(documentId));
            return;
        }

        bool docHadSummary = !string.IsNullOrEmpty(entity.Summary);
        Guid containerId = entity.ContainerId;

        context.Documents.Remove(entity);
        await context.SaveChangesAsync(ct);

        // If the deleted doc contributed to the container summary, mark the container as
        // stale so the next sweep tick triggers a re-rollup. Without this, the sweep query
        // wouldn't detect pure-deletion changes (no doc has a "newer" summary timestamp).
        if (docHadSummary)
        {
            await _containerStore.MarkSummaryStaleAsync(containerId, ct);
        }

        _logger.LogInformation(
            "Deleted document {DocumentId} ({FileName})",
            Sanitize(documentId),
            entity.FileName);
    }

    public async Task<bool> ExistsByPathAsync(Guid containerId, string path, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);

        return await context.Documents
            .AnyAsync(d => d.ContainerId == containerId && d.Path == path && d.Status == "Ready", ct);
    }

    public async Task<Document?> GetByPathAsync(Guid containerId, string path, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);

        var entity = await context.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.ContainerId == containerId && d.Path == path, ct);

        return entity is null ? null : MapToModel(entity);
    }

    public async Task UpdateSummaryAsync(string documentId, string? summary, DateTime? generatedAt, string? contentHash, CancellationToken ct = default)
    {
        if (!Guid.TryParse(documentId, out var guid))
        {
            _logger.LogWarning("Invalid document ID format: {DocumentId}", Sanitize(documentId));
            return;
        }

        await using var context = await _factory.CreateDbContextAsync(ct);

        var entity = await context.Documents.FirstOrDefaultAsync(d => d.Id == guid, ct);
        if (entity is null) return;

        entity.Summary = summary;
        entity.SummaryGeneratedAt = generatedAt;
        entity.SummaryContentHash = contentHash;
        await context.SaveChangesAsync(ct);
    }

    public async Task UpdateIngestionStateAsync(string documentId, IngestionState state, CancellationToken ct = default)
    {
        if (!Guid.TryParse(documentId, out var guid))
        {
            _logger.LogWarning("Invalid document ID format: {DocumentId}", Sanitize(documentId));
            return;
        }

        await using var context = await _factory.CreateDbContextAsync(ct);

        var entity = await context.Documents.FirstOrDefaultAsync(d => d.Id == guid, ct);
        if (entity is null) return;

        entity.IngestionState = state;
        await context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> FindContainersWithStaleSummariesAsync(CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);

        // Stale = container has at least one doc AND something changed more recently than the
        // container's last rollup. "Change" is the latest of three doc-level timestamps:
        //   • SummaryGeneratedAt — bumped when a per-doc summary is written (summary-clustering
        //     mode at ingest; document-clustering mode when a medoid summary is cached)
        //   • LastIndexedAt      — bumped on re-ingestion (content updates)
        //   • CreatedAt          — initial upload
        //
        // Before HERCULES this query filtered to docs with non-null Summary, which silently
        // excluded document-clustering containers (whose docs are mostly null-summary) — the
        // sweep then reported "no stale containers" forever and rollups never fired.
        return await context.Documents
            .GroupBy(d => d.ContainerId)
            .Select(g => new
            {
                ContainerId = g.Key,
                MaxSummary = g.Max(d => d.SummaryGeneratedAt),
                MaxIndexed = g.Max(d => d.LastIndexedAt),
                MaxCreated = g.Max(d => d.CreatedAt),
            })
            .Join(context.Containers,
                  x => x.ContainerId,
                  c => c.Id,
                  (x, c) => new { x.ContainerId, x.MaxSummary, x.MaxIndexed, x.MaxCreated, ContainerSummaryAt = c.SummaryGeneratedAt })
            .Where(x =>
                x.ContainerSummaryAt == null
                || (x.MaxSummary != null && x.MaxSummary > x.ContainerSummaryAt)
                || (x.MaxIndexed != null && x.MaxIndexed > x.ContainerSummaryAt)
                || x.MaxCreated > x.ContainerSummaryAt)
            .Select(x => x.ContainerId)
            .ToListAsync(ct);
    }

    public async Task<ContainerStats> GetContainerStatsAsync(Guid containerId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);

        var stats = await context.Documents
            .AsNoTracking()
            .Where(d => d.ContainerId == containerId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                DocumentCount = g.Count(),
                ReadyCount = g.Count(d => d.Status == "Ready"),
                ProcessingCount = g.Count(d => d.Status == "Processing" || d.Status == "Pending" || d.Status == "Queued"),
                FailedCount = g.Count(d => d.Status == "Failed"),
                TotalChunks = g.Sum(d => (long)d.ChunkCount),
                TotalSizeBytes = g.Sum(d => d.SizeBytes),
                LastIndexedAt = g.Max(d => d.LastIndexedAt)
            })
            .FirstOrDefaultAsync(ct);

        if (stats is null)
            return new ContainerStats(0, 0, 0, 0, 0, 0, null);

        return new ContainerStats(
            stats.DocumentCount,
            stats.ReadyCount,
            stats.ProcessingCount,
            stats.FailedCount,
            stats.TotalChunks,
            stats.TotalSizeBytes,
            stats.LastIndexedAt);
    }

    private static Document MapToModel(DocumentEntity entity)
    {
        var metadata = new Dictionary<string, string>(entity.Metadata ?? new());
        metadata["Status"] = entity.Status;
        metadata["ContentHash"] = entity.ContentHash;
        metadata["ChunkCount"] = entity.ChunkCount.ToString();
        if (!string.IsNullOrEmpty(entity.ErrorMessage))
            metadata["ErrorMessage"] = entity.ErrorMessage;

        return new(
            entity.Id.ToString(),
            entity.ContainerId.ToString(),
            entity.FileName,
            entity.ContentType,
            entity.Path,
            entity.SizeBytes,
            entity.CreatedAt,
            metadata,
            entity.Summary,
            entity.SummaryGeneratedAt,
            entity.SummaryContentHash,
            entity.IngestionState);
    }
}
