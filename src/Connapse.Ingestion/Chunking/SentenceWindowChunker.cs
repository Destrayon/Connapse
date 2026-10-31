using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Ingestion.Chunking;

/// <summary>
/// Sentence-window chunker. Each sentence becomes its own ChunkInfo whose
/// Content is the sentence (this gets embedded). Metadata["window"] holds the
/// N-neighbor join (default 3 → 7-sentence window). Metadata["original_text"]
/// preserves the matched sentence for citation correctness. Bypasses the
/// MinChunkSize merge-forward post-pass — chunks are intentionally tiny so
/// retrieval is precise; the wider window is substituted into search results
/// post-rerank by HybridSearchService.
/// </summary>
public class SentenceWindowChunker(ITokenCounter tokenCounter, ISentenceSegmenter sentenceSegmenter) : IChunkingStrategy
{
    public string Name => "SentenceWindow";

    public async Task<IReadOnlyList<ChunkInfo>> ChunkAsync(
        ParsedDocument parsedDocument,
        ChunkingSettings settings,
        CancellationToken cancellationToken = default)
    {
        var chunks = new List<ChunkInfo>();
        string content = parsedDocument.Content;

        if (string.IsNullOrWhiteSpace(content))
            return chunks;

        // The segmenter's sentences are not verbatim (it strips <...> and splits inside a.b), so
        // sentences and windows are sliced from the source, never the segmenter's strings (CON-143).
        // A segmenter that returns nothing yields one span, so the content is never dropped, and spans
        // over MaxChunkSize are split so none is embedded whole.
        IReadOnlyList<(int Start, int End)> located = await SentenceSpans.SplitOversize(
            content, SentenceSpans.Locate(content, sentenceSegmenter.Split(content)), settings, tokenCounter, cancellationToken);
        List<(int Start, int End)> spans = located
            .Select(span => SentenceSpans.Trim(content, span))
            .ToList();

        int windowSize = Math.Max(0, settings.SentenceWindowSize);

        for (int i = 0; i < spans.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int lo = Math.Max(0, i - windowSize);
            int hi = Math.Min(spans.Count, i + windowSize + 1);

            string window = content[spans[lo].Start..spans[hi - 1].End];

            (int sentenceOffset, int endOffset) = spans[i];
            string sentenceText = content[sentenceOffset..endOffset];
            int sentenceTokens = tokenCounter.CountTokens(sentenceText);

            var metadata = new Dictionary<string, string>(parsedDocument.Metadata)
            {
                ["ChunkingStrategy"] = Name,
                ["ChunkIndex"] = i.ToString(),
                ["window"] = window,
                ["original_text"] = sentenceText,
                ["window_size"] = windowSize.ToString()
            };

            chunks.Add(new ChunkInfo(
                Content: sentenceText,
                ChunkIndex: i,
                TokenCount: sentenceTokens,
                StartOffset: sentenceOffset,
                EndOffset: endOffset,
                Metadata: metadata));
        }

        return chunks;
    }
}
