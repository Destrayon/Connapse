using Connapse.Core.Interfaces;
using PragmaticSegmenterNet;

namespace Connapse.Ingestion.Utilities;

/// <summary>
/// Sentence segmenter backed by PragmaticSegmenterNet (a .NET port of the Ruby
/// pragmatic_segmenter golden-rules engine). Defaults to English.
/// </summary>
public class PragmaticSentenceSegmenter(Language language = Language.English) : ISentenceSegmenter
{
    public IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();
        return Segmenter.Segment(text, language);
    }
}
