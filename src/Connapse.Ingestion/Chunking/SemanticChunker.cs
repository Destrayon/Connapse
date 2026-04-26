using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Ingestion.Chunking;

/// <summary>
/// Splits text based on semantic boundaries detected through embedding similarity.
/// Groups sentences that are semantically similar together.
///
/// As a by-product of computing sentence embeddings for boundary detection, this chunker
/// also attaches a mean-pooled embedding to each produced ChunkInfo. IngestionPipeline
/// detects this and skips the second embedding pass, halving the number of Ollama calls.
/// </summary>
public class SemanticChunker(
    IEmbeddingProvider embeddingProvider,
    ITokenCounter tokenCounter,
    ISentenceSegmenter sentenceSegmenter) : IChunkingStrategy
{
    public string Name => "Semantic";

    public async Task<IReadOnlyList<ChunkInfo>> ChunkAsync(
        ParsedDocument parsedDocument,
        ChunkingSettings settings,
        CancellationToken cancellationToken = default)
    {
        var chunks = new List<ChunkInfo>();
        var content = parsedDocument.Content;

        if (string.IsNullOrWhiteSpace(content))
            return chunks;

        var sentences = SplitIntoSentences(content);

        if (sentences.Count == 0)
            return chunks;

        if (sentences.Count == 1)
        {
            // No embeddings needed to make a boundary decision for a single sentence.
            // Leave PrecomputedEmbedding null — pipeline will embed it normally.
            int tokenCount = tokenCounter.CountTokens(sentences[0]);
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

        // Embed all sentences in ONE batch request.
        // These embeddings serve double duty: boundary detection here, and chunk storage
        // via mean-pooling (attached to each ChunkInfo.PrecomputedEmbedding).
        var embeddings = await embeddingProvider.EmbedBatchAsync(
            sentences.ToArray(),
            cancellationToken);

        // Calculate similarity between adjacent sentences
        var similarities = new List<float>();
        for (int i = 0; i < embeddings.Count - 1; i++)
        {
            similarities.Add(CosineSimilarity(embeddings[i], embeddings[i + 1]));
        }

        // Use the 80th-percentile of all pairwise distances as the adaptive split threshold.
        // This outperforms a fixed constant because the threshold adjusts to the document's
        // own semantic density rather than a hard-coded value.
        var effectiveThreshold = settings.SemanticThreshold;
        if (similarities.Count >= 5)
        {
            var sortedDistances = similarities.OrderBy(s => s).ToList();
            var p80Index = (int)Math.Floor(0.80 * sortedDistances.Count);
            effectiveThreshold = sortedDistances[Math.Min(p80Index, sortedDistances.Count - 1)];
        }

        // Find split points where similarity drops below threshold
        var splitIndices = new List<int> { 0 };
        for (int i = 0; i < similarities.Count; i++)
        {
            if (similarities[i] < effectiveThreshold)
                splitIndices.Add(i + 1);
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

            var chunkSentences = sentences.GetRange(start, end - start);
            var chunkText = string.Join(" ", chunkSentences);
            int tokenCount = tokenCounter.CountTokens(chunkText);

            if (tokenCount > settings.MaxChunkSize)
            {
                // Over-size: split further. Sub-chunks don't have a clean sentence-embedding
                // mapping, so PrecomputedEmbedding is left null and pipeline embeds them normally.
                var subChunks = SplitLargeChunk(chunkText, settings.MaxChunkSize, settings.MinChunkSize, tokenCounter);
                foreach (var subChunk in subChunks)
                {
                    int subTokenCount = tokenCounter.CountTokens(subChunk);
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
                int startOffset = content.IndexOf(chunkText, currentOffset, StringComparison.Ordinal);
                if (startOffset == -1) startOffset = currentOffset;

                // Mean-pool the sentence embeddings for this chunk's sentences.
                // This gives a good approximate chunk embedding without an extra API call.
                var precomputed = MeanPool(embeddings, start, end);

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
                    },
                    PrecomputedEmbedding: precomputed));

                currentOffset = startOffset + chunkText.Length;
            }
        }

        // Safety net: if every segment was filtered out (all too small), return the whole
        // content as one chunk with the mean of all sentence embeddings.
        if (chunks.Count == 0)
        {
            int tc = tokenCounter.CountTokens(content);
            chunks.Add(new ChunkInfo(
                Content: content.Trim(),
                ChunkIndex: 0,
                TokenCount: tc,
                StartOffset: 0,
                EndOffset: content.Length,
                Metadata: new Dictionary<string, string>(parsedDocument.Metadata)
                {
                    ["ChunkingStrategy"] = Name,
                    ["ChunkIndex"] = "0"
                },
                PrecomputedEmbedding: MeanPool(embeddings, 0, embeddings.Count)));
        }

        return chunks;
    }

    /// <summary>
    /// Computes the mean (average) of embedding vectors for sentences at indices [start, end).
    /// Mean-pooling is the standard way sentence-transformer embeddings are combined into a
    /// longer passage embedding and avoids a second round-trip to the embedding service.
    /// </summary>
    private static float[] MeanPool(IReadOnlyList<float[]> embeddings, int start, int end)
    {
        var count = end - start;
        var dims = embeddings[start].Length;
        var result = new float[dims];

        for (int i = start; i < end; i++)
        {
            var emb = embeddings[i];
            for (int d = 0; d < dims; d++)
                result[d] += emb[d];
        }

        for (int d = 0; d < dims; d++)
            result[d] /= count;

        return result;
    }

    private List<string> SplitIntoSentences(string text)
    {
        IReadOnlyList<string> raw = sentenceSegmenter.Split(text);
        var sentences = new List<string>(raw.Count);
        foreach (string s in raw)
        {
            string trimmed = s.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                sentences.Add(trimmed);
        }
        return sentences;
    }

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

    private static List<string> SplitLargeChunk(string text, int maxTokens, int minTokens, ITokenCounter counter)
    {
        var result = new List<string>();
        int chunkSize = counter.GetIndexAtTokenCount(text, maxTokens);
        // Defensive: tiktoken can return 0 when text is shorter than maxTokens; treat as a single full-length chunk.
        if (chunkSize <= 0) chunkSize = text.Length;

        for (int i = 0; i < text.Length; i += chunkSize)
        {
            int length = Math.Min(chunkSize, text.Length - i);
            string chunk = text.Substring(i, length);

            if (counter.CountTokens(chunk) >= minTokens)
                result.Add(chunk);
        }

        return result;
    }
}
