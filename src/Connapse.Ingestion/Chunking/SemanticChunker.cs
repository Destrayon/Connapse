using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Utilities;

namespace Connapse.Ingestion.Chunking;

/// <summary>
/// Splits text based on semantic boundaries detected through embedding similarity.
/// Groups sentences that are semantically similar together.
/// </summary>
public class SemanticChunker : IChunkingStrategy
{
    private readonly IEmbeddingProvider _embeddingProvider;

    public string Name => "Semantic";

    public SemanticChunker(IEmbeddingProvider embeddingProvider)
    {
        _embeddingProvider = embeddingProvider;
    }

    public async Task<IReadOnlyList<ChunkInfo>> ChunkAsync(
        ParsedDocument parsedDocument,
        ChunkingSettings settings,
        CancellationToken cancellationToken = default)
    {
        var chunks = new List<ChunkInfo>();
        var content = parsedDocument.Content;

        if (string.IsNullOrWhiteSpace(content))
        {
            return chunks;
        }

        // Split content into sentences
        var sentences = SplitIntoSentences(content);

        if (sentences.Count == 0)
        {
            return chunks;
        }

        // If only one sentence, return it as a single chunk
        if (sentences.Count == 1)
        {
            var tokenCount = TokenCounter.EstimateTokenCount(sentences[0]);
            chunks.Add(new ChunkInfo(
                Content: sentences[0].Trim(),
                ChunkIndex: 0,
                TokenCount: tokenCount,
                StartOffset: 0,
                EndOffset: sentences[0].Length,
                Metadata: new Dictionary<string, string>(parsedDocument.Metadata)
                {
                    ["ChunkingStrategy"] = Name,
                    ["ChunkIndex"] = "0"
                }));
            return chunks;
        }

        // Get embeddings for all sentences
        var embeddings = await _embeddingProvider.EmbedBatchAsync(
            sentences.ToArray(),
            cancellationToken);

        // Calculate similarity between adjacent sentences
        var similarities = new List<float>();
        for (int i = 0; i < embeddings.Count - 1; i++)
        {
            var similarity = CosineSimilarity(embeddings[i], embeddings[i + 1]);
            similarities.Add(similarity);
        }

        // Find split points where similarity drops below threshold
        var splitIndices = new List<int> { 0 };
        for (int i = 0; i < similarities.Count; i++)
        {
            if (similarities[i] < settings.SemanticThreshold)
            {
                splitIndices.Add(i + 1);
            }
        }
        splitIndices.Add(sentences.Count);

        // Create chunks from split points
        int chunkIndex = 0;
        int currentOffset = 0;

        for (int i = 0; i < splitIndices.Count - 1; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var start = splitIndices[i];
            var end = splitIndices[i + 1];

            // Combine sentences for this chunk
            var chunkSentences = sentences.GetRange(start, end - start);
            var chunkText = string.Join(" ", chunkSentences);

            var tokenCount = TokenCounter.EstimateTokenCount(chunkText);

            // Enforce max chunk size - if chunk is too large, split it further
            if (tokenCount > settings.MaxChunkSize)
            {
                var subChunks = SplitLargeChunk(chunkText, settings.MaxChunkSize, settings.MinChunkSize);
                foreach (var subChunk in subChunks)
                {
                    var subTokenCount = TokenCounter.EstimateTokenCount(subChunk);
                    if (subTokenCount >= settings.MinChunkSize)
                    {
                        int startOffset = content.IndexOf(subChunk, currentOffset, StringComparison.Ordinal);
                        if (startOffset == -1) startOffset = currentOffset;

                        chunks.Add(new ChunkInfo(
                            Content: subChunk.Trim(),
                            ChunkIndex: chunkIndex++,
                            TokenCount: subTokenCount,
                            StartOffset: startOffset,
                            EndOffset: startOffset + subChunk.Length,
                            Metadata: new Dictionary<string, string>(parsedDocument.Metadata)
                            {
                                ["ChunkingStrategy"] = Name,
                                ["ChunkIndex"] = chunkIndex.ToString()
                            }));

                        currentOffset = startOffset + subChunk.Length;
                    }
                }
            }
            else if (tokenCount >= settings.MinChunkSize)
            {
                // Find position in original content
                int startOffset = content.IndexOf(chunkText, currentOffset, StringComparison.Ordinal);
                if (startOffset == -1) startOffset = currentOffset;

                chunks.Add(new ChunkInfo(
                    Content: chunkText.Trim(),
                    ChunkIndex: chunkIndex++,
                    TokenCount: tokenCount,
                    StartOffset: startOffset,
                    EndOffset: startOffset + chunkText.Length,
                    Metadata: new Dictionary<string, string>(parsedDocument.Metadata)
                    {
                        ["ChunkingStrategy"] = Name,
                        ["ChunkIndex"] = chunkIndex.ToString()
                    }));

                currentOffset = startOffset + chunkText.Length;
            }
        }

        return chunks;
    }

    /// <summary>
    /// Splits text into sentences using common sentence delimiters.
    /// </summary>
    private static List<string> SplitIntoSentences(string text)
    {
        var sentences = new List<string>();

        // Split on common sentence endings
        var splits = text.Split(new[] { ". ", ".\n", "! ", "!\n", "? ", "?\n" },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var split in splits)
        {
            var trimmed = split.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                sentences.Add(trimmed);
            }
        }

        return sentences;
    }

    /// <summary>
    /// Calculates cosine similarity between two embedding vectors.
    /// </summary>
    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have the same length");

        float dotProduct = 0;
        float magnitudeA = 0;
        float magnitudeB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            magnitudeA += a[i] * a[i];
            magnitudeB += b[i] * b[i];
        }

        magnitudeA = MathF.Sqrt(magnitudeA);
        magnitudeB = MathF.Sqrt(magnitudeB);

        if (magnitudeA == 0 || magnitudeB == 0)
            return 0;

        return dotProduct / (magnitudeA * magnitudeB);
    }

    /// <summary>
    /// Splits a chunk that exceeds max size into smaller chunks.
    /// </summary>
    private static List<string> SplitLargeChunk(string text, int maxTokens, int minTokens)
    {
        var result = new List<string>();
        int chunkSize = TokenCounter.GetCharacterPositionForTokens(text, maxTokens);

        for (int i = 0; i < text.Length; i += chunkSize)
        {
            int length = Math.Min(chunkSize, text.Length - i);
            var chunk = text.Substring(i, length);

            if (TokenCounter.EstimateTokenCount(chunk) >= minTokens)
            {
                result.Add(chunk);
            }
        }

        return result;
    }
}
