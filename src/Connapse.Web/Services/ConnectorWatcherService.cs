using System.Collections.Concurrent;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Connectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Services;

/// <summary>
/// BackgroundService that manages file change detection for all watchable containers.
/// Filesystem containers use FileSystemWatcher with 750ms debounce.
/// Cloud containers (S3, AzureBlob, MinIO) use 5-minute polling with delta detection.
/// A periodic rescan runs every 5 minutes as a fallback for all container types.
/// </summary>
public class ConnectorWatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectorFactory _connectorFactory;
    private readonly IIngestionQueue _queue;
    private readonly FileBrowserChangeNotifier _fileChangeNotifier;
    private readonly IOptionsMonitor<ChunkingSettings> _chunkingSettings;
    private readonly ILogger<ConnectorWatcherService> _logger;

    // Active watch tasks keyed by containerId
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _watcherCts = new();

    // Cancelled on BackgroundService shutdown to stop any watcher created after ExecuteAsync started
    // (i.e. runtime-created watchers triggered by the containers endpoint, which have no stoppingToken).
    private readonly CancellationTokenSource _masterCts = new();

    // Cached root paths per container for virtual-path computation
    private readonly ConcurrentDictionary<Guid, string> _rootPaths = new();

    // In-memory snapshots for cloud container delta detection: containerId → (path → (LastModified, SizeBytes))
    private readonly ConcurrentDictionary<Guid, Dictionary<string, (DateTime LastModified, long SizeBytes)>> _cloudSnapshots = new();

    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan RescanInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CloudPollInterval = TimeSpan.FromMinutes(5);

    public ConnectorWatcherService(
        IServiceScopeFactory scopeFactory,
        IConnectorFactory connectorFactory,
        IIngestionQueue queue,
        FileBrowserChangeNotifier fileChangeNotifier,
        IOptionsMonitor<ChunkingSettings> chunkingSettings,
        ILogger<ConnectorWatcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _connectorFactory = connectorFactory;
        _queue = queue;
        _fileChangeNotifier = fileChangeNotifier;
        _chunkingSettings = chunkingSettings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ensure runtime-created watchers (started via the containers endpoint without a stoppingToken)
        // are also cancelled when the BackgroundService stops.
        stoppingToken.Register(_masterCts.Cancel);

        // Load all watchable containers on startup and start watchers/pollers
        await using var scope = _scopeFactory.CreateAsyncScope();
        var containerStore = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var containers = await containerStore.ListAsync(stoppingToken);

        foreach (var container in containers.Where(c => IsWatchableConnector(c.ConnectorType)))
        {
            StartWatchingContainer(container, stoppingToken);
        }

        // Periodic rescan loop (fallback for FileSystemWatcher buffer overflow + cloud poll)
        using var rescanTimer = new PeriodicTimer(RescanInterval);
        while (await rescanTimer.WaitForNextTickAsync(stoppingToken))
        {
            await RescanAllAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Called when a container is created at runtime.
    /// Starts a FileSystemWatcher (Filesystem) or a polling loop (S3, AzureBlob, MinIO).
    /// </summary>
    public void StartWatchingContainer(Container container, CancellationToken stoppingToken = default)
    {
        if (!IsWatchableConnector(container.ConnectorType))
            return;

        if (_watcherCts.ContainsKey(Guid.Parse(container.Id)))
            return; // already watching

        // Link to both the caller's token and _masterCts so that:
        // - Startup watchers: cancelled by BackgroundService.StopAsync (stoppingToken) OR shutdown
        // - Runtime watchers (endpoint-created, stoppingToken=default): cancelled by shutdown via _masterCts
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _masterCts.Token);
        _watcherCts[Guid.Parse(container.Id)] = cts;

        if (container.ConnectorType == ConnectorType.Filesystem)
        {
            _ = Task.Run(() => WatchContainerAsync(container, cts.Token), cts.Token);
            _ = Task.Run(() => InitialSyncAsync(container, cts.Token), cts.Token);
        }
        else
        {
            _ = Task.Run(() => PollCloudContainerAsync(container, cts.Token), cts.Token);
        }
    }

    /// <summary>
    /// Called when a container is deleted — stops its watcher or polling loop.
    /// </summary>
    public void StopWatchingContainer(Guid containerId)
    {
        if (_watcherCts.TryRemove(containerId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        _cloudSnapshots.TryRemove(containerId, out _);
    }

    private async Task WatchContainerAsync(Container container, CancellationToken ct)
    {
        var containerId = Guid.Parse(container.Id);

        FilesystemConnector connector;
        try
        {
            connector = (FilesystemConnector)_connectorFactory.Create(container);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create Filesystem connector for container {ContainerName} ({ContainerId}). Watcher not started.",
                LogSanitizer.Sanitize(container.Name), containerId);
            return;
        }

        _logger.LogInformation(
            "Starting filesystem watcher for container '{ContainerName}' at '{RootPath}'",
            LogSanitizer.Sanitize(container.Name), LogSanitizer.Sanitize(connector.RootPath));

        _rootPaths[containerId] = connector.RootPath;

        // Track pending (debounced) events: path → (eventType, received at, oldPath)
        var pending = new ConcurrentDictionary<string, (ConnectorFileEventType EventType, DateTime ReceivedAt, string? OldPath)>();

        // Event consumer: fills pending dictionary
        var eventConsumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var ev in connector.WatchAsync(ct))
                    pending[ev.Path] = (ev.EventType, DateTime.UtcNow, ev.OldPath);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Filesystem watcher for container '{ContainerName}' encountered an error.",
                    LogSanitizer.Sanitize(container.Name));
            }
        }, ct);

        // Debounce loop: flush events that have been quiet for DebounceDelay
        using var debounceTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await debounceTimer.WaitForNextTickAsync(ct))
        {
            var now = DateTime.UtcNow;
            var ready = pending
                .Where(kv => (now - kv.Value.ReceivedAt) >= DebounceDelay)
                .ToList();

            foreach (var (path, ev) in ready)
            {
                if (pending.TryRemove(path, out _))
                    await HandleFileEventAsync(container, ev.EventType, path, ev.OldPath, ct);
            }
        }

        await eventConsumer;
    }

    private async Task HandleFileEventAsync(
        Container container,
        ConnectorFileEventType eventType,
        string path,
        string? oldPath,
        CancellationToken ct)
    {
        var containerId = Guid.Parse(container.Id);
        var virtualPath = ComputeVirtualPath(containerId, container, path);

        switch (eventType)
        {
            case ConnectorFileEventType.Created:
            {
                // A Created event fires because a file was just written to disk — either by a
                // UI upload (which already queued its own ingestion job) or by an external tool
                // dropping a brand-new file into the watched folder.
                // If a DB record already exists for this path (any status), the upload endpoint
                // already owns ingestion. Skip — the 5-minute rescan handles retries for Failed.
                // Only ingest when there is no record at all (genuinely new external file).
                await using var checkScope = _scopeFactory.CreateAsyncScope();
                var documentStore = checkScope.ServiceProvider.GetRequiredService<IDocumentStore>();
                var existing = await documentStore.GetByPathAsync(containerId, virtualPath, ct);
                if (existing is not null)
                    break; // already known — skip
                await EnqueueIngestionAsync(container, path, virtualPath, ct, null);
                break;
            }

            case ConnectorFileEventType.Changed:
            {
                // For Changed events: skip if in-flight, but do allow retrying failed files
                // (the user may have replaced the file externally with a corrected version).
                await using var checkScope = _scopeFactory.CreateAsyncScope();
                var documentStore = checkScope.ServiceProvider.GetRequiredService<IDocumentStore>();
                var existing = await documentStore.GetByPathAsync(containerId, virtualPath, ct);
                var status = existing?.Metadata.GetValueOrDefault("Status");
                if (status is "Pending" or "Queued" or "Processing")
                    break; // already in-flight — skip
                await EnqueueIngestionAsync(container, path, virtualPath, ct, existing?.Id);
                break;
            }

            case ConnectorFileEventType.Deleted:
                await DeleteDocumentByVirtualPathAsync(containerId, virtualPath, ct);
                break;

            case ConnectorFileEventType.Renamed:
            {
                if (oldPath is not null)
                {
                    var oldVirtualPath = ComputeVirtualPath(containerId, container, oldPath);
                    await DeleteDocumentByVirtualPathAsync(containerId, oldVirtualPath, ct);
                }

                // Atomic-save editors (VS Code, Notepad++, Word, etc.) rename a temp file to
                // the target, so the original document still exists at `path` in the DB.
                // Reuse its ID — otherwise the pipeline would try to INSERT a new row with the
                // same (ContainerId, Path) and hit the unique constraint.
                await using var renameScope = _scopeFactory.CreateAsyncScope();
                var renameStore = renameScope.ServiceProvider.GetRequiredService<IDocumentStore>();
                var existingAtNew = await renameStore.GetByPathAsync(containerId, virtualPath, ct);
                var existingStatus = existingAtNew?.Metadata.GetValueOrDefault("Status");
                if (existingStatus is "Pending" or "Queued" or "Processing")
                    break; // already in-flight
                await EnqueueIngestionAsync(container, path, virtualPath, ct, existingAtNew?.Id);
                break;
            }
        }
    }

    private async Task EnqueueIngestionAsync(Container container, string filePath, string virtualPath, CancellationToken ct, string? existingDocumentId = null)
    {
        if (!File.Exists(filePath))
            return;

        var fileName = Path.GetFileName(filePath);
        var documentId = existingDocumentId ?? Guid.NewGuid().ToString();
        var contentType = GetContentType(fileName);

        var strategy = Enum.TryParse<ChunkingStrategy>(_chunkingSettings.CurrentValue.Strategy, ignoreCase: true, out var parsed)
            ? parsed
            : ChunkingStrategy.Recursive;

        var job = new IngestionJob(
            JobId: Guid.NewGuid().ToString(),
            DocumentId: documentId,
            Path: filePath,         // absolute path — used by IngestionWorker to open the file
            Options: new IngestionOptions(
                DocumentId: documentId,
                FileName: fileName,
                ContentType: contentType,
                ContainerId: container.Id,
                Path: virtualPath,  // virtual path — stored in DB and used by the file browser
                Strategy: strategy,
                Metadata: new Dictionary<string, string>
                {
                    ["OriginalFileName"] = fileName,
                    ["Source"] = "FilesystemWatcher",
                    ["WatchedAt"] = DateTime.UtcNow.ToString("O")
                }));

        await _queue.EnqueueAsync(job, ct);

        _logger.LogInformation(
            "Enqueued filesystem ingestion for '{FilePath}' (virtual: '{VirtualPath}') in container '{ContainerName}'",
            LogSanitizer.Sanitize(filePath), LogSanitizer.Sanitize(virtualPath), LogSanitizer.Sanitize(container.Name));

        // Notify the file browser so it can show the file immediately with "Queued" status.
        _fileChangeNotifier.NotifyAdded(container.Id, virtualPath, fileName, documentId);
    }

    private async Task DeleteDocumentByVirtualPathAsync(Guid containerId, string virtualPath, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var documentStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var doc = await documentStore.GetByPathAsync(containerId, virtualPath, ct);

            if (doc is null)
                return;

            await _queue.CancelJobForDocumentAsync(doc.Id);
            await documentStore.DeleteAsync(doc.Id, ct);

            _logger.LogInformation(
                "Deleted document for removed file '{VirtualPath}' in container {ContainerId}",
                LogSanitizer.Sanitize(virtualPath), containerId);

            // Notify the file browser so it can remove the entry immediately.
            _fileChangeNotifier.NotifyDeleted(containerId.ToString(), virtualPath, Path.GetFileName(virtualPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error deleting document for path '{VirtualPath}' in container {ContainerId}",
                LogSanitizer.Sanitize(virtualPath), containerId);
        }
    }

    private async Task InitialSyncAsync(Container container, CancellationToken ct)
    {
        var containerId = Guid.Parse(container.Id);
        _logger.LogInformation(
            "Starting initial sync for Filesystem container '{ContainerName}'",
            LogSanitizer.Sanitize(container.Name));

        try
        {
            FilesystemConnector connector;
            try
            {
                connector = (FilesystemConnector)_connectorFactory.Create(container);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Cannot perform initial sync: Filesystem connector config invalid for container '{ContainerName}'.",
                    LogSanitizer.Sanitize(container.Name));
                return;
            }

            var files = await connector.ListFilesAsync(ct: ct);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var documentStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

            // Load all existing documents in one query instead of 2 per-file round-trips.
            var existingDocs = await documentStore.ListAsync(containerId, ct: ct);
            var existingByPath = existingDocs.ToDictionary(d => d.Path);

            int enqueued = 0;
            foreach (var file in files)
            {
                // Compute virtual path (e.g. "/subdir/file.md") relative to the connector root.
                // This is what gets stored in the DB and used by the file browser API.
                var virtualPath = "/" + Path.GetRelativePath(connector.RootPath, file.Path).Replace('\\', '/');

                if (existingByPath.TryGetValue(virtualPath, out var existing))
                {
                    var status = existing.Metadata.GetValueOrDefault("Status");
                    if (status is "Ready" or "Failed")
                        continue; // Ready: already indexed. Failed: pipeline decided — don't auto-retry.

                    // Document exists but is stuck mid-flight (Pending/Processing after a crash) —
                    // reuse its ID so the pipeline updates in place, avoiding the unique-path constraint.
                    await EnqueueIngestionAsync(container, file.Path, virtualPath, ct, existing.Id);
                }
                else
                {
                    await EnqueueIngestionAsync(container, file.Path, virtualPath, ct, null);
                }
                enqueued++;
            }

            _logger.LogInformation(
                "Initial sync for container '{ContainerName}': {Enqueued}/{Total} files enqueued.",
                LogSanitizer.Sanitize(container.Name), enqueued, files.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Initial sync failed for container '{ContainerName}'",
                LogSanitizer.Sanitize(container.Name));
        }
    }

    private async Task PollCloudContainerAsync(Container container, CancellationToken ct)
    {
        var containerId = Guid.Parse(container.Id);
        _logger.LogInformation(
            "Starting cloud polling for {ConnectorType} container '{ContainerName}'",
            container.ConnectorType, LogSanitizer.Sanitize(container.Name));

        // Initial sync: detect creates and deletes against DB (no change detection without a snapshot)
        await CloudSyncAsync(container, ct);

        // Polling loop
        using var pollTimer = new PeriodicTimer(CloudPollInterval);
        while (await pollTimer.WaitForNextTickAsync(ct))
        {
            try
            {
                await CloudSyncAsync(container, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error polling cloud container '{ContainerName}'. Will retry next cycle.",
                    LogSanitizer.Sanitize(container.Name));
            }
        }
    }

    internal async Task CloudSyncAsync(Container container, CancellationToken ct)
    {
        var containerId = Guid.Parse(container.Id);

        IConnector connector;
        try
        {
            connector = _connectorFactory.Create(container);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create connector for cloud container '{ContainerName}' ({ContainerId}). Skipping poll.",
                LogSanitizer.Sanitize(container.Name), containerId);
            return;
        }

        try
        {
            var remoteFiles = await connector.ListFilesAsync(ct: ct);
            var remoteByPath = new Dictionary<string, ConnectorFile>(remoteFiles.Count);
            foreach (var file in remoteFiles)
            {
                var vp = file.Path.StartsWith('/') ? file.Path : "/" + file.Path;
                remoteByPath[vp] = file;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var documentStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var existingDocs = await documentStore.ListAsync(containerId, ct: ct);
            var existingByPath = existingDocs.ToDictionary(d => d.Path);

            _cloudSnapshots.TryGetValue(containerId, out var previousSnapshot);

            // Phase 1: Build the complete change set before processing anything.
            // Pre-generate document IDs so we can notify the UI before ingestion starts.
            var toCreate = new List<(string VirtualPath, string? ContentType, string DocumentId)>();
            var toUpdate = new List<(string VirtualPath, string? ContentType, string DocumentId)>();
            var toDelete = new List<string>(); // virtual paths

            foreach (var (virtualPath, file) in remoteByPath)
            {
                if (!existingByPath.TryGetValue(virtualPath, out var existing))
                {
                    toCreate.Add((virtualPath, file.ContentType, Guid.NewGuid().ToString()));
                }
                else if (previousSnapshot is not null
                    && previousSnapshot.TryGetValue(virtualPath, out var prev)
                    && (prev.LastModified != file.LastModified || prev.SizeBytes != file.SizeBytes))
                {
                    var status = existing.Metadata.GetValueOrDefault("Status");
                    if (status is not ("Pending" or "Queued" or "Processing"))
                        toUpdate.Add((virtualPath, file.ContentType, existing.Id));
                }
            }

            foreach (var doc in existingDocs)
            {
                if (!remoteByPath.ContainsKey(doc.Path))
                    toDelete.Add(doc.Path);
            }

            if (toCreate.Count > 0 || toUpdate.Count > 0 || toDelete.Count > 0)
            {
                _logger.LogInformation(
                    "Cloud sync for '{ContainerName}': found {Created} new, {Changed} changed, {Deleted} deleted — processing",
                    LogSanitizer.Sanitize(container.Name), toCreate.Count, toUpdate.Count, toDelete.Count);

                // Phase 2: Notify the file browser for ALL new/changed files at once before ingestion starts.
                // This lets the UI show every detected file immediately (with "Queued" status).
                // Deletes are notified by DeleteDocumentByVirtualPathAsync in Phase 3.
                foreach (var (virtualPath, _, documentId) in toCreate)
                    _fileChangeNotifier.NotifyAdded(container.Id, virtualPath, Path.GetFileName(virtualPath), documentId);

                foreach (var (virtualPath, _, documentId) in toUpdate)
                    _fileChangeNotifier.NotifyAdded(container.Id, virtualPath, Path.GetFileName(virtualPath), documentId);

                // Phase 2b: Pre-register new documents in the database with "Pending" status
                // so they appear in the file browser immediately, before embedding starts.
                foreach (var (virtualPath, contentType, documentId) in toCreate)
                {
                    var remoteFile = remoteByPath[virtualPath];
                    await documentStore.StoreAsync(new Document(
                        Id: documentId,
                        ContainerId: container.Id,
                        FileName: Path.GetFileName(virtualPath),
                        ContentType: contentType ?? GetContentType(Path.GetFileName(virtualPath)),
                        Path: virtualPath,
                        SizeBytes: remoteFile.SizeBytes,
                        CreatedAt: DateTime.UtcNow,
                        Metadata: new Dictionary<string, string>
                        {
                            ["Source"] = "CloudPoll",
                            ["SyncedAt"] = DateTime.UtcNow.ToString("O")
                        }), ct);
                }

                // Phase 3: Enqueue ingestion jobs and delete DB records
                foreach (var (virtualPath, contentType, documentId) in toCreate)
                    await EnqueueCloudIngestionAsync(container, virtualPath, contentType, ct, documentId);

                foreach (var (virtualPath, contentType, documentId) in toUpdate)
                    await EnqueueCloudIngestionAsync(container, virtualPath, contentType, ct, documentId);

                foreach (var virtualPath in toDelete)
                    await DeleteDocumentByVirtualPathAsync(containerId, virtualPath, ct);
            }

            // Update snapshot for next poll
            var newSnapshot = new Dictionary<string, (DateTime LastModified, long SizeBytes)>(remoteByPath.Count);
            foreach (var (vp, file) in remoteByPath)
                newSnapshot[vp] = (file.LastModified, file.SizeBytes);
            _cloudSnapshots[containerId] = newSnapshot;
        }
        finally
        {
            if (connector is IDisposable d) d.Dispose();
        }
    }

    private async Task EnqueueCloudIngestionAsync(
        Container container, string virtualPath, string? contentType, CancellationToken ct, string? existingDocumentId = null)
    {
        var fileName = Path.GetFileName(virtualPath);
        var documentId = existingDocumentId ?? Guid.NewGuid().ToString();
        contentType ??= GetContentType(fileName);

        var strategy = Enum.TryParse<ChunkingStrategy>(_chunkingSettings.CurrentValue.Strategy, ignoreCase: true, out var parsed)
            ? parsed
            : ChunkingStrategy.Recursive;

        var job = new IngestionJob(
            JobId: Guid.NewGuid().ToString(),
            DocumentId: documentId,
            Path: virtualPath,
            Options: new IngestionOptions(
                DocumentId: documentId,
                FileName: fileName,
                ContentType: contentType,
                ContainerId: container.Id,
                Path: virtualPath,
                Strategy: strategy,
                Metadata: new Dictionary<string, string>
                {
                    ["OriginalFileName"] = fileName,
                    ["Source"] = "CloudPoll",
                    ["SyncedAt"] = DateTime.UtcNow.ToString("O")
                }));

        await _queue.EnqueueAsync(job, ct);

        _logger.LogInformation(
            "Enqueued cloud ingestion for '{VirtualPath}' in container '{ContainerName}'",
            LogSanitizer.Sanitize(virtualPath), LogSanitizer.Sanitize(container.Name));

        // NotifyAdded is called in bulk by CloudSyncAsync Phase 2 (before enqueue),
        // so we don't duplicate it here.
    }

    private async Task RescanAllAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var containerStore = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var containers = await containerStore.ListAsync(ct);

        foreach (var container in containers.Where(c => c.ConnectorType == ConnectorType.Filesystem))
        {
            await InitialSyncAsync(container, ct);
        }

        // Cloud containers are polled by their own PollCloudContainerAsync loops,
        // but RescanAllAsync serves as a safety net (e.g. if a polling task died).
        foreach (var container in containers.Where(c => IsCloudConnector(c.ConnectorType)))
        {
            if (!_watcherCts.ContainsKey(Guid.Parse(container.Id)))
            {
                _logger.LogWarning(
                    "Cloud container '{ContainerName}' has no active polling task. Restarting.",
                    LogSanitizer.Sanitize(container.Name));
                StartWatchingContainer(container);
            }
        }
    }

    /// <summary>
    /// Converts an absolute file path to a virtual path relative to the container root.
    /// Uses the cached root path if available, otherwise parses connector config directly.
    /// Falls back to "/" + filename if the root path cannot be determined.
    /// </summary>
    private string ComputeVirtualPath(Guid containerId, Container container, string absPath)
    {
        // Use cached root path populated by WatchContainerAsync
        if (_rootPaths.TryGetValue(containerId, out var rootPath))
            return "/" + Path.GetRelativePath(rootPath, absPath).Replace('\\', '/');

        // Fall back to parsing connector config (e.g. during InitialSync race with WatchContainerAsync)
        if (!string.IsNullOrEmpty(container.ConnectorConfig))
        {
            try
            {
                var config = JsonSerializer.Deserialize<FilesystemConnectorConfig>(container.ConnectorConfig,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (!string.IsNullOrWhiteSpace(config?.RootPath))
                    return "/" + Path.GetRelativePath(config.RootPath, absPath).Replace('\\', '/');
            }
            catch { /* ignore — fall through to filename-only fallback */ }
        }

        return "/" + Path.GetFileName(absPath);
    }

    private static bool IsWatchableConnector(ConnectorType type) =>
        type is ConnectorType.Filesystem or ConnectorType.S3 or ConnectorType.AzureBlob or ConnectorType.MinIO;

    private static bool IsCloudConnector(ConnectorType type) =>
        type is ConnectorType.S3 or ConnectorType.AzureBlob or ConnectorType.MinIO;

    private static string? GetContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".csv" => "text/csv",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".yaml" or ".yml" => "text/yaml",
            ".html" => "text/html",
            _ => null
        };
}
