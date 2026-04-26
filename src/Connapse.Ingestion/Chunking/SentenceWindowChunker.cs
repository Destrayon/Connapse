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

    public Task<IReadOnlyList<ChunkInfo>> ChunkAsync(
        ParsedDocument parsedDocument,
        ChunkingSettings settings,
        CancellationToken cancellationToken = default)
    {
        var chunks = new List<ChunkInfo>();
        string content = parsedDocument.Content;

        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult<IReadOnlyList<ChunkInfo>>(chunks);

        IReadOnlyList<string> rawSentences = sentenceSegmenter.Split(content);
        if (rawSentences.Count == 0)
            return Task.FromResult<IReadOnlyList<ChunkInfo>>(chunks);

        // Locate each sentence's source offset using a moving cursor.
        var spans = new List<(string Text, int Offset, int Tokens)>(rawSentences.Count);
        int cursor = 0;
        bool anyOffsetEstimated = false;
        foreach (string raw in rawSentences)
        {
            string trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;

            int idx = content.IndexOf(trimmed, cursor, StringComparison.Ordinal);
            if (idx < 0) { idx = cursor; anyOffsetEstimated = true; }

            // Clamp startOffset to a valid bound — cursor can drift past content.Length
            // if the segmenter ever expands a sentence beyond its source slice.
            int clampedIdx = Math.Min(Math.Max(idx, 0), content.Length);
            if (clampedIdx != idx) anyOffsetEstimated = true;

            spans.Add((trimmed, clampedIdx, tokenCounter.CountTokens(trimmed)));
            // Clamp cursor advancement too: clampedIdx + trimmed.Length can exceed
            // content.Length when the segmenter returns a sentence longer than its
            // source slice (or when clampedIdx == content.Length). Without this,
            // the next IndexOf would throw ArgumentOutOfRangeException.
            int nextCursor = clampedIdx + trimmed.Length;
            if (nextCursor > content.Length) anyOffsetEstimated = true;
            cursor = Math.Min(nextCursor, content.Length);
        }

        int windowSize = Math.Max(0, settings.SentenceWindowSize);

        for (int i = 0; i < spans.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int lo = Math.Max(0, i - windowSize);
            int hi = Math.Min(spans.Count, i + windowSize + 1);

            string window = string.Join(
                " ",
                Enumerable.Range(lo, hi - lo).Select(j => spans[j].Text));

            (string sentenceText, int sentenceOffset, int sentenceTokens) = spans[i];

            var metadata = new Dictionary<string, string>(parsedDocument.Metadata)
            {
                ["ChunkingStrategy"] = Name,
                ["ChunkIndex"] = i.ToString(),
                ["window"] = window,
                ["original_text"] = sentenceText,
                ["window_size"] = windowSize.ToString()
            };
            if (anyOffsetEstimated)
                metadata["OffsetEstimated"] = "true";

            // Clamp EndOffset so consumers can safely substring even when the
            // segmenter normalized the sentence to be longer than its source slice.
            int endOffset = Math.Min(content.Length, sentenceOffset + sentenceText.Length);
            chunks.Add(new ChunkInfo(
                Content: sentenceText,
                ChunkIndex: i,
                TokenCount: sentenceTokens,
                StartOffset: sentenceOffset,
                EndOffset: endOffset,
                Metadata: metadata));
        }

        return Task.FromResult<IReadOnlyList<ChunkInfo>>(chunks);
    }
}
