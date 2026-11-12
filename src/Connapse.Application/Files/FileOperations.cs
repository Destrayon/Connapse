using System.Globalization;
using System.Text;
using Connapse.Application.Containers;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using IDocumentAccess = Connapse.Application.Ports.IDocumentAccess;
using Microsoft.Extensions.Logging;

namespace Connapse.Application.Files;

/// <summary>Why a file action didn't succeed.</summary>
public enum FileStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "container-not-found", "Container not found")]
    ContainerNotFound,

    [Failure(404, "file-not-found", "File not found")]
    NotFound,

    [Failure(400, "invalid-page", "Invalid page")]
    InvalidPage,

    [Failure(400, "invalid-file-request", "Invalid file request")]
    Invalid,

    [Failure(400, "file-not-ready", "File not ready")]
    NotReady,

    [Failure(400, "file-ingestion-failed", "File failed ingestion")]
    IngestionFailed,

    [Failure(400, "no-parser", "No parser for this file type")]
    NoParser,

    [Failure(404, "file-not-readable", "File can't be read")]
    NotReadable,

    [Failure(400, "unsupported-file-type", "Unsupported file type")]
    UnsupportedType,

    [Failure(413, "file-too-large", "File too large")]
    TooLarge,

    [Failure(400, "container-not-writable", "Container not writable")]
    NotWritable,

    [Failure(503, "links-access-unchecked", "Access could not be checked")]
    LinksUnchecked,

    [Failure(409, "memory-container", "Agent memory container")]
    MemoryContainer,
}

/// <summary>
/// One entry at a folder level: a folder, or a document directly in it. A folder with no record of its own is one a
/// document's path implies, from uploads made before folders were tracked; it has no id.
/// </summary>
public sealed record FileEntry(string Name, string Path, bool IsFolder, long? SizeBytes, DateTime? LastModified, string? Id, Document? Document)
{
    /// <summary>When the entry last changed: a document's last upload or append, otherwise when it was added.</summary>
    public DateTime? UpdatedAt => Document is null ? LastModified : FileDates.UpdatedAt(Document);
}

/// <summary>A page of a folder's entries, in the order the query asked for.</summary>
public sealed record FileListing(IReadOnlyList<FileEntry> Entries, int TotalCount, bool HasMore);

/// <summary>
/// What to list. <see cref="Take"/> null is every entry, which only MCP's list_files asks for; REST and the file
/// browser name a page. <see cref="Sort"/> is "name" (the default: folders first, then by name) or "newest" (most
/// recently added first, entries with no date last), so the newest N are one call with Take = N.
/// </summary>
public sealed record ListFilesQuery(string? Path = null, int Skip = 0, int? Take = null, string? Sort = null);

/// <summary>The dates a document carries.</summary>
public static class FileDates
{
    /// <summary>
    /// When the document's file last changed. An upload to the same path, or an append, keeps the document's id and
    /// added date and stamps the upload time in its metadata.
    /// </summary>
    public static DateTime UpdatedAt(Document document) =>
        document.Metadata.TryGetValue("UploadedAt", out var stamp)
        && DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var uploaded)
            ? uploaded.ToUniversalTime()
            : document.CreatedAt;
}

/// <summary>A document and its text, as read from storage and parsed where it isn't text already.</summary>
public sealed record FileContent(Document Document, string Content)
{
    /// <summary>The document is still waiting to be indexed, so search can't find it yet; only a text file is read then.</summary>
    public bool AwaitingIndex => FileRules.IsAwaitingIndex(Document);
}

/// <summary>One file of a several-file read, as the caller named it: its text, or the status and detail saying why not.</summary>
public sealed record FileContentItem(string File, FileStatus Status, FileContent? Content, string? Error = null);

/// <summary>A deleted document, and whether its stored file could not be removed and needs cleaning up by hand.</summary>
public sealed record FileDeletion(Document Document, bool StorageCleanupFailed);

