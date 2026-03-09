namespace Connapse.Core.Interfaces;

public interface IFolderStore
{
    Task<Folder> CreateAsync(Guid containerId, string path, CancellationToken ct = default);
    Task<IReadOnlyList<Folder>> ListAsync(Guid containerId, string? parentPath = null, int skip = 0, int take = 50, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid containerId, string path, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid containerId, string path, CancellationToken ct = default);

    /// <summary>
    /// Walks up from the given file path, deleting any ancestor folders that contain
    /// no documents and no sub-folders. Stops at the root or at the first non-empty ancestor.
    /// </summary>
    Task DeleteEmptyAncestorsAsync(Guid containerId, string filePath, CancellationToken ct = default);
}
