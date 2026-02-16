namespace Connapse.Core.Interfaces;

public interface IDocumentStore
{
    Task<string> StoreAsync(Document document, CancellationToken ct = default);
    Task<Document?> GetAsync(string documentId, CancellationToken ct = default);
    Task<IReadOnlyList<Document>> ListAsync(string? collectionId = null, CancellationToken ct = default);
    Task DeleteAsync(string documentId, CancellationToken ct = default);
}
