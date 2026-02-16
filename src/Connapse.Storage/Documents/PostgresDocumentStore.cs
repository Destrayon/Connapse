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
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        var entity = new DocumentEntity
        {
            Id = string.IsNullOrEmpty(document.Id) ? Guid.NewGuid() : Guid.Parse(document.Id),
            FileName = document.FileName,
            ContentType = document.ContentType,
            CollectionId = document.CollectionId,
            VirtualPath = string.Empty, // Will be set by ingestion pipeline
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
            "Stored document {DocumentId} ({FileName}, {SizeBytes} bytes)",
            entity.Id,
            entity.FileName,
            entity.SizeBytes);

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

        if (entity == null)
        {
            return null;
        }

        return new Document(
            entity.Id.ToString(),
            entity.FileName,
            entity.ContentType,
            entity.CollectionId,
            entity.SizeBytes,
            entity.CreatedAt,
            entity.Metadata);
    }

    public async Task<IReadOnlyList<Document>> ListAsync(
        string? collectionId = null,
        CancellationToken ct = default)
    {
        var query = _context.Documents.AsNoTracking();

        if (!string.IsNullOrEmpty(collectionId))
        {
            query = query.Where(d => d.CollectionId == collectionId);
        }

        var entities = await query
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(e => new Document(
            e.Id.ToString(),
            e.FileName,
            e.ContentType,
            e.CollectionId,
            e.SizeBytes,
            e.CreatedAt,
            e.Metadata)).ToList();
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
}
