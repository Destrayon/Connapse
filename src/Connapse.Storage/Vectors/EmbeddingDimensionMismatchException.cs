namespace Connapse.Storage.Vectors;

/// <summary>The embedding model returned vectors of a different size than the Dimensions setting.</summary>
public sealed class EmbeddingDimensionMismatchException(string modelId, int expected, int actual, bool sentDimensions)
    : InvalidOperationException(MessageFor(modelId, expected, actual, sentDimensions))
{
    public string ModelId { get; } = modelId;

    /// <summary>The Dimensions setting.</summary>
    public int Expected { get; } = expected;

    /// <summary>The length of the vector the model returned.</summary>
    public int Actual { get; } = actual;

    private static string MessageFor(string modelId, int expected, int actual, bool sentDimensions) =>
        $"The embedding model '{modelId}' returned {actual}-dimension vectors, but Settings > Embedding > Dimensions is {expected}, " +
        "so they were rejected. " + (sentDimensions
            ? $"The model was asked for {expected} and ignored it: set Dimensions to {actual}, or use an endpoint that honors the dimensions parameter."
            : $"Set Dimensions to {actual}, or, if the model can return shorter vectors, turn on \"Ask the model for this size\".");
}
