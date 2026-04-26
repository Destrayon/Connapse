using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Ingestion.Chunking;

/// <summary>
/// Recursive splitter (paragraphs → newlines → sentences → words → chars).
/// Merge loop follows LangChain's _merge_splits: overlap is preserved by popping
/// from the head of the running buffer until the next split fits, never by
/// discarding the buffer. Offsets are tracked at split granularity so chunk
/// (start, end) round-trips exactly with the source text.
/// </summary>
public class RecursiveChunker(ITokenCounter tokenCounter) : IChunkingStrategy
{
    public string Name => "Recursive";

    public Task<IReadOnlyList<ChunkInfo>> ChunkAsync(
        ParsedDocument parsedDocument,
        ChunkingSettings settings,
        CancellationToken cancellationToken = default)
    {
        var chunks = new List<ChunkInfo>();
        string content = parsedDocument.Content;

        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult<IReadOnlyList<ChunkInfo>>(chunks);

        string[] separators = settings.RecursiveSeparators is { Length: > 0 } provided
            ? provided
            : ["\n\n", "\n", ". ", " "];

        List<(string Text, int Offset)> rawChunks = SplitRecursive(
            content,
            baseOffset: 0,
            separators,
            settings.MaxChunkSize,
            settings.Overlap,
            tokenCounter,
            cancellationToken);

        List<(string Text, int Offset)> merged = MergeForwardSmallChunks(
            rawChunks,
            settings.MinChunkSize,
            tokenCounter,
            content);

        if (merged.Count == 0)
        {
            merged.Add((content, 0));
        }

        int chunkIndex = 0;
        foreach ((string text, int offset) in merged)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int tokens = tokenCounter.CountTokens(text);
            string trimmed = text.Trim();
            if (trimmed.Length == 0) continue;

            var chunkMetadata = new Dictionary<string, string>(parsedDocument.Metadata)
            {
                ["ChunkingStrategy"] = Name,
                ["ChunkIndex"] = chunkIndex.ToString()
            };

            chunks.Add(new ChunkInfo(
                Content: trimmed,
                ChunkIndex: chunkIndex,
                TokenCount: tokens,
                StartOffset: offset,
                EndOffset: offset + text.Length,
                Metadata: chunkMetadata));

            chunkIndex++;
        }

        return Task.FromResult<IReadOnlyList<ChunkInfo>>(chunks);
    }

    /// <summary>
    /// Splits <paramref name="text"/> recursively through <paramref name="separators"/>
    /// until each chunk fits in <paramref name="maxTokens"/>. Each returned tuple is
    /// (chunk text, chunk's offset within the original document).
    /// </summary>
    private static List<(string Text, int Offset)> SplitRecursive(
        string text,
        int baseOffset,
        string[] separators,
        int maxTokens,
        int overlapTokens,
        ITokenCounter counter,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var output = new List<(string Text, int Offset)>();

        if (counter.CountTokens(text) <= maxTokens)
        {
            output.Add((text, baseOffset));
            return output;
        }

        int activeIdx = -1;
        string? activeSep = null;
        for (int i = 0; i < separators.Length; i++)
        {
            if (text.Contains(separators[i], StringComparison.Ordinal))
            {
                activeIdx = i;
                activeSep = separators[i];
                break;
            }
        }

        if (activeSep is null)
        {
            int chunkChars = counter.GetIndexAtTokenCount(text, maxTokens);
            if (chunkChars <= 0) chunkChars = text.Length;
            for (int i = 0; i < text.Length; i += chunkChars)
            {
                int len = Math.Min(chunkChars, text.Length - i);
                output.Add((text.Substring(i, len), baseOffset + i));
            }
            return output;
        }

        var splits = new List<(string Text, int Offset)>();
        int searchStart = 0;
        while (true)
        {
            int sepIdx = text.IndexOf(activeSep, searchStart, StringComparison.Ordinal);
            if (sepIdx < 0)
            {
                if (searchStart < text.Length)
                    splits.Add((text.Substring(searchStart), baseOffset + searchStart));
                break;
            }
            splits.Add((text.Substring(searchStart, sepIdx - searchStart), baseOffset + searchStart));
            searchStart = sepIdx + activeSep.Length;
        }

        var current = new List<(string Text, int Offset)>();

        string Joined() => string.Join(activeSep, current.Select(c => c.Text));
        int JoinedTokens() => current.Count == 0 ? 0 : counter.CountTokens(Joined());

        void Flush()
        {
            if (current.Count == 0) return;
            output.Add((Joined(), current[0].Offset));
        }

        int TokensWith((string Text, int Offset) candidate)
        {
            if (current.Count == 0) return counter.CountTokens(candidate.Text);
            string probe = Joined() + activeSep + candidate.Text;
            return counter.CountTokens(probe);
        }

        foreach ((string Text, int Offset) split in splits)
        {
            int splitTokens = counter.CountTokens(split.Text);

            if (splitTokens > maxTokens)
            {
                if (current.Count > 0) { Flush(); current.Clear(); }
                if (activeIdx + 1 < separators.Length)
                {
                    var sub = SplitRecursive(
                        split.Text,
                        split.Offset,
                        separators[(activeIdx + 1)..],
                        maxTokens,
                        overlapTokens,
                        counter,
                        ct);
                    output.AddRange(sub);
                }
                else
                {
                    int chunkChars = counter.GetIndexAtTokenCount(split.Text, maxTokens);
                    if (chunkChars <= 0) chunkChars = split.Text.Length;
                    for (int i = 0; i < split.Text.Length; i += chunkChars)
                    {
                        int len = Math.Min(chunkChars, split.Text.Length - i);
                        output.Add((split.Text.Substring(i, len), split.Offset + i));
                    }
                }
                continue;
            }

            if (current.Count > 0 && TokensWith(split) > maxTokens)
            {
                Flush();
                while (current.Count > 0
                    && (JoinedTokens() > overlapTokens || TokensWith(split) > maxTokens))
                {
                    current.RemoveAt(0);
                }
            }

            current.Add((split.Text, split.Offset));
        }

        if (current.Count > 0) Flush();
        return output;
    }

    /// <summary>
    /// Post-pass: any chunk smaller than <paramref name="minTokens"/> is merged into
    /// the preceding chunk (or following, if it's the first). Never silently dropped.
    /// </summary>
    private static List<(string Text, int Offset)> MergeForwardSmallChunks(
        List<(string Text, int Offset)> input,
        int minTokens,
        ITokenCounter counter,
        string content)
    {
        if (input.Count <= 1 || minTokens <= 0) return input;

        var output = new List<(string Text, int Offset)>();
        foreach ((string Text, int Offset) c in input)
        {
            int tokens = counter.CountTokens(c.Text);
            if (tokens >= minTokens || output.Count == 0)
            {
                output.Add((c.Text, c.Offset));
            }
            else
            {
                (string _, int prevOffset) = output[^1];
                int smallEnd = c.Offset + c.Text.Length;
                string mergedText = content.Substring(prevOffset, smallEnd - prevOffset);
                output[^1] = (mergedText, prevOffset);
            }
        }

        if (output.Count >= 2)
        {
            int firstTokens = counter.CountTokens(output[0].Text);
            if (firstTokens < minTokens)
            {
                (string _, int firstOffset) = output[0];
                (string _, int nextOffset) = output[1];
                int nextEnd = nextOffset + output[1].Text.Length;
                string mergedText = content.Substring(firstOffset, nextEnd - firstOffset);
                output[1] = (mergedText, firstOffset);
                output.RemoveAt(0);
            }
        }

        return output;
    }
}
