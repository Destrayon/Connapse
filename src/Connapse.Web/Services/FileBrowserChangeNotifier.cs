namespace Connapse.Web.Services;

/// <summary>
/// Carries information about a file-system event that the FileBrowser should reflect in real time.
/// VirtualPath is relative to the container root (e.g. "/subdir/file.pdf").
/// </summary>
public record FileBrowserFileEvent(
    string ContainerId,
    string VirtualPath,
    string FileName,
    string? DocumentId,
    bool IsDeleted);

/// <summary>
/// In-process singleton event bus fired by ConnectorWatcherService when a file is
/// added, changed, or deleted in a Filesystem container. FileBrowser components
/// subscribe to receive real-time file-list updates without polling.
/// </summary>
public class FileBrowserChangeNotifier
{
    public event Action<FileBrowserFileEvent>? FileChanged;

    internal void NotifyAdded(string containerId, string virtualPath, string fileName, string documentId) =>
        FileChanged?.Invoke(new FileBrowserFileEvent(containerId, virtualPath, fileName, documentId, false));

    internal void NotifyDeleted(string containerId, string virtualPath, string fileName) =>
        FileChanged?.Invoke(new FileBrowserFileEvent(containerId, virtualPath, fileName, null, true));
}
