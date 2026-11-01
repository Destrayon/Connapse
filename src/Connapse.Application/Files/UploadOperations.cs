using System.Security.Claims;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;

namespace Connapse.Application.Files;

/// <summary>One file to upload: its name, its bytes, and where and how to ingest it.</summary>
public sealed record FileUpload(string FileName, Stream Content, string? Path = null, string? ContentType = null, string? Strategy = null);

/// <summary>An uploaded file: the document it became, the ingestion job queued for it, and its path.</summary>
public sealed record UploadedFile(string DocumentId, string? JobId, string FileName, string Path);

/// <summary>One file of a bulk upload: where it goes, and the document it became or why it wasn't uploaded.</summary>
public sealed record UploadItem(string FileName, string Path, string? DocumentId = null, string? JobId = null, string? Error = null)
{
    public bool Uploaded => Error is null;
}

/// <summary>A bulk upload: the batch its jobs share, and each file's outcome in the order given.</summary>
public sealed record BulkUpload(string? BatchId, IReadOnlyList<UploadItem> Items)
{
    public int UploadedCount => Items.Count(i => i.Uploaded);
}

internal static class UploadRules
{
    /// <summary>Where a file lands: its folder, normalized, and its name.</summary>
    public static string PathOf(FileUpload upload) =>
        PathUtilities.NormalizePath(PathUtilities.NormalizeFolderPath(upload.Path ?? "/") + upload.FileName);

    /// <summary>Why one file can't be uploaded, checked before any storage is touched; null when it can be.</summary>
    public static (FileStatus Status, string Detail, string Field)? Problem(FileUpload upload, IFileTypeValidator types)
    {
        if (upload.FileName.Length > ValidationConstants.MaxFileNameLength)
            return (FileStatus.Invalid, $"Filename exceeds {ValidationConstants.MaxFileNameLength} characters.", "fileName");
        if (!PathUtilities.IsValidFileName(upload.FileName))
            return (FileStatus.Invalid, $"Invalid filename: '{upload.FileName}'.", "fileName");
        if (upload.Path is not null && PathUtilities.ContainsPathTraversal(upload.Path))
            return (FileStatus.Invalid, "Path traversal is not allowed.", "path");
        if (upload.Path is not null && upload.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length > ValidationConstants.MaxPathDepth)
            return (FileStatus.Invalid, $"Path exceeds maximum depth of {ValidationConstants.MaxPathDepth} levels.", "path");
        if (!types.IsSupported(upload.FileName))
            return (FileStatus.UnsupportedType, $"Unsupported file extension. Supported types: {string.Join(", ", types.SupportedExtensions.Order(StringComparer.Ordinal))}", "fileName");
        if (upload.Content.CanSeek && upload.Content.Length == 0)
            return (FileStatus.Invalid, "File is empty. Zero-byte uploads are not allowed.", "content");
        return null;
    }

    /// <summary>Whether the container takes uploads: only managed storage is writable.</summary>
    public static bool Writable(IManagedStorageProvider storage, string containerId)
    {
        var connector = storage.CreateConnector(containerId);
        try
        {
            return connector is IWritableConnector;
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
        }
    }

    public static UploadRequest Request(OperationContext context, Guid containerId, FileUpload upload) =>
        new(containerId, upload.FileName, upload.Content,
            Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null,
            upload.Path, upload.ContentType, upload.Strategy, context.Surface == OperationSurface.Mcp ? "MCP" : "API");
}

/// <summary>Uploads one file to a container and queues its ingestion. Editors and above, and agent keys that may write.</summary>
[Operation("files.upload", Scope = "knowledge:write", Risk = RiskTier.Write)]
public sealed class UploadFile(
    IContainerStore containers, IManagedStorageProvider storage, IFileTypeValidator types, IUploadService uploads, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<UploadedFile, FileStatus>> RunAsync(OperationContext context, Guid containerId, FileUpload upload, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return FileRules.Fail<UploadedFile>(FileStatus.Forbidden, "You don't have permission to upload files.");
        if (UploadRules.Problem(upload, types) is var (status, detail, field))
            return FileRules.Fail<UploadedFile>(status, detail, field);
        if (await containers.GetAsync(containerId, ct) is not { } container)
            return FileRules.ContainerNotFound<UploadedFile>(containerId);
        if (!UploadRules.Writable(storage, container.Id))
            return FileRules.Fail<UploadedFile>(FileStatus.NotWritable, "This container is not writable.");

        var uploaded = await uploads.UploadAsync(UploadRules.Request(context, containerId, upload), ct);
        return uploaded.Success
            ? Attempt<UploadedFile, FileStatus>.Succeed(new UploadedFile(uploaded.DocumentId!, uploaded.JobId, upload.FileName, UploadRules.PathOf(upload)))
            : FileRules.Fail<UploadedFile>(FileStatus.Invalid, uploaded.Error ?? "The file could not be uploaded.");
    }
}

/// <summary>
/// Uploads up to 100 files to a container in one batch. A file that can't be uploaded is reported in its place and the
/// rest still go. Editors and above, and agent keys that may write.
/// </summary>
[Operation("files.bulkUpload", Scope = "knowledge:write", Risk = RiskTier.Write)]
public sealed class BulkUploadFiles(
    IContainerStore containers, IManagedStorageProvider storage, IFileTypeValidator types, IUploadService uploads, IOperationAuthorizer authorizer)
{
    public const int MaxFiles = 100;

    public async Task<Attempt<BulkUpload, FileStatus>> RunAsync(
        OperationContext context, Guid containerId, IReadOnlyList<FileUpload> files, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return FileRules.Fail<BulkUpload>(FileStatus.Forbidden, "You don't have permission to upload files.");
        if (files.Count == 0)
            return FileRules.Fail<BulkUpload>(FileStatus.Invalid, "At least one file is required", "files");
        if (files.Count > MaxFiles)
            return FileRules.Fail<BulkUpload>(FileStatus.Invalid, $"A bulk upload must name at most {MaxFiles} files", "files");
        if (await containers.GetAsync(containerId, ct) is not { } container)
            return FileRules.ContainerNotFound<BulkUpload>(containerId);
        if (!UploadRules.Writable(storage, container.Id))
            return FileRules.Fail<BulkUpload>(FileStatus.NotWritable, "This container is not writable.");

        // Each file is checked first; only the ones that pass go to storage, as one batch.
        var problems = files.Select(f => UploadRules.Problem(f, types)?.Detail).ToList();
        var passing = files.Where((_, i) => problems[i] is null).ToList();
        var batch = passing.Count == 0
            ? new BulkUploadResult(0, 0, null, [])
            : await uploads.BulkUploadAsync(new BulkUploadRequest(containerId, [.. passing.Select(f => UploadRules.Request(context, containerId, f))]), ct);

        var items = new List<UploadItem>();
        int next = 0;
        for (int i = 0; i < files.Count; i++)
        {
            string path = UploadRules.PathOf(files[i]);
            if (problems[i] is { } problem)
            {
                items.Add(new UploadItem(files[i].FileName, path, Error: problem));
                continue;
            }
            var result = batch.Results[next++];
            items.Add(result.Success
                ? new UploadItem(files[i].FileName, path, result.DocumentId, result.JobId)
                : new UploadItem(files[i].FileName, path, Error: result.Error ?? "Unknown error"));
        }
        return Attempt<BulkUpload, FileStatus>.Succeed(new BulkUpload(batch.BatchId, items));
    }
}
