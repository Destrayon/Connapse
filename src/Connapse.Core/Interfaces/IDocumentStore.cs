namespace Connapse.Core.Interfaces;

public interface IDocumentStore
{
    Task<StoreResult> StoreAsync(Document document, CancellationToken ct = default);

    /// <summary>
    /// Writes a document's file and stores the document as <see cref="StoreAsync"/> does, but only while the folder it
    /// lands in still exists; null, with nothing written or stored, when a folder delete removed that folder first.
    /// The folder stays locked against a delete from before <paramref name="writeFile"/> runs until the document is
    /// stored, so a delete that comes after sees the document and removes it, file included.
    /// </summary>
    /// <remarks>
    /// A null <paramref name="folderId"/> is the container's root, which no delete removes. Either way the document's
    /// path stays locked against <see cref="RemoveUnrecordedFileAsync"/> from before the write until it is stored.
    /// </remarks>
    Task<StoreResult?> StoreInFolderAsync(Document document, Guid? folderId, Func<CancellationToken, Task> writeFile, CancellationToken ct = default);

    /// <summary>
    /// Runs <paramref name="deleteFile"/> only when no document holds <paramref name="path"/>, and returns whether it
    /// ran. The path is locked against <see cref="StoreInFolderAsync"/> meanwhile, so an upload to the same path
    /// either lands first, and its file is kept, or writes after the delete (CON-88).
    /// </summary>
    Task<bool> RemoveUnrecordedFileAsync(Guid containerId, string path, Func<CancellationToken, Task> deleteFile, CancellationToken ct = default);
    Task<Document?> GetAsync(string documentId, CancellationToken ct = default);
    Task<IReadOnlyList<Document>> ListAsync(Guid containerId, string? pathPrefix = null, int skip = 0, int take = 50, CancellationToken ct = default);
    Task DeleteAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Deletes a container's document and, in the same transaction, saves its stored file as owed a removal, so a
    /// removal that fails or is cut off is still retried and a sync meanwhile leaves the file alone (#378). Returns the
    /// owed removal's id, for the caller to complete once it has removed the file; null when nothing was deleted or
    /// the document has no stored file.
    /// </summary>
    Task<Guid?> DeleteOwingFileRemovalAsync(string documentId, CancellationToken ct = default);
    Task<bool> ExistsByPathAsync(Guid containerId, string path, CancellationToken ct = default);
    Task<Document?> GetByPathAsync(Guid containerId, string path, CancellationToken ct = default);
    Task<ContainerStats> GetContainerStatsAsync(Guid containerId, CancellationToken ct = default);

    /// <summary>
    /// The <c>resource_uri</c> of each requested document (null when it has none — uploads and non-cloud
    /// connectors — or the id is unknown). One batched lookup, for the search verifier which must map
    /// ranked hits back to their governing URIs.
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> GetResourceUrisAsync(
        IReadOnlyCollection<string> documentIds, CancellationToken ct = default);

    /// <summary>
    /// Those of <paramref name="documentIds"/> that have no <c>resource_uri</c> and are governed by Connapse's own
    /// access control alone: uploads, and documents from sources that never report one. A document whose source
    /// always reports one but which has none is left out, as search leaves it out (<see cref="SearchScopes.UnaddressedAdmitted"/>).
    /// </summary>
    Task<IReadOnlySet<string>> GetUnaddressedAdmittedAsync(
        IReadOnlyCollection<string> documentIds, CancellationToken ct = default);
}