/// <summary>One file of a bulk delete: its id, and why it wasn't deleted when it wasn't.</summary>
public sealed record BulkDeleteItem(string FileId, bool Deleted, bool StorageCleanupFailed = false, string? Error = null);

/// <summary>A queued retry of one document's ingestion.</summary>
public sealed record FileRetry(string DocumentId, string? JobId);

internal static class FileRules
{
    /// <summary>File types read as text; anything else goes through its parser. HTML is parsed, for its text.</summary>
    public static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".log", ".css", ".js", ".ts", ".jsx", ".tsx", ".json", ".jsonl", ".ndjson", ".xml", ".yaml", ".yml",
    };

    /// <summary>Whether a document's ingestion hasn't finished, so it isn't searchable yet.</summary>
    public static bool IsAwaitingIndex(Document document) =>
        document.Metadata.GetValueOrDefault("Status") is "Pending" or "Processing" or "Queued";

    public static Attempt<T, FileStatus> Fail<T>(FileStatus status, string detail, string? field = null) =>
        Attempt<T, FileStatus>.Fail(status, detail, field);

    public static Attempt<T, FileStatus> ContainerNotFound<T>(Guid containerId) =>
        Fail<T>(FileStatus.ContainerNotFound, $"Container {containerId} not found");

    public static Attempt<T, FileStatus> MemoryOnly<T>(Container container) =>
        Fail<T>(FileStatus.MemoryContainer, ContainerRules.MemoryOnly(container));

    /// <summary>
    /// A memory container refuses files; null for a documents container, which takes every file, including the ones
    /// that also load as tables beside it (CON-319).
    /// </summary>
    public static Attempt<T, FileStatus>? RefuseText<T>(Container container) =>
        container.Kind == ContainerKind.Memory ? MemoryOnly<T>(container) : null;

    public static Attempt<T, FileStatus> FileNotFound<T>(Guid containerId, string fileRef) =>
        Fail<T>(FileStatus.NotFound, $"File {fileRef} not found in container {containerId}");

    /// <summary>
    /// The document a reference names in a container, by id or by path, when the caller may read it. One the caller
    /// may not read is answered as missing, so a probe can't learn that it exists.
    /// </summary>
    public static async Task<Document?> FindReadableAsync(
        IDocumentStore documents, IDocumentAccess access, OperationContext context, Guid containerId, string fileRef, CancellationToken ct)
    {
        var document = Guid.TryParse(fileRef, out _)
            ? await documents.GetAsync(fileRef, ct)
            : await documents.GetByPathAsync(containerId, PathUtilities.NormalizePath(fileRef), ct);
        return document is not null && document.ContainerId == containerId.ToString() && await access.CanReadAsync(context.User, document, ct)
            ? document
            : null;
    }
}

/// <summary>
/// Lists a folder's entries in a container: its folders, the folders its documents' paths imply, and the documents
/// directly in it. Open to viewers, and to agent keys that may read.
/// </summary>
[Operation("files.list", Scope = "knowledge:read", Risk = RiskTier.Read)]
public sealed class ListFiles(IContainerStore containers, IDocumentStore documents, IFolderStore folders, IDocumentAccess access, IOperationAuthorizer authorizer)
{
    public const int MaxTake = 200;

