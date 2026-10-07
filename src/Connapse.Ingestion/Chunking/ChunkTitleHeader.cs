using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Utilities;

namespace Connapse.Ingestion.Chunking;

/// <summary>
/// Puts the document's title on its own line at the start of every chunk (#671), so a chunk deep in a
/// long page, transcript or thread still says what it belongs to, for keyword search (which indexes
/// chunk content) and for the embedding alike.
/// </summary>
public static class ChunkTitleHeader
{
    /// <summary>A title longer than this is cut: it is repeated in every chunk and counts against each one's budget.</summary>
    public const int MaxTitleTokens = 32;

    private static readonly Lazy<TiktokenTokenCounter> Tokens = new(() => new TiktokenTokenCounter());

    /// <summary>
    /// The parser's title (email subject, HTML/Office/PDF/EPUB title), else the file name without its
    /// extension when it has a letter in it: a name like "0000042" or "2026-10-07" says nothing about
    /// the content and would only add noise to every chunk.
    /// </summary>
    public static string? TitleOf(ParsedDocument parsed, string? fileName)
    {
        if (parsed.Metadata.TryGetValue("Title", out string? title) && !string.IsNullOrWhiteSpace(title))
            return Cap(title.Trim());
        string? stem = fileName is null ? null : Path.GetFileNameWithoutExtension(fileName).Trim();
        return string.IsNullOrEmpty(stem) || !stem.Any(char.IsLetter) ? null : Cap(stem);
    }

    private static string Cap(string title)
    {
        if (Tokens.Value.CountTokens(title) <= MaxTitleTokens)
            return title;
        return title[..Tokens.Value.GetIndexAtTokenCount(title, MaxTitleTokens)].TrimEnd();
    }

    /// <summary>Tokens the title line adds to a chunk.</summary>
    public static int HeaderTokens(string? title) =>
        string.IsNullOrWhiteSpace(title) ? 0 : Tokens.Value.CountTokens(title + "\n\n");

    /// <summary>
    /// The chunking settings with room left for the title line, so a chunk plus its title still fits
    /// <see cref="ChunkingSettings.MaxChunkSize"/>.
    /// </summary>
    public static ChunkingSettings Budget(ChunkingSettings settings, string? title)
    {
        int header = HeaderTokens(title);
        return header == 0 ? settings : settings with { MaxChunkSize = Math.Max(1, settings.MaxChunkSize - header) };
    }

    /// <summary>
    /// Prepends <paramref name="title"/> to each chunk that doesn't already start with it. Such a chunk's
    /// precomputed vector (Semantic's pooled sentence windows) didn't see the title, so it is dropped and
    /// the pipeline embeds the chunk's new text; like a header breadcrumb, the content is then no longer
    /// verbatim, so its offsets are marked estimated. A sentence window (SentenceWindow chunking) gets the
    /// title too, since search returns the window in place of the content.
    /// </summary>
    public static IReadOnlyList<ChunkInfo> Prepend(IReadOnlyList<ChunkInfo> chunks, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return chunks;
        int header = HeaderTokens(title);
        return chunks
            .Select(c =>
            {
                if (c.Content.StartsWith(title, StringComparison.Ordinal))
                    return c;
                Dictionary<string, string> metadata = new(c.Metadata) { ["OffsetEstimated"] = "true" };
                if (metadata.TryGetValue("window", out string? window) && !window.StartsWith(title, StringComparison.Ordinal))
                    metadata["window"] = $"{title}\n\n{window}";
                return c with
                {
                    Content = $"{title}\n\n{c.Content}",
                    TokenCount = c.TokenCount + header,
                    PrecomputedEmbedding = null,
                    Metadata = metadata,
                };
            })
            .ToList();
    }
}
