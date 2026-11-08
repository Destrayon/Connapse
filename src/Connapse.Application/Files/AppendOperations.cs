using System.Text;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Microsoft.Extensions.Options;
using IDocumentAccess = Connapse.Application.Ports.IDocumentAccess;

namespace Connapse.Application.Files;

/// <summary>An append: the document it grew, the ingestion job queued to reindex it, its path, and its size now.</summary>
public sealed record FileAppend(string DocumentId, string? JobId, string Path, long SizeBytes);

/// <summary>
/// Adds text to the end of an existing text file, exactly as given (send your own leading newline), and reindexes it.
/// The file keeps its id, path, added date and chunking strategy; its updated date moves. Appends to one file wait for
/// each other, so two at once both land. Editors and above, and agent keys that may write.
/// </summary>
[Operation("files.append", Scope = "knowledge:write", Risk = RiskTier.Write)]
public sealed class AppendToFile(
    IContainerStore containers,
    IDocumentStore documents,
    IDocumentAccess access,
    IManagedStorageProvider storage,
    IUploadService uploads,
    IOperationAuthorizer authorizer,
    IOptionsMonitor<UploadSettings> settings)
{
    // One lock per stripe of container and path, shared by every instance in this process.
    private static readonly SemaphoreSlim[] Locks = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

    private const string ChunkingStrategyKey = "IndexedWith:ChunkingStrategy";
    private const string StrategyExplicitKey = "IndexedWith:StrategyExplicit";

    public async Task<Attempt<FileAppend, FileStatus>> RunAsync(
        OperationContext context, Guid containerId, string fileRef, string? text, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return FileRules.Fail<FileAppend>(FileStatus.Forbidden, "You don't have permission to append to files.");
        if (string.IsNullOrEmpty(text))
            return FileRules.Fail<FileAppend>(FileStatus.Invalid, "text is required", "text");
        if (await containers.GetAsync(containerId, ct) is not { } container)
            return FileRules.ContainerNotFound<FileAppend>(containerId);
        if (container.Kind == ContainerKind.Memory)
            return FileRules.MemoryOnly<FileAppend>(container);
        if (!UploadRules.Writable(storage, container.Id))
            return FileRules.Fail<FileAppend>(FileStatus.NotWritable, "This container is not writable.");
        if (await FileRules.FindReadableAsync(documents, access, context, containerId, fileRef, ct) is not { } document)
            return FileRules.FileNotFound<FileAppend>(containerId, fileRef);

        string extension = Path.GetExtension(document.FileName).ToLowerInvariant();
        if (!FileRules.TextExtensions.Contains(extension))
            return FileRules.Fail<FileAppend>(FileStatus.UnsupportedType,
                $"Only text files can be appended to, not '{extension}' files. Text types: {string.Join(", ", FileRules.TextExtensions.Order(StringComparer.Ordinal))}", "file");

        byte[] added = Encoding.UTF8.GetBytes(text);
        long maxFileBytes = settings.CurrentValue.MaxFileBytes;
        var gate = Locks[(int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode($"{containerId}|{document.Path}") % (uint)Locks.Length)];
        await gate.WaitAsync(ct);
        try
        {
            using var combined = new MemoryStream();
            var connector = storage.CreateConnector(container.Id);
            try
            {
                using var stored = await connector.ReadFileAsync(document.Path, ct);
                await stored.CopyToAsync(combined, ct);
            }
            catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
            {
                return FileRules.Fail<FileAppend>(FileStatus.NotReadable, "The backing file could not be read from storage");
            }
            finally
            {
                (connector as IDisposable)?.Dispose();
            }

            if (combined.Length + added.Length > maxFileBytes)
                return FileRules.Fail<FileAppend>(FileStatus.TooLarge, UploadLimits.FileTooLarge(combined.Length + added.Length, maxFileBytes), "text");
            combined.Write(added);
            combined.Position = 0;

            // An explicit chunking strategy stays; otherwise the upload picks one again, as it did the first time.
            string? strategy = document.Metadata.GetValueOrDefault(StrategyExplicitKey) == "true"
                ? document.Metadata.GetValueOrDefault(ChunkingStrategyKey)
                : null;
            var upload = new FileUpload(document.FileName, combined, PathUtilities.GetParentPath(document.Path), document.ContentType, strategy);
            var uploaded = await uploads.UploadAsync(UploadRules.Request(context, containerId, upload), ct);
            return uploaded.Success
                ? Attempt<FileAppend, FileStatus>.Succeed(new FileAppend(uploaded.DocumentId!, uploaded.JobId, document.Path, combined.Length))
                : FileRules.Fail<FileAppend>(FileStatus.Invalid, uploaded.Error ?? "The file could not be appended to.");
        }
        finally
        {
            gate.Release();
        }
    }
}
