namespace Connapse.Core.Interfaces;

/// <summary>
/// Counts tokens using a real tokenizer (BPE/tiktoken). Replaces the legacy
/// chars-times-0.25 heuristic in <c>Connapse.Ingestion.Utilities.TokenCounter</c>.
/// </summary>
public interface ITokenCounter
{
    int CountTokens(string text);

    /// <summary>
    /// Returns the character index in <paramref name="text"/> at which approximately
    /// <paramref name="tokenCount"/> tokens have been consumed (0-based, exclusive).
    /// Used by chunkers when no separator applies and a character-aligned split is needed.
    /// </summary>
    int GetIndexAtTokenCount(string text, int tokenCount);
}
