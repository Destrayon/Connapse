using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Ingestion.Chunking;

/// <summary>
/// Token-budgeted chunker that respects sentence boundaries — never splits a
/// sentence mid-way. Mirrors AWS Bedrock's "Default" chunking strategy. A better
/// baseline than <see cref="FixedSizeChunker"/> for users who don't want to pay
/// the embedding cost of <see cref="SemanticChunker"/> but still want
/// retrieval-friendly chunk boundaries.
/// </summary>
public class SentenceAwareFixedSizeChunker(
    ITokenCounter tokenCounter,
    ISentenceSegmenter sentenceSegmenter,
    RecursiveChunker recursiveChunker) : IChunkingStrategy
{
    public string Name => "SentenceAwareFixedSize";

    public async Task<IReadOnlyList<ChunkInfo>> ChunkAsync(
        ParsedDocument parsedDocument,
        ChunkingSettings settings,
        CancellationToken cancellationToken = default)
    {
        var chunks = new List<ChunkInfo>();
        string content = parsedDocument.Content;

        if (string.IsNullOrWhiteSpace(content))
            return chunks;

        IReadOnlyList<string> rawSentences = sentenceSegmenter.Split(content);
        if (rawSentences.Count == 0)
        {
            chunks.Add(BuildChunk(parsedDocument, content, 0, content.Length, 0));
            return chunks;
        }

        // Locate every sentence's source offset (moving cursor; fallback to cursor if
        // the segmenter normalized whitespace and the sentence isn't a verbatim slice).
        var sentenceSpans = new List<(string Text, int Offset, int Tokens)>();
        int cursor = 0;
        foreach (string s in rawSentences)
        {
            string trimmed = s.Trim();
            if (trimmed.Length == 0) continue;
            int idx = content.IndexOf(trimmed, cursor, StringComparison.Ordinal);
            if (idx < 0) idx = cursor;
            sentenceSpans.Add((trimmed, idx, tokenCounter.CountTokens(trimmed)));
            cursor = idx + trimmed.Length;
        }

        var rawChunks = new List<(int Start, int End)>();
        var buffer = new List<(string Text, int Offset, int Tokens)>();
        int total = 0;

        void Flush()
        {
            if (buffer.Count == 0) return;
            int start = buffer[0].Offset;
            int end = buffer[^1].Offset + buffer[^1].Text.Length;
            rawChunks.Add((start, end));
        }

        foreach ((string text, int offset, int tokens) sentence in sentenceSpans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Single sentence exceeds the budget on its own — recurse via RecursiveChunker
            // for hierarchical sub-splitting. Flush any buffered sentences first.
            if (sentence.tokens > settings.MaxChunkSize)
            {
                Flush();
                buffer.Clear();
                total = 0;

                var subDoc = new ParsedDocument(sentence.text, parsedDocument.Metadata, parsedDocument.Warnings);
                var subResult = await recursiveChunker.ChunkAsync(subDoc, settings, cancellationToken);
                foreach (ChunkInfo sub in subResult)
                {
                    int subStart = sentence.offset + sub.StartOffset;
                    int subEnd = sentence.offset + sub.EndOffset;
                    rawChunks.Add((subStart, Math.Min(subEnd, content.Length)));
                }
                continue;
            }

            int join = buffer.Count > 0 ? 1 : 0;
            if (total + sentence.tokens + join > settings.MaxChunkSize && buffer.Count > 0)
            {
                Flush();
                // Pop from head until overlap fits OR there's room for the next sentence.
                while (total > settings.Overlap
                    || (buffer.Count > 0 && total + sentence.tokens + (buffer.Count > 1 ? 1 : 0) > settings.MaxChunkSize))
                {
                    if (buffer.Count == 0) break;
                    total -= buffer[0].Tokens + (buffer.Count > 1 ? 1 : 0);
                    buffer.RemoveAt(0);
                }
            }
            buffer.Add(sentence);
            total += sentence.tokens + (buffer.Count > 1 ? 1 : 0);
        }
        Flush();

        var merged = MergeForwardSmallChunks(rawChunks, settings.MinChunkSize, content);
        if (merged.Count == 0)
        {
            chunks.Add(BuildChunk(parsedDocument, content, 0, content.Length, 0));
            return chunks;
        }

        int chunkIndex = 0;
        foreach ((int start, int end) span in merged)
        {
            chunks.Add(BuildChunk(parsedDocument, content, span.start, span.end, chunkIndex++));
        }

        return chunks;
    }

    private ChunkInfo BuildChunk(
        ParsedDocument parsedDocument,
        string content,
        int start,
        int end,
        int chunkIndex)
    {
        string text = content.Substring(start, end - start);
        int tokens = tokenCounter.CountTokens(text);
        var metadata = new Dictionary<string, string>(parsedDocument.Metadata)
        {
            ["ChunkingStrategy"] = Name,
            ["ChunkIndex"] = chunkIndex.ToString()
        };
        return new ChunkInfo(
            Content: text.Trim(),
            ChunkIndex: chunkIndex,
            TokenCount: tokens,
            StartOffset: start,
            EndOffset: end,
            Metadata: metadata);
    }

    private List<(int Start, int End)> MergeForwardSmallChunks(
        List<(int Start, int End)> input,
        int minTokens,
        string content)
    {
        if (input.Count <= 1 || minTokens <= 0) return input;

        var output = new List<(int Start, int End)>();
        foreach ((int start, int end) span in input)
        {
            int tokens = tokenCounter.CountTokens(content.Substring(span.start, span.end - span.start));
            if (tokens >= minTokens || output.Count == 0)
            {
                output.Add(span);
            }
            else
            {
                (int prevStart, int _) = output[^1];
                output[^1] = (prevStart, span.end);
            }
        }

        if (output.Count >= 2)
        {
            int firstTokens = tokenCounter.CountTokens(content.Substring(output[0].Start, output[0].End - output[0].Start));
            if (firstTokens < minTokens)
            {
                (int firstStart, int _) = output[0];
                (int _, int nextEnd) = output[1];
                output[1] = (firstStart, nextEnd);
                output.RemoveAt(0);
            }
        }

        return output;
    }
}
