using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.Documents;

/// <summary>
/// PostgreSQL-backed document store implementation.
/// Handles CRUD operations for documents in the knowledge base.
/// </summary>
public class PostgresDocumentStore : IDocumentStore
{
    private readonly KnowledgeDbContext _context;
    private readonly ILogger<PostgresDocumentStore> _logger;

    public PostgresDocumentStore(
        KnowledgeDbContext context,
        ILogger<PostgresDocumentStore> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<string> StoreAsync(Document document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var entity = new DocumentEntity
        {
            Id = string.IsNullOrEmpty(document.Id) ? Guid.NewGuid() : Guid.Parse(document.Id),
            ContainerId = Guid.Parse(document.ContainerId),
            FileName = document.FileName,
            ContentType = document.ContentType,
            Path = document.Path,
            ContentHash = string.Empty, // Will be set by ingestion pipeline
            SizeBytes = document.SizeBytes,
            ChunkCount = 0,
            Status = "Pending",
            CreatedAt = document.CreatedAt,
            Metadata = document.Metadata ?? new Dictionary<string, string>()
        };

        _context.Documents.Add(entity);
        await _context.SaveChangesAsync(ct);

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
            _logger.LogWarning("Invalid document ID format: {DocumentId}", documentId);
            return null;
        }

        var entity = await _context.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == guid, ct);

        return entity is null ? null : MapToModel(entity);
    }

    public async Task<IReadOnlyList<Document>> ListAsync(
        Guid containerId,
        string? pathPrefix = null,
        CancellationToken ct = default)
    {
        var query = _context.Documents
            .AsNoTracking()
            .Where(d => d.ContainerId == containerId);

        if (!string.IsNullOrEmpty(pathPrefix))
        {
            query = query.Where(d => d.Path.StartsWith(pathPrefix));
        }

        var entities = await query
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(MapToModel).ToList();
    }

    public async Task DeleteAsync(string documentId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(documentId, out var guid))
        {
            _logger.LogWarning("Invalid document ID format: {DocumentId}", documentId);
            return;
        }

        var entity = await _context.Documents
            .FirstOrDefaultAsync(d => d.Id == guid, ct);

        if (entity == null)
        {
            _logger.LogWarning("Document not found: {DocumentId}", documentId);
            return;
        }

        _context.Documents.Remove(entity);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Deleted document {DocumentId} ({FileName})",
            documentId,
            entity.FileName);
    }

    public async Task<bool> ExistsByPathAsync(Guid containerId, string path, CancellationToken ct = default)
    {
        return await _context.Documents
            .AnyAsync(d => d.ContainerId == containerId && d.Path == path, ct);
    }

    private static Document MapToModel(DocumentEntity entity)
    {
        // Merge entity columns into metadata so the API exposes them
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
