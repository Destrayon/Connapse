using AIKnowledge.Core;
using AIKnowledge.Core.Interfaces;
using AIKnowledge.Ingestion.Utilities;

namespace AIKnowledge.Ingestion.Chunking;

/// <summary>
/// Splits text recursively using hierarchical separators (paragraphs → sentences → words).
/// Preserves document structure better than fixed-size chunking.
/// </summary>
public class RecursiveChunker : IChunkingStrategy
{
    public string Name => "Recursive";

    public Task<IReadOnlyList<ChunkInfo>> ChunkAsync(
        ParsedDocument parsedDocument,
        ChunkingSettings settings,
        CancellationToken cancellationToken = default)
    {
        var chunks = new List<ChunkInfo>();
        var content = parsedDocument.Content;

        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult<IReadOnlyList<ChunkInfo>>(chunks);
        }

        var separators = settings.RecursiveSeparators;
        if (separators == null || separators.Length == 0)
        {
            separators = ["\n\n", "\n", ". ", " "];
        }

        // Split content recursively
        var textChunks = SplitTextRecursive(
            content,
            separators,
            settings.MaxChunkSize,
            settings.Overlap);

        int chunkIndex = 0;
        int currentOffset = 0;

        foreach (var chunkText in textChunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tokenCount = TokenCounter.EstimateTokenCount(chunkText);

            // Only create chunk if it meets minimum size
            if (tokenCount >= settings.MinChunkSize)
            {
                // Find actual position in original content
                int startOffset = content.IndexOf(chunkText, currentOffset, StringComparison.Ordinal);
                if (startOffset == -1) startOffset = currentOffset;

                int endOffset = startOffset + chunkText.Length;

                var chunkMetadata = new Dictionary<string, string>(parsedDocument.Metadata)
                {
                    ["ChunkingStrategy"] = Name,
                    ["ChunkIndex"] = chunkIndex.ToString()
                };

                chunks.Add(new ChunkInfo(
                    Content: chunkText.Trim(),
                    ChunkIndex: chunkIndex,
                    TokenCount: tokenCount,
                    StartOffset: startOffset,
                    EndOffset: endOffset,
                    Metadata: chunkMetadata));

                chunkIndex++;
                currentOffset = endOffset;
            }
        }

        return Task.FromResult<IReadOnlyList<ChunkInfo>>(chunks);
    }

    /// <summary>
    /// Recursively splits text using different separators, trying to keep chunks under maxTokens.
    /// </summary>
    private static List<string> SplitTextRecursive(
        string text,
        string[] separators,
        int maxTokens,
        int overlapTokens)
    {
        var result = new List<string>();

        // Base case: text is small enough
        var tokenCount = TokenCounter.EstimateTokenCount(text);
        if (tokenCount <= maxTokens)
        {
            result.Add(text);
            return result;
        }

        // Try each separator in order
        foreach (var separator in separators)
        {
            if (text.Contains(separator))
            {
                var splits = text.Split(new[] { separator }, StringSplitOptions.None);

                // Rebuild chunks, combining splits until we hit maxTokens
                var currentChunk = new System.Text.StringBuilder();

                foreach (var split in splits)
                {
                    var testChunk = currentChunk.Length == 0
                        ? split
                        : currentChunk.ToString() + separator + split;

                    if (TokenCounter.EstimateTokenCount(testChunk) <= maxTokens)
                    {
                        currentChunk.Clear();
                        currentChunk.Append(testChunk);
                    }
                    else
                    {
                        // Current chunk is full, save it
                        if (currentChunk.Length > 0)
                        {
                            var chunkText = currentChunk.ToString();
                            result.Add(chunkText);

                            // Start new chunk with overlap from previous
                            currentChunk.Clear();
                            if (overlapTokens > 0 && !string.IsNullOrEmpty(chunkText))
                            {
                                var overlapText = GetOverlapText(chunkText, overlapTokens);
                                currentChunk.Append(overlapText);
                                if (currentChunk.Length > 0 && !string.IsNullOrWhiteSpace(split))
                                {
                                    currentChunk.Append(separator);
                                }
                            }
                        }

                        // Add current split
                        currentChunk.Append(split);

                        // If even a single split is too large, we need to recurse with next separator
                        if (TokenCounter.EstimateTokenCount(currentChunk.ToString()) > maxTokens && split.Length > 0)
                        {
                            var subsplits = SplitTextRecursive(split, separators[(Array.IndexOf(separators, separator) + 1)..], maxTokens, overlapTokens);
                            foreach (var subsplit in subsplits)
                            {
                                result.Add(subsplit);
                            }
                            currentChunk.Clear();
                        }
                    }
                }

                // Add any remaining text
                if (currentChunk.Length > 0)
                {
                    result.Add(currentChunk.ToString());
                }

                return result;
            }
        }

        // No separator found, split by character count as fallback
        int chunkSize = TokenCounter.GetCharacterPositionForTokens(text, maxTokens);
        for (int i = 0; i < text.Length; i += chunkSize)
        {
            int length = Math.Min(chunkSize, text.Length - i);
            result.Add(text.Substring(i, length));
        }

        return result;
    }

    /// <summary>
    /// Extracts the last N tokens from text for overlap.
    /// </summary>
    private static string GetOverlapText(string text, int overlapTokens)
    {
        if (string.IsNullOrEmpty(text) || overlapTokens <= 0)
            return string.Empty;

        // Estimate characters needed for overlap tokens
        int overlapChars = TokenCounter.GetCharacterPositionForTokens(text, overlapTokens);

        if (overlapChars >= text.Length)
            return text;

        // Take the last overlapChars characters
        return text.Substring(text.Length - overlapChars);
    }
}
