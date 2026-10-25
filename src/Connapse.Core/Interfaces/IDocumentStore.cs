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
    Task<bool> ExistsByPathAsync(Guid containerId, string path, CancellationToken ct = default);
    Task<Document?> GetByPathAsync(Guid containerId, string path, CancellationToken ct = default);
    Task<ContainerStats> GetContainerStatsAsync(Guid containerId, CancellationToken ct = default);
    Task UpdateSummaryAsync(string documentId, string? summary, DateTime? generatedAt, string? contentHash, CancellationToken ct = default);

    /// <summary>
    /// Clears the cached per-document summary fields (Summary, SummaryGeneratedAt, SummaryContentHash)
    /// for every document in a container. Returns the number of rows affected. Embeddings, chunks, and
    /// the documents themselves are left intact — this only drops the summary cache so it can be
    /// regenerated. Pairs with <see cref="IContainerStore.UpdateSummaryAsync"/> (nulls) to fully reset
    /// a container's summarization state.
    /// </summary>
    Task<int> ClearDocumentSummariesAsync(Guid containerId, CancellationToken ct = default);

    /// <summary>
    /// The document's text rebuilt from its stored chunks in order, without the overlap between
    /// neighbouring chunks. Null when it has no chunks.
    /// </summary>
    Task<string?> GetDocumentTextAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Returns container IDs whose docs have summaries newer than the container's own summary
    /// (or whose container has no summary at all but some docs do). Used by the hourly sweep
    /// to catch any containers missed by event-driven rollup triggering.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindContainersWithStaleSummariesAsync(CancellationToken ct = default);

    /// <summary>
    /// The <c>resource_uri</c> of each requested document (null when it has none — uploads and non-cloud
    /// connectors — or the id is unknown). One batched lookup, for the search verifier which must map
    /// ranked hits back to their governing URIs.
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> GetResourceUrisAsync(
        IReadOnlyCollection<string> documentIds, CancellationToken ct = default);
}
