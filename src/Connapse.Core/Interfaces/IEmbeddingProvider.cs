namespace Connapse.Core.Interfaces;

public interface IEmbeddingProvider
{
    /// <param name="inputType">
    /// Which side of a search the text is on. Required so no caller can forget it: models trained
    /// with a query/document instruction embed each side differently.
    /// </param>
    Task<float[]> EmbedAsync(string text, EmbeddingInputType inputType, CancellationToken ct = default);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, EmbeddingInputType inputType, CancellationToken ct = default);
    int Dimensions { get; }
    string ModelId { get; }
}
