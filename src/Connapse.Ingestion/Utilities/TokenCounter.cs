namespace Connapse.Ingestion.Utilities;

/// <summary>
/// Estimates token count for text using a simple heuristic.
/// For more accurate token counting, consider using Microsoft.ML.Tokenizers or tiktoken.
/// </summary>
public static class TokenCounter
{
    // Approximate tokens per character ratio (empirically ~0.25 for English text)
    private const double TokensPerCharacter = 0.25;

    /// <summary>
    /// Estimates the number of tokens in the given text.
    /// Uses a simple heuristic: ~4 characters per token.
    /// </summary>
    /// <param name="text">The text to count tokens for.</param>
    /// <returns>Estimated token count.</returns>
    public static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        // Simple heuristic: count characters and divide by 4
        // This approximates OpenAI tokenization reasonably well for English text
        return (int)Math.Ceiling(text.Length * TokensPerCharacter);
    }

    /// <summary>
    /// Estimates the number of tokens in the given text using word count.
    /// Alternative method: assumes ~1.3 tokens per word on average.
    /// </summary>
    /// <param name="text">The text to count tokens for.</param>
    /// <returns>Estimated token count.</returns>
    public static int EstimateTokenCountByWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        // Split on whitespace and count words
        var words = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        // Approximate: 1.3 tokens per word on average
        return (int)Math.Ceiling(words.Length * 1.3);
    }

    /// <summary>
    /// Gets the character position that represents approximately the given token count.
    /// </summary>
    /// <param name="text">The text to analyze.</param>
    /// <param name="targetTokens">The target number of tokens.</param>
    /// <returns>Character position (0-based index).</returns>
    public static int GetCharacterPositionForTokens(string text, int targetTokens)
    {
        if (string.IsNullOrWhiteSpace(text) || targetTokens <= 0)
            return 0;

        // Estimate characters needed for target token count
        int targetChars = (int)(targetTokens / TokensPerCharacter);

        return Math.Min(targetChars, text.Length);
    }
}
