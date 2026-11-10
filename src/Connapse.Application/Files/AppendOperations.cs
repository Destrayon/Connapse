using System.Text;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Ingestion.Pipeline;
using Microsoft.Extensions.Options;
using IDocumentAccess = Connapse.Application.Ports.IDocumentAccess;

namespace Connapse.Application.Files;

/// <summary>An append: the document it grew, the ingestion job queued to reindex it, its path, and its size now.</summary>
public sealed record FileAppend(string DocumentId, string? JobId, string Path, long SizeBytes);

/// <summary>
/// Adds text to the end of an existing text file, exactly as given (send your own leading newline), and reindexes it.
/// The file keeps its id, path, added date and chunking strategy; its updated date moves. The text is written in the
/// file's own encoding (UTF-8 unless its byte order mark says UTF-16 or UTF-32). Appends to one file through the same
/// server wait for each other, so two at once both land; appends through different servers sharing one database are not
/// serialized, and the later one can drop the earlier one's text. Editors and above, and agent keys that may write.
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

    public async Task<Attempt<FileAppend, FileStatus>> RunAsync(
        OperationContext context, Guid containerId, string fileRef, string? text, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return FileRules.Fail<FileAppend>(FileStatus.Forbidden, "You don't have permission to append to files.");
        if (string.IsNullOrEmpty(text))
            return FileRules.Fail<FileAppend>(FileStatus.Invalid, "text is required", "text");
        if (string.IsNullOrWhiteSpace(fileRef))
            return FileRules.Fail<FileAppend>(FileStatus.Invalid, "file is required", "file");
        if (await containers.GetAsync(containerId, ct) is not { } container)
            return FileRules.ContainerNotFound<FileAppend>(containerId);
        if (FileRules.RefuseText<FileAppend>(container) is { } refused)
            return refused;
        if (!UploadRules.Writable(storage, container.Id))
            return FileRules.Fail<FileAppend>(FileStatus.NotWritable, "This container is not writable.");
        if (await FileRules.FindReadableAsync(documents, access, context, containerId, fileRef, ct) is not { } document)
            return FileRules.FileNotFound<FileAppend>(containerId, fileRef);

        string extension = Path.GetExtension(document.FileName).ToLowerInvariant();
        if (!FileRules.TextExtensions.Contains(extension))
            return FileRules.Fail<FileAppend>(FileStatus.UnsupportedType,
                $"Only text files can be appended to, not '{extension}' files. Text types: {string.Join(", ", FileRules.TextExtensions.Order(StringComparer.Ordinal))}", "file");

        long maxFileBytes = settings.CurrentValue.MaxFileBytes;
        // The fewest bytes the text can take in any encoding the file might be in, so a file that is already too big is
        // turned away before any of it is read, and the read never buffers more than the limit leaves room for.
        long fewestAdded = Math.Min(Encoding.UTF8.GetByteCount(text), text.Length * 2L);
        if (document.SizeBytes + fewestAdded > maxFileBytes)
            return FileRules.Fail<FileAppend>(FileStatus.TooLarge, UploadLimits.FileTooLarge(document.SizeBytes + fewestAdded, maxFileBytes), "text");

        var gate = Locks[(int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode($"{containerId}|{document.Path}") % (uint)Locks.Length)];
        await gate.WaitAsync(ct);
        try
        {
            using var combined = new MemoryStream();
            long room = maxFileBytes - fewestAdded;
            long read;
            var connector = storage.CreateConnector(container.Id);
            try
            {
                using var stored = await connector.ReadFileAsync(document.Path, ct);
                read = await CopyAtMostAsync(stored, combined, room, ct);
            }
            catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
            {
                return FileRules.Fail<FileAppend>(FileStatus.NotReadable, "The backing file could not be read from storage");
            }
            finally
            {
                (connector as IDisposable)?.Dispose();
            }
            if (read > room)
                return FileRules.Fail<FileAppend>(FileStatus.TooLarge, UploadLimits.FileTooLarge(read + fewestAdded, maxFileBytes), "text");

            // The text goes in the file's own encoding, so a UTF-16 or UTF-32 file doesn't end in UTF-8 bytes.
            byte[] added = EncodingOf(combined.GetBuffer().AsSpan(0, (int)combined.Length)).GetBytes(text);
            if (combined.Length + added.Length > maxFileBytes)
                return FileRules.Fail<FileAppend>(FileStatus.TooLarge, UploadLimits.FileTooLarge(combined.Length + added.Length, maxFileBytes), "text");
            combined.Write(added);
            combined.Position = 0;
            long size = combined.Length; // the upload closes the stream

            // An explicit chunking strategy stays; otherwise the upload picks one again, as it did the first time.
            string? strategy = document.Metadata.GetValueOrDefault(IngestionPipeline.MetadataKeyStrategyExplicit) == "true"
                ? document.Metadata.GetValueOrDefault(IngestionPipeline.MetadataKeyChunkingStrategy)
                : null;
            var upload = new FileUpload(document.FileName, combined, PathUtilities.GetParentPath(document.Path), document.ContentType, strategy);
            var uploaded = await uploads.UploadAsync(UploadRules.Request(context, containerId, upload), ct);
            return uploaded.Success
                ? Attempt<FileAppend, FileStatus>.Succeed(new FileAppend(uploaded.DocumentId!, uploaded.JobId, document.Path, size))
                : FileRules.Fail<FileAppend>(FileStatus.Invalid, uploaded.Error ?? "The file could not be appended to.");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The encoding a file's byte order mark names, without a mark of its own; UTF-8 when it has none.</summary>
    internal static Encoding EncodingOf(ReadOnlySpan<byte> start) => start switch
    {
        [0xFF, 0xFE, 0x00, 0x00, ..] => new UTF32Encoding(bigEndian: false, byteOrderMark: false),
        [0x00, 0x00, 0xFE, 0xFF, ..] => new UTF32Encoding(bigEndian: true, byteOrderMark: false),
        [0xFF, 0xFE, ..] => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        [0xFE, 0xFF, ..] => new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    };

    /// <summary>
    /// Copies the source while it stays within <paramref name="limit"/> bytes, and answers how many it read: more than
    /// the limit means it stopped there, without copying the read that went past it.
    /// </summary>
    private static async Task<long> CopyAtMostAsync(Stream source, Stream destination, long limit, CancellationToken ct)
    {
        byte[] buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            copied += read;
            if (copied > limit)
                return copied;
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return copied;
    }
}