    public async Task<Attempt<FileListing, FileStatus>> RunAsync(OperationContext context, Guid containerId, ListFilesQuery query, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return FileRules.Fail<FileListing>(FileStatus.Forbidden, "You don't have permission to view files.");
        bool newest = string.Equals(query.Sort, "newest", StringComparison.OrdinalIgnoreCase);
        if (!newest && query.Sort is not null && !string.Equals(query.Sort, "name", StringComparison.OrdinalIgnoreCase))
            return FileRules.Fail<FileListing>(FileStatus.Invalid, "sort must be \"name\" or \"newest\"", "sort");
        if (query.Skip < 0)
            return FileRules.Fail<FileListing>(FileStatus.InvalidPage, "skip must be >= 0");
        if (query.Take is < 1)
            return FileRules.Fail<FileListing>(FileStatus.InvalidPage, "take must be >= 1");
        if (query.Take is > MaxTake)
            return FileRules.Fail<FileListing>(FileStatus.InvalidPage, $"take must be <= {MaxTake}");
        if (await containers.GetAsync(containerId, ct) is not { } container)
            return FileRules.ContainerNotFound<FileListing>(containerId);
        if (container.Kind == ContainerKind.Memory)
            return FileRules.MemoryOnly<FileListing>(container);

        string level = PathUtilities.NormalizeFolderPath(query.Path ?? "/");
        var entries = new List<FileEntry>();
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in await folders.ListAsync(containerId, parentPath: level, take: int.MaxValue, ct: ct))
        {
            string name = PathUtilities.GetFileName(folder.Path.TrimEnd('/'));
            if (named.Add(name))
                entries.Add(new FileEntry(name, folder.Path, IsFolder: true, null, folder.CreatedAt, folder.Id, null));
        }

        // Only documents the caller may read: one they may not read is neither listed nor lets its folder be inferred.
        var found = await documents.ListAsync(containerId, pathPrefix: level, take: int.MaxValue, ct: ct);
        var readable = await access.ReadableAsync(context.User, found, ct);
        foreach (var document in found.Where(d => readable.Contains(d.Id)))
        {
            if (string.Equals(PathUtilities.GetParentPath(document.Path), level, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(new FileEntry(document.FileName, document.Path, IsFolder: false, document.SizeBytes, document.CreatedAt, document.Id, document));
                continue;
            }

            // A document deeper down implies the folder it sits under at this level, recorded or not.
            string below = document.Path[level.Length..];
            int slash = below.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && named.Add(below[..slash]))
                entries.Add(new FileEntry(below[..slash], $"{level}{below[..slash]}/", IsFolder: true, null, null, null, null));
        }

        entries.Sort(newest ? ByNewest : ByName);

        var page = entries.Skip(query.Skip).Take(query.Take ?? int.MaxValue).ToList();
        return Attempt<FileListing, FileStatus>.Succeed(new FileListing(page, entries.Count, query.Skip + page.Count < entries.Count));
    }

    private static int ByName(FileEntry a, FileEntry b) => a.IsFolder != b.IsFolder
        ? (a.IsFolder ? -1 : 1)
        : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Most recently added first; a folder only a document's path implies has no date and goes last.</summary>
    private static int ByNewest(FileEntry a, FileEntry b)
    {
        int byDate = Nullable.Compare(b.LastModified, a.LastModified);
        return byDate != 0 ? byDate : ByName(a, b);
    }
}

