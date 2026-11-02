namespace Connapse.Storage.Vectors;

/// <summary>The embedding model refused a text because it is longer than its context length.</summary>
public sealed class EmbeddingInputTooLongException(string message, string reason) : InvalidOperationException(message)
{
    /// <summary>The provider's own words.</summary>
    public string Reason { get; } = reason;
}
