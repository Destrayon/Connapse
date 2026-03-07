using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<PostgresDocumentStore> _logger;

    public PostgresDocumentStore(
        IDbContextFactory<KnowledgeDbContext> factory,
        ILogger<PostgresDocumentStore> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<string> StoreAsync(Document document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        await using var context = await _factory.CreateDbContextAsync(ct);

        var entity = new DocumentEntity
        {
            Id = string.IsNullOrEmpty(document.Id) ? Guid.NewGuid() : Guid.Parse(document.Id),
            ContainerId = Guid.Parse(document.ContainerId),
            FileName = document.FileName,
            ContentType = document.ContentType,
            Path = document.Path,
            ContentHash = string.Empty,
            SizeBytes = document.SizeBytes,
            ChunkCount = 0,
            Status = "Pending",
            CreatedAt = document.CreatedAt,
            Metadata = document.Metadata ?? new Dictionary<string, string>()
        };

        context.Documents.Add(entity);
        await context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Stored document {DocumentId} ({FileName}, {SizeBytes} bytes) in container {ContainerId}",
            entity.Id,
            entity.FileName,
            entity.SizeBytes,
            entity.ContainerId);

        return entity.Id.ToString();
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

        context.Documents.Remove(entity);
        await context.SaveChangesAsync(ct);

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
            metadata);
    }
}
