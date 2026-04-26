namespace Connapse.Core.Interfaces;

/// <summary>
/// Splits raw text into sentences. Replaces ad-hoc regex splitting with a tested
/// boundary detector that handles abbreviations, decimals, URLs, ellipses, and CJK.
/// </summary>
public interface ISentenceSegmenter
{
    /// <summary>
    /// Splits <paramref name="text"/> into sentences in document order. Empty
    /// input yields an empty list. Sentence text is returned verbatim — no
    /// trimming, no normalization — so callers can compute offsets if needed.
    /// </summary>
    IReadOnlyList<string> Split(string text);
}
