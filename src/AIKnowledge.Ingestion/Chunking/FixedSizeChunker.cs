using AIKnowledge.Core;
using AIKnowledge.Core.Interfaces;
using AIKnowledge.Ingestion.Utilities;

namespace AIKnowledge.Ingestion.Chunking;

/// <summary>
/// Splits text into fixed-size chunks based on token count with configurable overlap.
/// </summary>
public class FixedSizeChunker : IChunkingStrategy
{
    public string Name => "FixedSize";

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

        var maxChunkSize = settings.MaxChunkSize;
        var overlap = settings.Overlap;

        // Ensure overlap is not larger than chunk size
        if (overlap >= maxChunkSize)
        {
            overlap = maxChunkSize / 4; // Default to 25% overlap
        }

        int currentPosition = 0;
        int chunkIndex = 0;

        while (currentPosition < content.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Find the end position for this chunk (targeting maxChunkSize tokens)
            int targetChars = TokenCounter.GetCharacterPositionForTokens(
                content[currentPosition..],
                maxChunkSize);

            int endPosition = currentPosition + targetChars;

            // Don't exceed content length
            if (endPosition > content.Length)
            {
                endPosition = content.Length;
            }
            else
            {
                // Try to break at a natural boundary (newline, period, space)
                endPosition = FindNaturalBreakpoint(content, currentPosition, endPosition);
            }

            // Extract the chunk
            var chunkText = content[currentPosition..endPosition];
            var tokenCount = TokenCounter.EstimateTokenCount(chunkText);

            // Only create chunk if it meets minimum size or is the last chunk
            if (tokenCount >= settings.MinChunkSize || endPosition >= content.Length)
            {
                var chunkMetadata = new Dictionary<string, string>(parsedDocument.Metadata)
                {
                    ["ChunkingStrategy"] = Name,
                    ["ChunkIndex"] = chunkIndex.ToString()
                };

                chunks.Add(new ChunkInfo(
                    Content: chunkText.Trim(),
                    ChunkIndex: chunkIndex,
                    TokenCount: tokenCount,
                    StartOffset: currentPosition,
                    EndOffset: endPosition,
                    Metadata: chunkMetadata));

                chunkIndex++;
            }

            // Move to next chunk, accounting for overlap
            if (endPosition >= content.Length)
            {
                break;
            }

            // Calculate overlap position
            int overlapChars = TokenCounter.GetCharacterPositionForTokens(chunkText, overlap);
            currentPosition = endPosition - overlapChars;

            // Ensure we're making progress
            if (currentPosition <= endPosition - targetChars)
            {
                currentPosition = endPosition;
            }
        }

        return Task.FromResult<IReadOnlyList<ChunkInfo>>(chunks);
    }

    /// <summary>
    /// Finds a natural breakpoint (newline, period, space) near the target position.
    /// </summary>
    private static int FindNaturalBreakpoint(string content, int start, int target)
    {
        // Look for natural boundaries within a reasonable window
        int searchWindow = Math.Min(100, (target - start) / 4);

        // First, try to find a double newline (paragraph break)
        for (int i = target; i > target - searchWindow && i > start; i--)
        {
            if (i > 0 && content[i] == '\n' && content[i - 1] == '\n')
            {
                return i;
            }
        }

        // Next, try to find a single newline
        for (int i = target; i > target - searchWindow && i > start; i--)
        {
            if (content[i] == '\n')
            {
                return i;
            }
        }

        // Next, try to find a sentence boundary (period followed by space)
        for (int i = target; i > target - searchWindow && i > start; i--)
        {
            if (content[i] == '.' && i + 1 < content.Length && char.IsWhiteSpace(content[i + 1]))
            {
                return i + 1;
            }
        }

        // Finally, try to find any whitespace
        for (int i = target; i > target - searchWindow && i > start; i--)
        {
            if (char.IsWhiteSpace(content[i]))
            {
                return i;
            }
        }

        // If no natural boundary found, use the target position
        return target;
    }
}
