namespace AIKnowledge.Core.Interfaces;

public interface IDocumentStore
{
    Task<string> StoreAsync(Document document, CancellationToken ct = default);
    Task<Document?> GetAsync(string documentId, CancellationToken ct = default);
    Task<IReadOnlyList<Document>> ListAsync(Guid containerId, string? pathPrefix = null, CancellationToken ct = default);
    Task DeleteAsync(string documentId, CancellationToken ct = default);
    Task<bool> ExistsByPathAsync(Guid containerId, string path, CancellationToken ct = default);
}
