using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;

namespace Connapse.Storage.Connectors;

/// <summary>
/// Scope for a filesystem source: where to read from and what to pick up.
/// <para>
/// The AllowUpload, AllowDelete, and AllowCreateFolder flags are gone (#352). They existed
/// when a filesystem directory could be a browsable container, and encoded a per-container
/// answer to "may this be written to?". A filesystem directory is now a source, and the rule
/// is uniform with no exceptions: if Connapse does not own the bytes, they are not mutated
/// through Connapse. The flags were UI hints rather than enforcement in any case — write
/// capability is a type guarantee now, since only MinioConnector implements IWritableConnector.
/// </para>
/// </summary>
public record FilesystemConnectorConfig
{
    public string RootPath { get; init; } = "";
    public IReadOnlyList<string> IncludePatterns { get; init; } = [];
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];
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

    /// <summary>
    /// The resolved configuration, exposed so tests can assert on what ConnectorFactory
    /// recombined from a connection and a source — in particular the root the source is
    /// confined to.
    /// </summary>
    internal FilesystemConnectorConfig Config => _config;

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

    public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
    {
        var rootDir = string.IsNullOrEmpty(prefix)
            ? _config.RootPath
            : GetFullPath(prefix);

        if (!Directory.Exists(rootDir))
            return Task.FromResult<IReadOnlyList<ConnectorFile>>([]);

        var files = new List<ConnectorFile>();

        // Reparse points are skipped rather than followed (#365). The bare
        // SearchOption.AllDirectories overload descends through junctions and symlinks, so a
        // link planted inside the root pulled its target's contents into the index. Each entry
        // is also re-confined below rather than trusted from the walk: the root was validated
        // once at construction, but this runs every sync cycle for the life of the process,
        // and a directory swapped for a link in between would otherwise win (CWE-367).
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };

        string confinementRoot = _config.RootPath;

        foreach (var filePath in Directory.EnumerateFiles(rootDir, "*", options))
        {
            if (PathConfinement.ResolveWithin(confinementRoot, filePath) is null)
                continue;

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
        // An absolute path used to be returned unchecked, "for watcher events" — which meant
        // any caller handing in a rooted path read straight past the root. Watcher events are
        // now confined like everything else: they originate inside the root, so a genuine one
        // still resolves, and one that does not is exactly what this must refuse (#365).
        string? resolved = Path.IsPathRooted(path)
            ? PathConfinement.ResolveWithin(_config.RootPath, path)
            : PathConfinement.CombineWithin(_config.RootPath, path);

        return resolved
            ?? throw new UnauthorizedAccessException(
                $"Path '{path}' resolves outside the connector root directory.");
    }

    private static bool MatchesGlob(string fileName, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";
        return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase);
    }
}
