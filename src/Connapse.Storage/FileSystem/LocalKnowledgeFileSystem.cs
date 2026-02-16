using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.FileSystem;

public class LocalKnowledgeFileSystem : IKnowledgeFileSystem
{
    private readonly string _rootPath;

    public LocalKnowledgeFileSystem(IOptions<KnowledgeFileSystemOptions> options)
    {
        _rootPath = Path.GetFullPath(options.Value.RootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public string RootPath => _rootPath;

    public string ResolvePath(string virtualPath)
    {
        var normalized = virtualPath
            .Replace('\\', '/')
            .TrimStart('/')
            .Replace('/', Path.DirectorySeparatorChar);

        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, normalized));

        if (!fullPath.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"Path '{virtualPath}' resolves outside the root directory.");

        return fullPath;
    }

    public Task EnsureDirectoryExistsAsync(string virtualPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var physicalPath = ResolvePath(virtualPath);
        Directory.CreateDirectory(physicalPath);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FileSystemEntry>> ListAsync(string virtualPath = "/", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var physicalPath = ResolvePath(virtualPath);

        if (!Directory.Exists(physicalPath))
            return Task.FromResult<IReadOnlyList<FileSystemEntry>>(Array.Empty<FileSystemEntry>());

        var entries = new List<FileSystemEntry>();

        foreach (var dir in Directory.EnumerateDirectories(physicalPath))
        {
            ct.ThrowIfCancellationRequested();
            var info = new DirectoryInfo(dir);
            entries.Add(new FileSystemEntry(
                Name: info.Name,
                VirtualPath: ToVirtualPath(dir),
                IsDirectory: true,
                SizeBytes: 0,
                LastModifiedUtc: info.LastWriteTimeUtc));
        }

        foreach (var file in Directory.EnumerateFiles(physicalPath))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            entries.Add(new FileSystemEntry(
                Name: info.Name,
                VirtualPath: ToVirtualPath(file),
                IsDirectory: false,
                SizeBytes: info.Length,
                LastModifiedUtc: info.LastWriteTimeUtc));
        }

        return Task.FromResult<IReadOnlyList<FileSystemEntry>>(entries);
    }

    public Task<bool> ExistsAsync(string virtualPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var physicalPath = ResolvePath(virtualPath);
        var exists = File.Exists(physicalPath) || Directory.Exists(physicalPath);
        return Task.FromResult(exists);
    }

    public async Task SaveFileAsync(string virtualPath, Stream content, CancellationToken ct = default)
    {
        var physicalPath = ResolvePath(virtualPath);
        var directory = Path.GetDirectoryName(physicalPath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        await using var fileStream = new FileStream(
            physicalPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await content.CopyToAsync(fileStream, ct);
    }

    public Task<Stream> OpenFileAsync(string virtualPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var physicalPath = ResolvePath(virtualPath);

        if (!File.Exists(physicalPath))
            throw new FileNotFoundException($"File not found: {virtualPath}", virtualPath);

        Stream stream = new FileStream(
            physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string virtualPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var physicalPath = ResolvePath(virtualPath);

        if (File.Exists(physicalPath))
        {
            File.Delete(physicalPath);
        }
        else if (Directory.Exists(physicalPath))
        {
            Directory.Delete(physicalPath, recursive: true);
        }

        return Task.CompletedTask;
    }

    private string ToVirtualPath(string physicalPath)
    {
        var relative = Path.GetRelativePath(_rootPath, physicalPath);
        return "/" + relative.Replace(Path.DirectorySeparatorChar, '/');
    }
}
