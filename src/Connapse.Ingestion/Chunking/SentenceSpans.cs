namespace Connapse.Ingestion.Chunking;

/// <summary>
/// Maps a sentence segmenter's output back onto the text it came from, so chunkers can slice
/// chunks out of the source instead of re-joining the segmenter's strings (CON-143).
///
/// The segmenter's sentences are not verbatim slices: PragmaticSegmenterNet strips anything that
/// looks like an HTML tag (<c>List&lt;Group&gt;</c> comes back as <c>List</c>), collapses
/// whitespace, and splits at a '.' with no space after it (<c>searchSettings.</c> |
/// <c>CurrentValue</c>). Each sentence's characters are matched in order against the source,
/// skipping whitespace and any source text the segmenter deleted, which places the sentence ends
/// in the source whatever was rewritten in between.
/// </summary>
internal static class SentenceSpans
{
    /// <summary>How far past the last match a sentence character is looked for before it is skipped.</summary>
    internal const int SearchWindow = 4096;

    /// <summary>
    /// Returns spans that tile <paramref name="content"/> end to end: the first starts at 0, each
    /// starts where the one before ends, and the last ends at <c>content.Length</c>, so no text
    /// falls between them. Each span holds one or more sentences with their trailing whitespace.
    /// A boundary is kept only where whitespace follows the sentence in the source, so a split
    /// inside a token such as <c>a.b</c> joins the two sentences. Empty content yields no spans.
    /// </summary>
    public static IReadOnlyList<(int Start, int End)> Locate(string content, IEnumerable<string> sentences)
    {
        if (content.Length == 0)
            return [];

        var starts = new List<int> { 0 };
        int pos = 0;
        foreach (string sentence in sentences)
        {
            int end = MatchEnd(content, sentence, ref pos);
            if (end == 0 || end >= content.Length || !char.IsWhiteSpace(content[end]))
                continue;

            int next = end;
            while (next < content.Length && char.IsWhiteSpace(content[next])) next++;
            if (next < content.Length)
                starts.Add(next);
        }

        var spans = new List<(int Start, int End)>(starts.Count);
        for (int i = 0; i < starts.Count; i++)
            spans.Add((starts[i], i + 1 < starts.Count ? starts[i + 1] : content.Length));
        return spans;
    }

    /// <summary>The span without its leading and trailing whitespace; an all-whitespace span comes back empty.</summary>
    public static (int Start, int End) Trim(string content, (int Start, int End) span)
    {
        string slice = content[span.Start..span.End];
        int end = span.Start + slice.TrimEnd().Length;
        return (end - slice.Trim().Length, end);
    }

    /// <summary>
    /// Matches the sentence's non-whitespace characters in order from <paramref name="pos"/>, taking
    /// each one's next occurrence. A character not found within <see cref="SearchWindow"/> is one the
    /// segmenter added or changed, and is skipped. Returns the index just past the last match, or 0
    /// when nothing matched.
    /// </summary>
    private static int MatchEnd(string content, string sentence, ref int pos)
    {
        int end = 0;
        foreach (char c in sentence)
        {
            if (char.IsWhiteSpace(c))
                continue;
            int found = content.IndexOf(c, pos, Math.Min(SearchWindow, content.Length - pos));
            if (found < 0)
                continue;
            pos = found + 1;
            end = pos;
        }
        return end;
    }
}
