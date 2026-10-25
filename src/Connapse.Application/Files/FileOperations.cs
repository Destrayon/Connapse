using System.Text;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
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

    [Failure(400, "container-not-writable", "Container not writable")]
    NotWritable,
}

/// <summary>
/// One entry at a folder level: a folder, or a document directly in it. A folder with no record of its own is one a
/// document's path implies, from uploads made before folders were tracked; it has no id.
/// </summary>
public sealed record FileEntry(string Name, string Path, bool IsFolder, long? SizeBytes, DateTime? LastModified, string? Id, Document? Document);

/// <summary>A page of a folder's entries, folders first, then by name.</summary>
public sealed record FileListing(IReadOnlyList<FileEntry> Entries, int TotalCount, bool HasMore);

/// <summary>
/// What to list. <see cref="Take"/> null is every entry, which only MCP's list_files asks for; REST and the file
/// browser name a page.
/// </summary>
public sealed record ListFilesQuery(string? Path = null, int Skip = 0, int? Take = null);

/// <summary>A document and its text, as read from storage and parsed where it isn't text already.</summary>
public sealed record FileContent(Document Document, string Content);

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
        ".txt", ".md", ".markdown", ".csv", ".log", ".css", ".js", ".ts", ".jsx", ".tsx", ".json", ".xml", ".yaml", ".yml",
    };

    public static Attempt<T, FileStatus> Fail<T>(FileStatus status, string detail, string? field = null) =>
        Attempt<T, FileStatus>.Fail(status, detail, field);

    public static Attempt<T, FileStatus> ContainerNotFound<T>(Guid containerId) =>
        Fail<T>(FileStatus.ContainerNotFound, $"Container {containerId} not found");

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
        if (query.Skip < 0)
            return FileRules.Fail<FileListing>(FileStatus.InvalidPage, "skip must be >= 0");
        if (query.Take is < 1)
            return FileRules.Fail<FileListing>(FileStatus.InvalidPage, "take must be >= 1");
        if (query.Take is > MaxTake)
            return FileRules.Fail<FileListing>(FileStatus.InvalidPage, $"take must be <= {MaxTake}");
        if (await containers.GetAsync(containerId, ct) is null)
            return FileRules.ContainerNotFound<FileListing>(containerId);

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

        entries.Sort((a, b) => a.IsFolder != b.IsFolder
            ? (a.IsFolder ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var page = entries.Skip(query.Skip).Take(query.Take ?? int.MaxValue).ToList();
        return Attempt<FileListing, FileStatus>.Succeed(new FileListing(page, entries.Count, query.Skip + page.Count < entries.Count));
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
/// Reads one document's full text, by id or path, from its stored file: text files as they are, other types through
/// their parser. A document still ingesting, or whose ingestion failed, has no text to give yet.
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
        if (await FileRules.FindReadableAsync(documents, access, context, containerId, fileRef, ct) is not { } document)
            return FileRules.FileNotFound<FileContent>(containerId, fileRef);

        document.Metadata.TryGetValue("Status", out var status);
        if (status is "Pending" or "Processing" or "Queued")
            return FileRules.Fail<FileContent>(FileStatus.NotReady, $"Document is still being ingested (status: {status})");
        if (status == "Failed")
            return FileRules.Fail<FileContent>(FileStatus.IngestionFailed,
                $"Document failed ingestion: {document.Metadata.GetValueOrDefault("ErrorMessage") ?? "unknown error"}");

        string extension = Path.GetExtension(document.FileName).ToLowerInvariant();
        var parser = parsers.FirstOrDefault(p => p.SupportedExtensions.Contains(extension));
        if (!FileRules.TextExtensions.Contains(extension) && parser is null)
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
/// Deletes one document: its ingestion is cancelled, its row (with its chunks and vectors) removed, folders it leaves
/// empty removed, and its stored file deleted. A stored file that won't go is reported, not failed on: the document
/// is gone either way. Editors and above, and agent keys that may write.
/// </summary>
[Operation("files.delete", Scope = "knowledge:write", Risk = RiskTier.Destructive)]
public sealed class DeleteFile(
    IContainerStore containers,
    IDocumentStore documents,
    IFolderStore folders,
    IKnowledgeFileSystem files,
    IIngestionQueue queue,
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
        await documents.DeleteAsync(fileId, ct);
        if (!string.IsNullOrEmpty(document.Path))
            await folders.DeleteEmptyAncestorsAsync(containerId, document.Path, ct);

        bool cleanupFailed = false;
        try
        {
            if (!string.IsNullOrEmpty(document.Path))
                await files.DeleteAsync(document.Path, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to delete backing file {Path}", document.Path);
            cleanupFailed = true;
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
    ListFiles list, GetFile get, GetFileContent content, DeleteFile delete, BulkDeleteFiles bulkDelete, CheckFileReindex reindexCheck, RetryFile retry)
{
    public ListFiles List { get; } = list;

    public GetFile Get { get; } = get;

    public GetFileContent Content { get; } = content;

    public DeleteFile Delete { get; } = delete;

    public BulkDeleteFiles BulkDelete { get; } = bulkDelete;

    public CheckFileReindex ReindexCheck { get; } = reindexCheck;

    public RetryFile Retry { get; } = retry;
}
