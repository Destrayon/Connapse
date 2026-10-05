using System.Text;
using System.Text.RegularExpressions;
using Connapse.Core.Interfaces;
using PragmaticSegmenterNet;

namespace Connapse.Ingestion.Utilities;

/// <summary>
/// Sentence segmenter backed by PragmaticSegmenterNet (a .NET port of the Ruby
/// pragmatic_segmenter golden-rules engine). Defaults to English.
/// </summary>
public class PragmaticSentenceSegmenter(Language language = Language.English) : ISentenceSegmenter
{
    /// <summary>
    /// Stands in for '$' while the library runs. The library builds Regex.Replace replacements out
    /// of matched text without escaping them, and .NET reads $_ $&amp; $` $' there as the whole
    /// input, the match, and the text before and after it: a `$_` inside parentheses spliced the
    /// document into itself until ingestion ran out of memory or took hours (#663).
    /// </summary>
    private const char DollarPlaceholder = '';

    public IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        // Text that already holds the placeholder can't be restored unambiguously.
        if (text.Contains(DollarPlaceholder))
            return SplitOnTerminators(text);

        try
        {
            if (!text.Contains('$'))
                return Segmenter.Segment(text, language);
            return Segmenter.Segment(text.Replace('$', DollarPlaceholder), language)
                .Select(s => s.Replace(DollarPlaceholder, '$'))
                .ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            // One of the library's patterns backtracks without end on some inputs (long digit
            // runs from a timetable PDF, #595). RegexTimeout gives it a deadline; this keeps the
            // document instead of failing it over sentence boundaries.
            return SplitOnTerminators(text);
        }
    }

    /// <summary>
    /// Splits after '.', '!' or '?' followed by whitespace, and at blank lines. Cruder than the
    /// golden rules -- it splits after abbreviations -- but linear and regex-free.
    /// </summary>
    internal static IReadOnlyList<string> SplitOnTerminators(string text)
    {
        var sentences = new List<string>();
        var current = new StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            current.Append(c);

            bool atEnd = i + 1 >= text.Length;
            bool terminator = (c is '.' or '!' or '?') && (atEnd || char.IsWhiteSpace(text[i + 1]));
            bool blankLine = c == '\n' && !atEnd && text[i + 1] is ('\n' or '\r');

            if (terminator || blankLine)
                Flush();
        }

        Flush();
        return sentences;

        void Flush()
        {
            string sentence = current.ToString().Trim();
            if (sentence.Length > 0)
                sentences.Add(sentence);
            current.Clear();
        }
    }
}
