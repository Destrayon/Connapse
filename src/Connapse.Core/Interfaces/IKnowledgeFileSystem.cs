namespace Connapse.Core.Interfaces;

/// <summary>
/// Manages a virtual file system that maps paths like "/folder/a/b" to physical directories
/// under a configurable root. Used by both the Web UI and CLI.
/// </summary>
public interface IKnowledgeFileSystem
{
    /// <summary>The absolute physical root directory.</summary>
    string RootPath { get; }

    /// <summary>
    /// Resolves a virtual path to an absolute physical path.
    /// Throws if the resolved path escapes the root directory.
    /// </summary>
    string ResolvePath(string virtualPath);

    /// <summary>Ensures all directories in the virtual path exist on disk.</summary>
    Task EnsureDirectoryExistsAsync(string virtualPath, CancellationToken ct = default);

    /// <summary>Lists files and subdirectories at a virtual path.</summary>
    Task<IReadOnlyList<FileSystemEntry>> ListAsync(string virtualPath = "/", CancellationToken ct = default);

    /// <summary>Checks whether a file or directory exists at the virtual path.</summary>
    Task<bool> ExistsAsync(string virtualPath, CancellationToken ct = default);

    /// <summary>Writes a stream to a file at the virtual path, creating directories as needed.</summary>
    Task SaveFileAsync(string virtualPath, Stream content, CancellationToken ct = default);

    /// <summary>Opens a file for reading at the virtual path.</summary>
    Task<Stream> OpenFileAsync(string virtualPath, CancellationToken ct = default);

    /// <summary>Deletes a file or directory (recursively) at the virtual path.</summary>
    Task DeleteAsync(string virtualPath, CancellationToken ct = default);
}
