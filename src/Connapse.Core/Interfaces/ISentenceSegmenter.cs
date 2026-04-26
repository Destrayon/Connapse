namespace Connapse.Core.Interfaces;

/// <summary>
/// Splits raw text into sentences. Replaces ad-hoc regex splitting with a tested
/// boundary detector that handles abbreviations, decimals, URLs, ellipses, and CJK.
/// </summary>
public interface ISentenceSegmenter
{
    /// <summary>
    /// Splits <paramref name="text"/> into sentences in document order. Empty
    /// or whitespace-only input yields an empty list. Implementations may
    /// normalize whitespace inside or around sentences (e.g., trimming or
    /// collapsing internal whitespace) — callers that need byte-exact source
    /// offsets must locate sentence boundaries via <c>IndexOf</c> rather than
    /// rely on the returned strings being verbatim slices.
    /// </summary>
    IReadOnlyList<string> Split(string text);
}
