using System.Globalization;
using System.Text.RegularExpressions;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Ingestion.Chunking;

/// <summary>
/// Chunks a record — an issue or pull request with its discussion — as one piece whenever it fits
/// the token budget, because a ticket is read as a whole: the question, the attempts, and the
/// answer only make sense together.
/// <para>
/// A record over budget is split at its comment boundaries (<c>--- … ---</c> lines, as
/// GitHubRecordRenderer writes them), packing adjacent parts together up to the budget. Every
/// piece is prefixed with the record's header — number, title, state, links — so each chunk
/// still says which record it belongs to. A single part that is too large on its own is split
/// further by the recursive chunker. Nothing cleverer is attempted: most records fit one chunk.
/// </para>
/// </summary>
public sealed partial class RecordChunker(ITokenCounter tokenCounter, RecursiveChunker recursiveChunker) : IChunkingStrategy
{
    public string Name => "Record";

    public async Task<IReadOnlyList<ChunkInfo>> ChunkAsync(
        ParsedDocument parsedDocument,
        ChunkingSettings settings,
        CancellationToken cancellationToken = default)
    {
        string content = parsedDocument.Content;
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var chunks = new List<ChunkInfo>();

        if (tokenCounter.CountTokens(content) <= settings.MaxChunkSize)
        {
            chunks.Add(Build(parsedDocument, content, 0, content.Length, 0, offsetEstimated: false));
            return chunks;
        }

        // The header is the opening block, up to the first blank line: title, facts, and edges.
        int headerEnd = content.IndexOf("\n\n", StringComparison.Ordinal);
        int bodyStart = headerEnd < 0 ? 0 : headerEnd + 2;

        // Every piece repeats the header, so it may take at most half the budget — a record that
        // references hundreds of others would otherwise leave nothing for the text. Whatever it
        // takes is subtracted in full, so header plus text never exceeds the limit.
        string header = FitHeader(headerEnd < 0 ? "" : content[..headerEnd].Trim(), settings.MaxChunkSize / 2);
        int budget = settings.MaxChunkSize - tokenCounter.CountTokens(header) - 4;

        var parts = SplitAtComments(content, bodyStart);
        int index = 0;
        int packStart = -1;
        int packEnd = -1;

        async Task FlushAsync()
        {
            if (packStart < 0) return;
            await EmitAsync(parsedDocument, content, header, packStart, packEnd, budget, settings, chunks, () => index++, cancellationToken);
            packStart = packEnd = -1;
        }

        foreach ((int start, int end) in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (packStart >= 0 && tokenCounter.CountTokens(content[packStart..end]) <= budget)
            {
                packEnd = end;
                continue;
            }

            await FlushAsync();
            packStart = start;
            packEnd = end;
        }

        await FlushAsync();
        return chunks;
    }

    /// <summary>Emits one packed span, splitting it further if it is still over budget on its own.</summary>
    private async Task EmitAsync(
        ParsedDocument doc, string content, string header, int start, int end, int budget,
        ChunkingSettings settings, List<ChunkInfo> chunks, Func<int> nextIndex, CancellationToken ct)
    {
        string span = content[start..end];
        if (string.IsNullOrWhiteSpace(span))
            return;

        if (tokenCounter.CountTokens(span) <= budget)
        {
            chunks.Add(Build(doc, WithHeader(header, span), start, end, nextIndex(), offsetEstimated: header.Length > 0));
            return;
        }

        var pieces = await recursiveChunker.ChunkAsync(
            new ParsedDocument(span, new Dictionary<string, string>(), []),
            // The minimum is lowered with the maximum: left at its default, the recursive chunker
            // merges small pieces back together past the reduced budget.
            settings with
            {
                MaxChunkSize = budget,
                MinChunkSize = Math.Min(settings.MinChunkSize, budget / 4),
                Overlap = Math.Min(settings.Overlap, budget / 4),
            },
            ct);

        foreach (var piece in pieces)
        {
            chunks.Add(Build(
                doc, WithHeader(header, piece.Content),
                start + piece.StartOffset, start + piece.EndOffset, nextIndex(), offsetEstimated: true));
        }
    }

    /// <summary>
    /// The body and each comment as <c>(start, end)</c> spans of <paramref name="content"/>. The
    /// body is everything before the first comment delimiter.
    /// </summary>
    private static List<(int Start, int End)> SplitAtComments(string content, int bodyStart)
    {
        var starts = new List<int> { bodyStart };
        foreach (Match m in CommentDelimiter().Matches(content, bodyStart))
        {
            if (m.Index > bodyStart)
                starts.Add(m.Index);
        }

        var spans = new List<(int, int)>();
        for (int i = 0; i < starts.Count; i++)
            spans.Add((starts[i], i + 1 < starts.Count ? starts[i + 1] : content.Length));

        return spans;
    }

    /// <summary>
    /// Shortens a header to <paramref name="maxTokens"/>: whole lines are dropped from the end
    /// first (the edge list, then the facts), keeping the title line, which is what says which
    /// record a chunk belongs to; a title that is still too long is cut.
    /// </summary>
    private string FitHeader(string header, int maxTokens)
    {
        if (tokenCounter.CountTokens(header) <= maxTokens)
            return header;

        var lines = header.Split('\n').ToList();
        while (lines.Count > 1 && tokenCounter.CountTokens(string.Join('\n', lines)) > maxTokens)
            lines.RemoveAt(lines.Count - 1);

        string fitted = string.Join('\n', lines);
        while (fitted.Length > 0 && tokenCounter.CountTokens(fitted + "…") > maxTokens)
            fitted = fitted[..(fitted.Length * 3 / 4)];

        return fitted.Length < lines[0].Length ? fitted + "…" : fitted;
    }

    private static string WithHeader(string header, string text) =>
        header.Length == 0 ? text.Trim() : header + "\n\n" + text.Trim();

    private ChunkInfo Build(ParsedDocument doc, string text, int start, int end, int chunkIndex, bool offsetEstimated)
    {
        var metadata = new Dictionary<string, string>(doc.Metadata)
        {
            ["ChunkingStrategy"] = Name,
            ["ChunkIndex"] = chunkIndex.ToString(CultureInfo.InvariantCulture),
        };
        if (offsetEstimated)
            metadata["OffsetEstimated"] = "true";

        string trimmed = text.Trim();
        return new ChunkInfo(
            Content: trimmed,
            ChunkIndex: chunkIndex,
            TokenCount: tokenCounter.CountTokens(trimmed),
            StartOffset: start,
            EndOffset: end,
            Metadata: metadata);
    }

    [GeneratedRegex(@"^--- .+ ---$", RegexOptions.Multiline)]
    private static partial Regex CommentDelimiter();
}