/// <summary>Reads one document's details, by id or path, when the caller may read it. Open to viewers and reading agent keys.</summary>
[Operation("files.get", Scope = "knowledge:read", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class GetFile(IContainerStore containers, IDocumentStore documents, IDocumentAccess access, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<Document, FileStatus>> RunAsync(OperationContext context, Guid containerId, string fileRef, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return FileRules.Fail<Document>(FileStatus.Forbidden, "You don't have permission to view files.");
        if (await containers.GetAsync(containerId, ct) is null)
            return FileRules.ContainerNotFound<Document>(containerId);

        return await FileRules.FindReadableAsync(documents, access, context, containerId, fileRef, ct) is { } document
            ? Attempt<Document, FileStatus>.Succeed(document)
            : FileRules.FileNotFound<Document>(containerId, fileRef);
    }
}

/// <summary>
/// The files of the same container one file links to and the ones that link to it (CON-214), by id or path, when the
/// caller may read it. A linked file the caller may not read is left out and not counted; when access to the links
/// can't be checked, none are given. Open to viewers and reading agent keys.
/// </summary>
[Operation("files.links", Scope = "knowledge:read", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class GetFileLinks(
    IContainerStore containers, IDocumentStore documents, IDocumentAccess access, IDocumentLinkReader links, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<DocumentLinkSet, FileStatus>> RunAsync(OperationContext context, Guid containerId, string fileRef, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return FileRules.Fail<DocumentLinkSet>(FileStatus.Forbidden, "You don't have permission to view files.");
        if (await containers.GetAsync(containerId, ct) is null)
            return FileRules.ContainerNotFound<DocumentLinkSet>(containerId);
        if (await FileRules.FindReadableAsync(documents, access, context, containerId, fileRef, ct) is not { } document)
            return FileRules.FileNotFound<DocumentLinkSet>(containerId, fileRef);

        DocumentLinkSet found = await links.ReadAsync(SearchPrincipal.Resolve(context.User), containerId, document.Id, ct);
        return found.Denied
            ? FileRules.Fail<DocumentLinkSet>(FileStatus.LinksUnchecked, "Access to this file's links could not be checked; try again.")
            : Attempt<DocumentLinkSet, FileStatus>.Succeed(found);
    }
}

/// <summary>
/// Reads one document's full text, by id or path, from its stored file: text files as they are, other types through
/// their parser. A text file is read as soon as it is stored, while it still waits to be indexed for search; another
/// type still ingesting, or any document whose ingestion failed, has no text to give yet.
/// </summary>
[Operation("files.content", Scope = "knowledge:read", Risk = RiskTier.Read)]
public sealed class GetFileContent(
    IContainerStore containers,
    IDocumentStore documents,
    IDocumentAccess access,
    IManagedStorageProvider storage,
    IEnumerable<IDocumentParser> parsers,
    IOperationAuthorizer authorizer)
{
    public async Task<Attempt<FileContent, FileStatus>> RunAsync(OperationContext context, Guid containerId, string fileRef, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return FileRules.Fail<FileContent>(FileStatus.Forbidden, "You don't have permission to view files.");
        var container = await containers.GetAsync(containerId, ct);
        if (container is null)
            return FileRules.ContainerNotFound<FileContent>(containerId);
        return await ReadInAsync(context, containerId, container, fileRef, ct);
    }

    /// <summary>One file's text, once the caller's role and the container are known to be good.</summary>
    internal async Task<Attempt<FileContent, FileStatus>> ReadInAsync(
        OperationContext context, Guid containerId, Container container, string fileRef, CancellationToken ct)
    {
        if (await FileRules.FindReadableAsync(documents, access, context, containerId, fileRef, ct) is not { } document)
            return FileRules.FileNotFound<FileContent>(containerId, fileRef);

        string extension = Path.GetExtension(document.FileName).ToLowerInvariant();
        bool isText = FileRules.TextExtensions.Contains(extension);
        document.Metadata.TryGetValue("Status", out var status);
        // A text file's stored bytes are its text, so it needn't wait for indexing to be read (CON-277).
        if (!isText && FileRules.IsAwaitingIndex(document))
            return FileRules.Fail<FileContent>(FileStatus.NotReady, $"Document is still being ingested (status: {status})");
        if (status == "Failed")
            return FileRules.Fail<FileContent>(FileStatus.IngestionFailed,
                $"Document failed ingestion: {document.Metadata.GetValueOrDefault("ErrorMessage") ?? "unknown error"}");

        var parser = parsers.FirstOrDefault(p => p.SupportedExtensions.Contains(extension));
        if (!isText && parser is null)
            return FileRules.Fail<FileContent>(FileStatus.NoParser, $"No parser available for '{extension}' files");

        var connector = storage.CreateConnector(container.Id);
        try
        {
            using var raw = await connector.ReadFileAsync(document.Path, ct);
            // Storage streams (MinIO, S3) aren't seekable, and parsers such as PdfPig need to seek.
            using var buffered = raw.CanSeek ? null : new MemoryStream();
            if (buffered is not null)
            {
                await raw.CopyToAsync(buffered, ct);
                buffered.Position = 0;
            }
            var stream = buffered ?? raw;

            string content;
            if (FileRules.TextExtensions.Contains(extension))
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                content = await reader.ReadToEndAsync(ct);
            }
            else
                content = (await parser!.ParseAsync(stream, document.FileName, ct)).Content;
            return Attempt<FileContent, FileStatus>.Succeed(new FileContent(document, content));
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
        {
            return FileRules.Fail<FileContent>(FileStatus.NotReadable, "The backing file could not be read from storage");
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
        }
    }
}

/// <summary>
/// Reads up to 50 files' text in one call, each as files.content does, and answers each in place: its text, or why it
/// has none. A call returns at most 2,000,000 characters in all; a file that would go past that is answered with an
/// error instead. Open to viewers and reading agent keys.
/// </summary>
[Operation("files.contents", Scope = "knowledge:read", Risk = RiskTier.Read)]
public sealed class GetFileContents(IContainerStore containers, GetFileContent content, IOperationAuthorizer authorizer)
{
    public const int MaxFiles = 50;

    /// <summary>The most text one call returns, across all its files; a file that would go past it is answered with an error.</summary>
    public const int MaxTotalChars = 2_000_000;

    public async Task<Attempt<IReadOnlyList<FileContentItem>, FileStatus>> RunAsync(
        OperationContext context, Guid containerId, IReadOnlyList<string> fileRefs, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return FileRules.Fail<IReadOnlyList<FileContentItem>>(FileStatus.Forbidden, "You don't have permission to view files.");
        if (fileRefs.Count == 0)
            return FileRules.Fail<IReadOnlyList<FileContentItem>>(FileStatus.Invalid, "At least one file is required", "files");
        if (fileRefs.Count > MaxFiles)
            return FileRules.Fail<IReadOnlyList<FileContentItem>>(FileStatus.Invalid, $"A maximum of {MaxFiles} files can be read in one call", "files");
        if (fileRefs.Any(string.IsNullOrWhiteSpace))
            return FileRules.Fail<IReadOnlyList<FileContentItem>>(FileStatus.Invalid, "Every file must be an id or a path", "files");
        if (await containers.GetAsync(containerId, ct) is not { } container)
            return FileRules.ContainerNotFound<IReadOnlyList<FileContentItem>>(containerId);

        var items = new List<FileContentItem>(fileRefs.Count);
        long returned = 0;
        foreach (string fileRef in fileRefs)
        {
            Attempt<FileContent, FileStatus> read;
            try
            {
                read = await content.ReadInAsync(context, containerId, container, fileRef, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A parser that throws on one file costs that file, not the whole call.
                items.Add(new FileContentItem(fileRef, FileStatus.NotReadable, null, "The file's text could not be read"));
                continue;
            }
            if (!read.Succeeded)
            {
                items.Add(new FileContentItem(fileRef, read.Status, null, read.Detail));
                continue;
            }
            if (returned + read.Result!.Content.Length > MaxTotalChars)
            {
                items.Add(new FileContentItem(fileRef, FileStatus.TooLarge, null,
                    $"Skipped: this file's text would take the call past the maximum of {MaxTotalChars} characters; read it in a call of its own"));
                continue;
            }
            returned += read.Result.Content.Length;
            items.Add(new FileContentItem(fileRef, read.Status, read.Result));
        }
        return Attempt<IReadOnlyList<FileContentItem>, FileStatus>.Succeed(items);
    }
}

/// <summary>
/// Deletes one document: its ingestion is cancelled, its row (with its chunks and vectors) removed, folders it leaves
/// empty removed, and its stored file deleted. A stored file that won't go is reported, not failed on: the document
/// is gone either way. Editors and above, and agent keys that may write.
/// </summary>
[Operation("files.delete", Scope = "knowledge:write", Risk = RiskTier.Destructive)]
public sealed class DeleteFile(
    IContainerStore containers,
    IDocumentStore documents,
    IFolderStore folders,
    IManagedStorageProvider storage,
    IIngestionQueue queue,
    IStoredFileRemovalStore removals,
    IAuditLogger audit,
    IOperationAuthorizer authorizer,
    ILogger<DeleteFile> logger)
{
    public async Task<Attempt<FileDeletion, FileStatus>> RunAsync(OperationContext context, Guid containerId, string fileId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return FileRules.Fail<FileDeletion>(FileStatus.Forbidden, "You don't have permission to delete files.");
        if (await containers.GetAsync(containerId, ct) is null)
            return FileRules.ContainerNotFound<FileDeletion>(containerId);
        return await DeleteInAsync(containerId, fileId, ct);
    }

    /// <summary>The delete itself, for a container already found and a caller already allowed.</summary>
    internal async Task<Attempt<FileDeletion, FileStatus>> DeleteInAsync(Guid containerId, string fileId, CancellationToken ct)
    {
        var document = await documents.GetAsync(fileId, ct);
        if (document is null || document.ContainerId != containerId.ToString())
            return FileRules.FileNotFound<FileDeletion>(containerId, fileId);

        await queue.CancelJobForDocumentAsync(fileId);
        // The stored file is owed a removal from the moment the record goes, so a removal below that fails, or that a
        // crash cuts off, is retried, and a sync meanwhile doesn't index the file again (#378).
        Guid? owedRemoval = await documents.DeleteOwingFileRemovalAsync(fileId, ct);
        if (!string.IsNullOrEmpty(document.Path))
            await folders.DeleteEmptyAncestorsAsync(containerId, document.Path, ct);

        bool cleanupFailed = false;
        try
        {
            // Through the container's own storage, which keeps each container's files under its own prefix.
            if (!string.IsNullOrEmpty(document.Path) && storage.CreateConnector(containerId.ToString()) is IWritableConnector connector)
            {
                try
                {
                    // Under the path's lock, and only while no record holds the path, so an upload to the same path
                    // that lands meanwhile keeps its file. Not the request's token: the record is already gone, so a
                    // cancelled delete must still remove the file.
                    await documents.RemoveUnrecordedFileAsync(containerId, document.Path,
                        token => connector.DeleteFileAsync(document.Path, token), CancellationToken.None);
                }
                finally
                {
                    (connector as IDisposable)?.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to delete backing file {Path}", document.Path);
            cleanupFailed = true;
        }

        if (!cleanupFailed && owedRemoval is Guid removalId)
        {
            try
            {
                await removals.CompleteAsync(removalId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Still owed, so the removal job finds the file already gone and completes it then.
                logger.LogWarning(ex, "Failed to complete the owed removal of {Path}", document.Path);
            }
        }

        await audit.LogAsync("doc.deleted", "document", fileId, new { document.FileName, ContainerId = containerId }, ct);
        return Attempt<FileDeletion, FileStatus>.Succeed(new FileDeletion(document, cleanupFailed));
    }
}

/// <summary>Deletes up to 100 documents in one call, each as files.delete does, and says what happened to each.</summary>
[Operation("files.bulkDelete", Scope = "knowledge:write", Risk = RiskTier.Destructive)]
public sealed class BulkDeleteFiles(IContainerStore containers, DeleteFile delete, IOperationAuthorizer authorizer)
{
    public const int MaxFiles = 100;

    public async Task<Attempt<IReadOnlyList<BulkDeleteItem>, FileStatus>> RunAsync(
        OperationContext context, Guid containerId, IReadOnlyList<string> fileIds, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return FileRules.Fail<IReadOnlyList<BulkDeleteItem>>(FileStatus.Forbidden, "You don't have permission to delete files.");
        if (fileIds.Count == 0)
            return FileRules.Fail<IReadOnlyList<BulkDeleteItem>>(FileStatus.Invalid, "At least one file id is required", "fileIds");
        if (fileIds.Count > MaxFiles)
            return FileRules.Fail<IReadOnlyList<BulkDeleteItem>>(FileStatus.Invalid, $"A bulk delete must name at most {MaxFiles} files", "fileIds");
        if (await containers.GetAsync(containerId, ct) is null)
            return FileRules.ContainerNotFound<IReadOnlyList<BulkDeleteItem>>(containerId);

        var items = new List<BulkDeleteItem>();
        foreach (string fileId in fileIds)
        {
            var deleted = await delete.DeleteInAsync(containerId, fileId, ct);
            items.Add(deleted.Succeeded
                ? new BulkDeleteItem(fileId, Deleted: true, deleted.Result!.StorageCleanupFailed)
                // Within the one container the call names, so the container needn't be repeated per file.
                : new BulkDeleteItem(fileId, Deleted: false, Error: $"File '{fileId}' not found in this container."));
        }
        return Attempt<IReadOnlyList<BulkDeleteItem>, FileStatus>.Succeed(items);
    }
}

/// <summary>Whether one document needs reindexing, and why. Open to viewers and reading agent keys.</summary>
[Operation("files.reindexCheck", Scope = "knowledge:read", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class CheckFileReindex(IContainerStore containers, IDocumentStore documents, IDocumentAccess access, IReindexService reindex, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<ReindexCheck, FileStatus>> RunAsync(OperationContext context, Guid containerId, string fileId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return FileRules.Fail<ReindexCheck>(FileStatus.Forbidden, "You don't have permission to view files.");
        if (await containers.GetAsync(containerId, ct) is null)
            return FileRules.ContainerNotFound<ReindexCheck>(containerId);
        // Only a document of this container the caller may read: the route names the container, so another's can't be
        // checked through it, and one they may not read is answered as missing, as files.get answers it.
        var document = await documents.GetAsync(fileId, ct);
        if (document is null || document.ContainerId != containerId.ToString() || !await access.CanReadAsync(context.User, document, ct))
            return FileRules.FileNotFound<ReindexCheck>(containerId, fileId);

        return Attempt<ReindexCheck, FileStatus>.Succeed(await reindex.CheckDocumentAsync(fileId, ct));
    }
}

/// <summary>Queues one document's ingestion again, with a fresh attempt budget. Editors and above, and agent keys that may write.</summary>
[Operation("files.retry", Scope = "knowledge:write", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class RetryFile(IContainerStore containers, IDocumentStore documents, IIngestionQueue queue, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<FileRetry, FileStatus>> RunAsync(OperationContext context, Guid containerId, string fileId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return FileRules.Fail<FileRetry>(FileStatus.Forbidden, "You don't have permission to retry files.");
        if (await containers.GetAsync(containerId, ct) is null)
            return FileRules.ContainerNotFound<FileRetry>(containerId);
        var document = await documents.GetAsync(fileId, ct);
        if (document is null || document.ContainerId != containerId.ToString())
            return FileRules.FileNotFound<FileRetry>(containerId, fileId);

        string? jobId = await queue.EnqueueAsync(new IngestionJob(
            DocumentId: document.Id,
            Options: new IngestionOptions(
                DocumentId: document.Id,
                FileName: document.FileName,
                ContentType: document.ContentType,
                ContainerId: document.ContainerId,
                Path: document.Path)), ct);
        await audit.LogAsync("doc.retried", "document", document.Id, new { document.FileName, ContainerId = containerId, jobId }, ct);
        return Attempt<FileRetry, FileStatus>.Succeed(new FileRetry(document.Id, jobId));
    }
}

/// <summary>Every file operation in one place, for the file browser.</summary>
public sealed class FileOperations(
    ListFiles list, GetFile get, GetFileLinks links, GetFileContent content, DeleteFile delete, BulkDeleteFiles bulkDelete, CheckFileReindex reindexCheck, RetryFile retry,
    UploadFile upload)
{
    public ListFiles List { get; } = list;

    public GetFile Get { get; } = get;

    public GetFileLinks Links { get; } = links;

    public GetFileContent Content { get; } = content;

    public DeleteFile Delete { get; } = delete;

    public BulkDeleteFiles BulkDelete { get; } = bulkDelete;

    public CheckFileReindex ReindexCheck { get; } = reindexCheck;

    public RetryFile Retry { get; } = retry;

    public UploadFile Upload { get; } = upload;
}
