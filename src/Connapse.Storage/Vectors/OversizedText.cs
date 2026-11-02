namespace Connapse.Storage.Vectors;

/// <summary>
/// Embeds text longer than the model's context length the way OpenAI's cookbook does
/// ("Embedding texts that are longer than the model's maximum context length"): split it, embed
/// each piece, and average the pieces' vectors weighted by length, then rescale to unit length.
/// The stored chunk text is left whole; only what is sent to the model is split.
/// </summary>
public static class OversizedText
{
    /// <summary>
    /// Shortest text that is still split. A model that rejects anything this short has a context
    /// length no text could fit, so splitting further would only send thousands of requests.
    /// </summary>
    public const int MinSplitLength = 32;

    /// <summary>
    /// True when Ollama's error says the input was over the context length. Its wordings:
    /// "the input length exceeds the context length" (400 or 413), and "input exceeds maximum
    /// context length and cannot be truncated further" when its own truncation could not help.
    /// </summary>
    public static bool IsContextLengthError(string message) =>
        message.Contains("context length", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits <paramref name="text"/> in two near its middle, at the whitespace closest to the
    /// middle when the middle half has any, so words stay whole.
    /// </summary>
    public static bool TrySplit(string text, out string first, out string second)
    {
        if (text.Length < MinSplitLength)
        {
            first = second = string.Empty;
            return false;
        }

        int middle = text.Length / 2;
        int at = middle;
        for (int offset = 0; offset <= text.Length / 4; offset++)
        {
            if (char.IsWhiteSpace(text[middle - offset]))
            {
                at = middle - offset;
                break;
            }
            if (char.IsWhiteSpace(text[middle + offset]))
            {
                at = middle + offset;
                break;
            }
        }

        // Never cut between the two halves of a surrogate pair.
        if (char.IsLowSurrogate(text[at]))
            at++;

        first = text[..at];
        second = text[at..];
        return true;
    }

    /// <summary>Length-weighted mean of the pieces' vectors, rescaled to unit length.</summary>
    public static float[] Pool(IReadOnlyList<(float[] Vector, int Length)> pieces)
    {
        if (pieces.Count == 1)
            return pieces[0].Vector;

        double[] sum = new double[pieces[0].Vector.Length];
        foreach ((float[] vector, int length) in pieces)
            for (int d = 0; d < sum.Length; d++)
                sum[d] += vector[d] * (double)length;

        double norm = Math.Sqrt(sum.Sum(v => v * v));
        float[] pooled = new float[sum.Length];
        for (int d = 0; d < sum.Length; d++)
            pooled[d] = norm > 0 ? (float)(sum[d] / norm) : 0f;
        return pooled;
    }
}
