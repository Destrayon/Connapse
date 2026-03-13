using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.Connectors;

/// <summary>
/// Per-container configuration stored as JSON in containers.connector_config.
/// </summary>
public record FilesystemConnectorConfig
{
    public string RootPath { get; init; } = "";
    public IReadOnlyList<string> IncludePatterns { get; init; } = [];
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];
    /// <summary>When false, hides delete buttons in the UI for this container.</summary>
    public bool AllowDelete { get; init; } = true;
    /// <summary>When false, hides the upload button and disables drag-and-drop for this container.</summary>
    public bool AllowUpload { get; init; } = true;
    /// <summary>When false, hides the New Folder button for this container.</summary>
    public bool AllowCreateFolder { get; init; } = true;
}

/// <summary>
/// IConnector implementation backed by a local filesystem directory.
/// SupportsLiveWatch = true — yields ConnectorFileEvents from FileSystemWatcher.
/// All paths handled by this connector are OS-native (absolute or relative to RootPath).
/// </summary>
public class FilesystemConnector : IConnector
{
    private readonly FilesystemConnectorConfig _config;

    public FilesystemConnector(FilesystemConnectorConfig config)
    {
        _config = config;
    }

    public ConnectorType Type => ConnectorType.Filesystem;
    public bool SupportsLiveWatch => true;
    public bool SupportsWrite => true;

    public string RootPath => _config.RootPath;

    public string ResolveJobPath(string relativePath) =>
        Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found at '{fullPath}'.", fullPath);

        return Task.FromResult<Stream>(new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true));
    }

    public async Task WriteFileAsync(string path, Stream content, string? contentType = null, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await content.CopyToAsync(fs, ct);
    }

    public Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
    {
        var rootDir = string.IsNullOrEmpty(prefix)
            ? _config.RootPath
            : GetFullPath(prefix);

        if (!Directory.Exists(rootDir))
            return Task.FromResult<IReadOnlyList<ConnectorFile>>([]);

        var files = new List<ConnectorFile>();
        foreach (var filePath in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(filePath);

            if (_config.IncludePatterns.Count > 0 && !_config.IncludePatterns.Any(p => MatchesGlob(fileName, p)))
                continue;

            if (_config.ExcludePatterns.Any(p => MatchesGlob(fileName, p)))
                continue;

            var info = new FileInfo(filePath);
            files.Add(new ConnectorFile(
                Path: filePath,
                SizeBytes: info.Length,
                LastModified: info.LastWriteTimeUtc,
                ContentType: null));
        }

        return Task.FromResult<IReadOnlyList<ConnectorFile>>(files);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(File.Exists(GetFullPath(path)));

    public async IAsyncEnumerable<ConnectorFileEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(_config.RootPath))
            throw new DirectoryNotFoundException($"Filesystem connector root path not found: '{_config.RootPath}'");

        var channel = Channel.CreateUnbounded<ConnectorFileEvent>(
            new UnboundedChannelOptions { SingleReader = true });

        using var watcher = new FileSystemWatcher(_config.RootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };

        FileSystemEventHandler onCreated = (_, e) =>
            channel.Writer.TryWrite(new ConnectorFileEvent(ConnectorFileEventType.Created, e.FullPath));

        FileSystemEventHandler onChanged = (_, e) =>
            channel.Writer.TryWrite(new ConnectorFileEvent(ConnectorFileEventType.Changed, e.FullPath));

        FileSystemEventHandler onDeleted = (_, e) =>
            channel.Writer.TryWrite(new ConnectorFileEvent(ConnectorFileEventType.Deleted, e.FullPath));

        RenamedEventHandler onRenamed = (_, e) =>
            channel.Writer.TryWrite(new ConnectorFileEvent(ConnectorFileEventType.Renamed, e.FullPath, e.OldFullPath));

        watcher.Created += onCreated;
        watcher.Changed += onChanged;
        watcher.Deleted += onDeleted;
        watcher.Renamed += onRenamed;

        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(ct))
            {
                var fileName = Path.GetFileName(ev.Path);

                if (_config.IncludePatterns.Count > 0 && !_config.IncludePatterns.Any(p => MatchesGlob(fileName, p)))
                    continue;

                if (_config.ExcludePatterns.Any(p => MatchesGlob(fileName, p)))
                    continue;

                yield return ev;
            }
        }
        finally
        {
            watcher.Created -= onCreated;
            watcher.Changed -= onChanged;
            watcher.Deleted -= onDeleted;
            watcher.Renamed -= onRenamed;
            channel.Writer.TryComplete();
        }
    }

    private string GetFullPath(string path)
    {
        // If already absolute and not under RootPath, use as-is (for watcher events)
        if (Path.IsPathRooted(path))
            return path;

        var fullPath = Path.GetFullPath(Path.Combine(_config.RootPath, path.TrimStart('/', '\\')));

        // Prevent path traversal — resolved path must stay within the connector's root
        var rootFull = Path.GetFullPath(_config.RootPath);
        if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"Path '{path}' resolves outside the connector root directory.");

        return fullPath;
    }

    private static bool MatchesGlob(string fileName, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";
        return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase);
    }
}
