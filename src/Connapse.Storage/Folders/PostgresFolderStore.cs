using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.Folders;

public class PostgresFolderStore(
    IDbContextFactory<KnowledgeDbContext> factory,
    ILogger<PostgresFolderStore> logger) : IFolderStore
{
    public async Task<Folder> CreateAsync(Guid containerId, string path, CancellationToken ct = default)
    {
        var normalizedPath = PathUtilities.NormalizeFolderPath(path);

        await using var context = await factory.CreateDbContextAsync(ct);

        var exists = await context.Folders
            .AnyAsync(f => f.ContainerId == containerId && f.Path == normalizedPath, ct);

        if (exists)
            throw new InvalidOperationException($"Folder '{normalizedPath}' already exists in this container.");

        var entity = new FolderEntity
        {
            Id = Guid.NewGuid(),
            ContainerId = containerId,
            Path = normalizedPath,
            CreatedAt = DateTime.UtcNow
        };

        context.Folders.Add(entity);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Created folder {Path} in container {ContainerId}", normalizedPath, containerId);

        return new Folder(entity.Id.ToString(), entity.ContainerId.ToString(), entity.Path, entity.CreatedAt);
    }

    public async Task<IReadOnlyList<Folder>> ListAsync(
        Guid containerId,
        string? parentPath = null,
        int skip = 0,
        int take = 50,
        CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var normalizedParent = PathUtilities.NormalizeFolderPath(parentPath ?? "/");

        var entities = await context.Folders
            .AsNoTracking()
            .Where(f => f.ContainerId == containerId && f.Path.StartsWith(normalizedParent) && f.Path != normalizedParent)
            .OrderBy(f => f.Path)
            .ToListAsync(ct);

        // Filter to immediate children only (exclude deeply nested subfolders)
        var immediateChildren = entities.Where(e =>
        {
            var relative = e.Path[normalizedParent.Length..].TrimEnd('/');
            return relative.Length > 0 && !relative.Contains('/');
        }).Skip(skip).Take(take).ToList();

        return immediateChildren.Select(e =>
            new Folder(e.Id.ToString(), e.ContainerId.ToString(), e.Path, e.CreatedAt)).ToList();
    }

    public async Task<bool> DeleteAsync(Guid containerId, string path, CancellationToken ct = default)
    {
        var normalizedPath = PathUtilities.NormalizeFolderPath(path);

        await using var context = await factory.CreateDbContextAsync(ct);

        var foldersToDelete = await context.Folders
            .Where(f => f.ContainerId == containerId && f.Path.StartsWith(normalizedPath))
            .ToListAsync(ct);

        if (foldersToDelete.Count == 0)
            return false;

        var documentsToDelete = await context.Documents
            .Where(d => d.ContainerId == containerId && d.Path.StartsWith(normalizedPath))
            .ToListAsync(ct);

        if (documentsToDelete.Count > 0)
        {
            context.Documents.RemoveRange(documentsToDelete);
            logger.LogInformation(
                "Cascade deleting {Count} documents under folder {Path} in container {ContainerId}",
                documentsToDelete.Count, normalizedPath, containerId);
        }

        context.Folders.RemoveRange(foldersToDelete);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Deleted folder {Path} and {SubCount} sub-folders in container {ContainerId}",
            normalizedPath, foldersToDelete.Count - 1, containerId);

        return true;
    }

    public async Task<bool> ExistsAsync(Guid containerId, string path, CancellationToken ct = default)
    {
        var normalizedPath = PathUtilities.NormalizeFolderPath(path);

        await using var context = await factory.CreateDbContextAsync(ct);

        return await context.Folders
            .AnyAsync(f => f.ContainerId == containerId && f.Path == normalizedPath, ct);
    }
}
