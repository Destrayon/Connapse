using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;

namespace Connapse.Application.Files;

/// <summary>Why a folder action didn't succeed.</summary>
public enum FolderStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "container-not-found", "Container not found")]
    ContainerNotFound,

    [Failure(404, "folder-not-found", "Folder not found")]
    NotFound,

    [Failure(400, "invalid-folder", "Invalid folder")]
    Invalid,

    [Failure(409, "folder-exists", "Folder already exists")]
    Exists,
}

/// <summary>A deleted folder: how many documents went with it, and whether any stored file could not be removed.</summary>
public sealed record FolderDeletion(string Path, int DocumentsDeleted, bool StorageCleanupFailed);

internal static class FolderRules
{
    public static Attempt<T, FolderStatus> Fail<T>(FolderStatus status, string detail, string? field = null) =>
        Attempt<T, FolderStatus>.Fail(status, detail, field);

    /// <summary>
    /// The folder path a request names, normalized, or why it can't name one: it is required, has no <c>..</c>
    /// segment, is at most <see cref="ValidationConstants.MaxPathDepth"/> levels deep, and isn't the root.
    /// </summary>
    public static (string? Path, string? Error) Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (null, "Folder path is required");
        if (PathUtilities.ContainsPathTraversal(path))
            return (null, "Path must not contain '..' segments");
        if (path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length > ValidationConstants.MaxPathDepth)
            return (null, $"Path exceeds maximum depth of {ValidationConstants.MaxPathDepth} levels");
        string normalized = PathUtilities.NormalizeFolderPath(path);
        return normalized == "/" ? (null, "The root folder can't be created or deleted") : (normalized, null);
    }
}

/// <summary>Creates an empty folder in a container. Editors and above, and agent keys that may write.</summary>
[Operation("folders.create", Scope = "knowledge:write", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class CreateFolder(IContainerStore containers, IFolderStore folders, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<Folder, FolderStatus>> RunAsync(OperationContext context, Guid containerId, string? path, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return FolderRules.Fail<Folder>(FolderStatus.Forbidden, "You don't have permission to create folders.");
        var (normalized, error) = FolderRules.Normalize(path);
        if (normalized is null)
            return FolderRules.Fail<Folder>(FolderStatus.Invalid, error!, "path");
        if (await containers.GetAsync(containerId, ct) is null)
            return FolderRules.Fail<Folder>(FolderStatus.ContainerNotFound, $"Container {containerId} not found");
        if (await folders.ExistsAsync(containerId, normalized, ct))
            return FolderRules.Fail<Folder>(FolderStatus.Exists, $"Folder '{normalized}' already exists");

        var folder = await folders.CreateAsync(containerId, normalized, ct);
        await audit.LogAsync("folder.created", "folder", folder.Id, new { folder.Path, ContainerId = containerId }, ct);
        return Attempt<Folder, FolderStatus>.Succeed(folder);
    }
}

/// <summary>
/// Deletes a folder with everything under it: its sub-folders, and every document in them with its ingestion
/// cancelled and its stored file removed. A recorded folder goes through the folder store, which locks it against
/// uploads and removes the stored files before it commits, so an upload landing meanwhile is removed whole too. A
/// folder with no record of its own, one only its documents' paths imply, is deleted by deleting those documents as
/// files.delete does. Editors and above, and agent keys that may write.
/// </summary>
[Operation("folders.delete", Scope = "knowledge:write", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class DeleteFolder(
    IContainerStore containers,
    IDocumentStore documents,
    IFolderStore folders,
    IIngestionQueue queue,
    DeleteFile deleteFile,
    IAuditLogger audit,
    IOperationAuthorizer authorizer)
{
    public async Task<Attempt<FolderDeletion, FolderStatus>> RunAsync(OperationContext context, Guid containerId, string? path, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return FolderRules.Fail<FolderDeletion>(FolderStatus.Forbidden, "You don't have permission to delete folders.");
        var (normalized, error) = FolderRules.Normalize(path);
        if (normalized is null)
            return FolderRules.Fail<FolderDeletion>(FolderStatus.Invalid, error!, "path");
        if (await containers.GetAsync(containerId, ct) is null)
            return FolderRules.Fail<FolderDeletion>(FolderStatus.ContainerNotFound, $"Container {containerId} not found");

        var inside = await documents.ListAsync(containerId, pathPrefix: normalized, take: int.MaxValue, ct: ct);
        bool recorded = await folders.ExistsAsync(containerId, normalized, ct);
        if (!recorded && inside.Count == 0)
            return NotFound(normalized);

        bool cleanupFailed = false;
        if (recorded)
        {
            foreach (var document in inside)
                await queue.CancelJobForDocumentAsync(document.Id);

            // The folder store deletes the records under it and their stored files in one go; a stored file that
            // won't go throws and rolls the delete back, so nothing is left half-deleted to report.
            if (!await folders.DeleteAsync(containerId, normalized, ct))
                return NotFound(normalized);
        }
        else
        {
            foreach (var document in inside)
            {
                var deleted = await deleteFile.DeleteInAsync(containerId, document.Id, ct);
                cleanupFailed |= deleted.Result?.StorageCleanupFailed == true;
            }
        }

        await audit.LogAsync("folder.deleted", "folder", normalized, new { Path = normalized, ContainerId = containerId, Documents = inside.Count }, ct);
        return Attempt<FolderDeletion, FolderStatus>.Succeed(new FolderDeletion(normalized, inside.Count, cleanupFailed));
    }

    private static Attempt<FolderDeletion, FolderStatus> NotFound(string path) =>
        FolderRules.Fail<FolderDeletion>(FolderStatus.NotFound, $"Folder '{path}' not found");
}

/// <summary>Every folder operation in one place, for the file browser.</summary>
public sealed class FolderOperations(CreateFolder create, DeleteFolder delete)
{
    public CreateFolder Create { get; } = create;

    public DeleteFolder Delete { get; } = delete;
}
