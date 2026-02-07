namespace AIKnowledge.Core.Interfaces;

public interface IFolderStore
{
    Task<Folder> CreateAsync(Guid containerId, string path, CancellationToken ct = default);
    Task<IReadOnlyList<Folder>> ListAsync(Guid containerId, string? parentPath = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid containerId, string path, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid containerId, string path, CancellationToken ct = default);
}
