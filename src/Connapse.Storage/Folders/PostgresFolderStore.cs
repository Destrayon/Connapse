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
    IContainerStore containerStore,
    IDocumentStore documents,
    IManagedStorageProvider storage,
    ILogger<PostgresFolderStore> logger) : IFolderStore
{
    private const int StoredFileDeleteAttempts = 3;

    /// <summary>How long a removal saved with a folder delete waits for the delete's own attempt before the job may take it.</summary>
    public static readonly TimeSpan InlineRemovalGrace = TimeSpan.FromMinutes(5);

    public async Task<Folder> CreateAsync(Guid containerId, string path, CancellationToken ct = default)
    {
        var normalizedPath = PathUtilities.NormalizeFolderPath(path);

        await using var context = await factory.CreateDbContextAsync(ct);

        var exists = await context.Folders
            .AnyAsync(f => f.ContainerId == containerId && f.Path == normalizedPath, ct);

        if (exists)
            throw new InvalidOperationException($"Folder '{normalizedPath}' already exists in this container.");

        // Build list of all ancestor paths that need to exist (mkdir -p behavior)
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var ancestorPaths = new List<string>();
        for (var i = 1; i < segments.Length; i++)
        {
            ancestorPaths.Add("/" + string.Join("/", segments.Take(i)) + "/");
        }

        if (ancestorPaths.Count > 0)
        {
            var existingPaths = await context.Folders
                .Where(f => f.ContainerId == containerId && ancestorPaths.Contains(f.Path))
                .Select(f => f.Path)
                .ToListAsync(ct);

            var now = DateTime.UtcNow;
            foreach (var ancestor in ancestorPaths.Where(a => !existingPaths.Contains(a)))
            {
                context.Folders.Add(new FolderEntity
                {
                    Id = Guid.NewGuid(),
                    ContainerId = containerId,
                    Path = ancestor,
                    CreatedAt = now
                });
            }
        }

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

    public async Task<Folder> EnsureAsync(Guid containerId, string path, CancellationToken ct = default)
    {
        var normalizedPath = PathUtilities.NormalizeFolderPath(path);
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        await using var context = await factory.CreateDbContextAsync(ct);

        // ON CONFLICT DO NOTHING, so two uploads creating the same folder at once both get it. A folder delete may
        // remove it before it is read back, so this tries again rather than hand back a folder that is gone.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            for (int i = 1; i <= segments.Length; i++)
            {
                string ancestor = "/" + string.Join("/", segments.Take(i)) + "/";
                await context.Database.ExecuteSqlAsync(
                    $"INSERT INTO folders (id, container_id, path, created_at) VALUES (gen_random_uuid(), {containerId}, {ancestor}, now()) ON CONFLICT (container_id, path) DO NOTHING",
                    ct);
            }

            var entity = await context.Folders.AsNoTracking()
                .FirstOrDefaultAsync(f => f.ContainerId == containerId && f.Path == normalizedPath, ct);
            if (entity is not null)
                return new Folder(entity.Id.ToString(), entity.ContainerId.ToString(), entity.Path, entity.CreatedAt);
        }

        throw new InvalidOperationException($"Folder '{normalizedPath}' kept being deleted while it was being created.");
    }

    public async Task<bool> DeleteAsync(Guid containerId, string path, CancellationToken ct = default)
    {
        var normalizedPath = PathUtilities.NormalizeFolderPath(path);

        await using var context = await factory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        // Locking the folders first is what keeps an upload whole: one storing a document into any of them holds
        // FOR KEY SHARE until it has, so this waits for it and the documents read below include it; one that comes
        // after finds its folder gone and removes its own stored file.
        var foldersToDelete = await context.Folders
            .FromSql($"SELECT * FROM folders WHERE container_id = {containerId} AND starts_with(path, {normalizedPath}) FOR UPDATE")
            .ToListAsync(ct);

        if (foldersToDelete.Count == 0)
            return false;

        var documentsToDelete = await context.Documents
            .Where(d => d.ContainerId == containerId && d.Path.StartsWith(normalizedPath))
            .ToListAsync(ct);

        bool anyDeletedHadSummary = documentsToDelete.Any(d => !string.IsNullOrEmpty(d.Summary));

        if (documentsToDelete.Count > 0)
        {
            context.Documents.RemoveRange(documentsToDelete);
            logger.LogInformation(
                "Cascade deleting {Count} documents under folder {Path} in container {ContainerId}",
                documentsToDelete.Count, normalizedPath, containerId);
        }

        context.Folders.RemoveRange(foldersToDelete);

        // Saved with the delete, so a removal that keeps failing or that a crash cuts off is still owed, and
        // StoredFileRemovalJob retries it (CON-100). Not due until the removal below has had its chance.
        DateTime now = DateTime.UtcNow;
        List<StoredFileRemovalEntity> removals = [.. documentsToDelete.Select(d => new StoredFileRemovalEntity
        {
            Id = Guid.NewGuid(),
            ContainerId = containerId,
            Path = d.Path,
            CreatedAt = now,
            NextAttemptAt = now + InlineRemovalGrace,
        })];
        context.StoredFileRemovals.AddRange(removals);

        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(CancellationToken.None);

        // Stored files go only once the delete is saved, so a delete that fails before then leaves every file its
        // records point to (CON-94). Past the commit the delete isn't cancelled, since stopping would leave files
        // nothing points to.
        await DeleteStoredFilesAsync(containerId, removals);

        // If any cascade-deleted doc contributed to the container summary, mark stale so the
        // next sweep tick triggers a re-rollup. Same reason as PostgresDocumentStore.DeleteAsync
        // (pure deletes don't otherwise show up in the sweep's "newer doc timestamp" query).
        if (anyDeletedHadSummary)
        {
            await containerStore.MarkSummaryStaleAsync(containerId, CancellationToken.None);
        }

        logger.LogInformation("Deleted folder {Path} and {SubCount} sub-folders in container {ContainerId}",
            normalizedPath, foldersToDelete.Count - 1, containerId);

        return true;
    }

    private async Task DeleteStoredFilesAsync(Guid containerId, IReadOnlyCollection<StoredFileRemovalEntity> removals)
    {
        if (removals.Count == 0)
            return;

        IConnector connector;
        try
        {
            connector = storage.CreateConnector(containerId.ToString());
        }
        catch (Exception ex)
        {
            // The delete is already saved, so it isn't reported as failed.
            logger.LogError(ex, "Could not reach storage to remove {Count} files in container {ContainerId} after their folder was deleted; they will be retried",
                removals.Count, containerId);
            return;
        }

        try
        {
            if (connector is not IWritableConnector writable)
                return;

            foreach (StoredFileRemovalEntity removal in removals)
            {
                if (await DeleteStoredFileAsync(containerId, writable, removal.Path))
                    await CompleteRemovalAsync(removal.Id);
            }
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
        }
    }

    private async Task<bool> DeleteStoredFileAsync(Guid containerId, IWritableConnector writable, string path)
    {
        // The folders are no longer locked, so an upload may already have written a new file to the same path. The
        // file is removed under that path's lock, and only while no record points to it, so the newer upload's stays.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await documents.RemoveUnrecordedFileAsync(containerId, path,
                    token => writable.DeleteFileAsync(path, token), CancellationToken.None);
                return true;
            }
            catch (Exception ex) when (attempt < StoredFileDeleteAttempts)
            {
                logger.LogWarning(ex, "Could not remove the stored file {Path} in container {ContainerId} (attempt {Attempt}), trying again",
                    LogSanitizer.Sanitize(path), containerId, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
            }
            catch (Exception ex)
            {
                // Its record is already gone, so nothing points to the file; it is logged rather than failing a delete
                // that has been saved.
                logger.LogError(ex, "Could not remove the stored file {Path} in container {ContainerId} after its folder was deleted; it will be retried",
                    LogSanitizer.Sanitize(path), containerId);
                return false;
            }
        }
    }

    private async Task CompleteRemovalAsync(Guid removalId)
    {
        try
        {
            await using var context = await factory.CreateDbContextAsync(CancellationToken.None);
            await context.StoredFileRemovals.Where(r => r.Id == removalId).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The file is gone; the job finds nothing to remove and drops the entry.
            logger.LogWarning(ex, "Could not mark stored-file removal {RemovalId} done", removalId);
        }
    }

    public async Task<bool> ExistsAsync(Guid containerId, string path, CancellationToken ct = default)
    {
        var normalizedPath = PathUtilities.NormalizeFolderPath(path);

        await using var context = await factory.CreateDbContextAsync(ct);

        return await context.Folders
            .AnyAsync(f => f.ContainerId == containerId && f.Path == normalizedPath, ct);
    }

    public async Task DeleteEmptyAncestorsAsync(Guid containerId, string filePath, CancellationToken ct = default)
    {
        var folderPath = PathUtilities.GetParentPath(filePath);

        await using var context = await factory.CreateDbContextAsync(ct);

        while (folderPath != "/")
        {
            var hasDocuments = await context.Documents
                .AnyAsync(d => d.ContainerId == containerId && d.Path.StartsWith(folderPath), ct);

            if (hasDocuments)
                break;

            var hasSubFolders = await context.Folders
                .AnyAsync(f => f.ContainerId == containerId && f.Path != folderPath && f.Path.StartsWith(folderPath), ct);

            if (hasSubFolders)
                break;

            var folder = await context.Folders
                .FirstOrDefaultAsync(f => f.ContainerId == containerId && f.Path == folderPath, ct);

            if (folder is null)
                break;

            context.Folders.Remove(folder);
            await context.SaveChangesAsync(ct);

            logger.LogInformation("Cleaned up empty folder {Path} in container {ContainerId}", folderPath, containerId);

            // Move up to parent
            var parentPath = PathUtilities.GetParentPath(folderPath.TrimEnd('/'));
            if (parentPath == folderPath)
                break;
            folderPath = parentPath;
        }
    }
}
