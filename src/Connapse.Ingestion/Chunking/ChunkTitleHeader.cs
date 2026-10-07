using Connapse.Core.Interfaces;

namespace Connapse.Ingestion.Chunking;

/// <summary>
/// Puts the document's title on its own line at the start of every chunk (#671), so a chunk deep in a
/// long page, transcript or thread still says what it belongs to, for keyword search (which indexes
/// chunk content) and for the embedding alike.
/// </summary>
public static class ChunkTitleHeader
{
    /// <summary>The parser's title (email subject, HTML/Office/PDF/EPUB title), else the file name without its extension.</summary>
    public static string? TitleOf(ParsedDocument parsed, string? fileName)
    {
        if (parsed.Metadata.TryGetValue("Title", out string? title) && !string.IsNullOrWhiteSpace(title))
            return title.Trim();
        string? stem = fileName is null ? null : Path.GetFileNameWithoutExtension(fileName).Trim();
        return string.IsNullOrEmpty(stem) ? null : stem;
    }

    /// <summary>
    /// Prepends <paramref name="title"/> to each chunk that doesn't already start with it. Such a chunk's
    /// precomputed vector (Semantic's pooled sentence windows) didn't see the title, so it is dropped and
    /// the pipeline embeds the chunk's new text; like a header breadcrumb, the content is then no longer
    /// verbatim, so its offsets are marked estimated.
    /// </summary>
    public static IReadOnlyList<ChunkInfo> Prepend(IReadOnlyList<ChunkInfo> chunks, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return chunks;
        return chunks
            .Select(c => c.Content.StartsWith(title, StringComparison.Ordinal)
                ? c
                : c with
                {
                    Content = $"{title}\n\n{c.Content}",
                    PrecomputedEmbedding = null,
                    Metadata = new Dictionary<string, string>(c.Metadata) { ["OffsetEstimated"] = "true" },
                })
            .ToList();
    }
}
