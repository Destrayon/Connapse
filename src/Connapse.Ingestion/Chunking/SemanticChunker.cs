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
///
/// Oversize semantic groups are sub-split via <see cref="RecursiveChunker"/> so they
/// still respect structural boundaries (paragraphs / newlines / sentences / words)
/// rather than being cleaved at arbitrary character positions.
/// </summary>
public class SemanticChunker(
    IEmbeddingProvider embeddingProvider,
    ITokenCounter tokenCounter,
    ISentenceSegmenter sentenceSegmenter,
    RecursiveChunker recursiveChunker) : IChunkingStrategy
{
    public string Name => "Semantic";

    public async Task<IReadOnlyList<ChunkInfo>> ChunkAsync(
        ParsedDocument parsedDocument,
        ChunkingSettings settings,
        CancellationToken cancellationToken = default)
    {
        var chunks = new List<ChunkInfo>();
        string content = parsedDocument.Content;

        if (string.IsNullOrWhiteSpace(content))
            return chunks;

        List<string> sentences = SplitIntoSentences(content);

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

        // Build context-aware "combined" texts for embedding: buffer_size sentences
        // before + the sentence itself + buffer_size after. The chunk is still the
        // original sentence; the embedding just sees more context. Default buffer is 1.
        int bufferSize = Math.Max(0, settings.SemanticBufferSize);
        string[] combinedTexts = new string[sentences.Count];
        for (int i = 0; i < sentences.Count; i++)
        {
            int startIdx = Math.Max(0, i - bufferSize);
            int endIdx = Math.Min(sentences.Count - 1, i + bufferSize);
            combinedTexts[i] = string.Join(" ", sentences.GetRange(startIdx, endIdx - startIdx + 1));
        }

        // Embed the context-windowed sentences in ONE batch.
        // These embeddings serve double duty: boundary detection here, and chunk
        // storage via mean-pooling (attached to each ChunkInfo.PrecomputedEmbedding).
        IReadOnlyList<float[]> embeddings = await embeddingProvider.EmbedBatchAsync(
            combinedTexts,
            cancellationToken);

        // Compute adjacent-pair *distances* (1 - cosine similarity).
        var distances = new List<float>();
        for (int i = 0; i < embeddings.Count - 1; i++)
        {
            distances.Add(1f - CosineSimilarity(embeddings[i], embeddings[i + 1]));
        }

        // Empty-distances guard: <=1 sentence (or all sentences identical) — return
        // the whole content as one chunk and bail. This avoids walking the per-segment
        // loop with a degenerate splitIndices = [0, sentences.Count].
        if (distances.Count == 0)
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
                PrecomputedEmbedding: embeddings.Count > 0 ? MeanPool(embeddings, 0, embeddings.Count) : null));
            return chunks;
        }

        // Pick threshold: pluggable method on distances when we have enough data
        // (at least 5 distances). Below that, fall back to SemanticThreshold.
        // Convert the legacy similarity-based threshold to a distance threshold so
        // users who tuned SemanticThreshold under the old similarity convention
        // don't see silently inverted behavior: split where (1 - sim) > (1 - threshold)
        // is equivalent to the legacy split where sim < threshold.
        //
        // ComputeBreakpointThreshold also returns the *array* the threshold was
        // derived from. For Percentile / StdDev / IQR this is the distance series
        // itself; for Gradient it's the gradient series — different units from the
        // distances, so the splits loop must iterate the gradient array (not
        // distances) when comparing against a gradient-derived threshold.
        double effectiveThreshold = 1.0 - settings.SemanticThreshold;
        IReadOnlyList<float> breakpointArray = distances;
        if (distances.Count >= 5)
        {
            (effectiveThreshold, breakpointArray) = ComputeBreakpointThreshold(
                distances,
                settings.SemanticBreakpointMethod,
                settings.SemanticBreakpointAmount);
        }

        // Find split points: split where the breakpoint-array value EXCEEDS threshold.
        // breakpointArray.Count == distances.Count for every method, so `i + 1` still
        // maps cleanly to a sentence boundary.
        var splitIndices = new List<int> { 0 };
        for (int i = 0; i < breakpointArray.Count; i++)
        {
            if (breakpointArray[i] > effectiveThreshold)
                splitIndices.Add(i + 1);
        }
        splitIndices.Add(sentences.Count);

        // Build raw chunks. Use a search hint when locating each chunk text in the source
        // (LangChain pattern): back the search up by a token-budget-worth of characters
        // before the previous end so we can still find chunks whose text differs slightly
        // from a verbatim source slice (e.g., trailing whitespace stripped by Trim()).
        // No in-loop MinChunkSize filter — small chunks are merged forward in a post-pass
        // so document content is never silently dropped.
        var rawChunks = new List<RawChunk>();
        int prevStart = 0;
        int prevLen = 0;

        for (int i = 0; i < splitIndices.Count - 1; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int start = splitIndices[i];
            int end = splitIndices[i + 1];

            List<string> chunkSentences = sentences.GetRange(start, end - start);
            string chunkText = string.Join(" ", chunkSentences);
            int tokenCount = tokenCounter.CountTokens(chunkText);

            int searchBackup = Math.Min(prevLen, 256);
            int hint = Math.Max(0, prevStart + prevLen - searchBackup);
            int startOffset = content.IndexOf(chunkText, hint, StringComparison.Ordinal);
            bool offsetExact = startOffset >= 0;
            if (!offsetExact)
            {
                // Clamp so Offset + chunkText.Length never exceeds content.Length;
                // EndOffset still round-trips into a valid range even on fallback.
                startOffset = Math.Min(hint, Math.Max(0, content.Length - chunkText.Length));
            }

            if (tokenCount > settings.MaxChunkSize)
            {
                // Oversize semantic group: delegate to RecursiveChunker for hierarchical
                // sub-splitting (paragraphs → newlines → sentences → words). Sub-chunks
                // don't have a clean sentence-embedding mapping, so PrecomputedEmbedding
                // is left null and the pipeline embeds them normally.
                var syntheticDoc = new ParsedDocument(
                    chunkText,
                    parsedDocument.Metadata,
                    parsedDocument.Warnings);

                IReadOnlyList<ChunkInfo> sub = await recursiveChunker.ChunkAsync(
                    syntheticDoc,
                    settings,
                    cancellationToken);

                foreach (ChunkInfo s in sub)
                {
                    // Guard the source slice: when the IndexOf hint fallback fires
                    // (line ~163) startOffset can land near content.Length, and
                    // startOffset + s.StartOffset + subLen may exceed the buffer.
                    int subLen = s.EndOffset - s.StartOffset;
                    int absStart = startOffset + s.StartOffset;
                    if (absStart < 0 || absStart >= content.Length) continue;
                    subLen = Math.Min(subLen, content.Length - absStart);
                    if (subLen <= 0) continue;
                    rawChunks.Add(new RawChunk(
                        Text: content.Substring(absStart, subLen),
                        Offset: absStart,
                        Tokens: s.TokenCount,
                        Embedding: null,
                        OffsetEstimated: !offsetExact));
                }
            }
            else
            {
                // Mean-pool the sentence embeddings for this chunk's sentences.
                // This gives a good approximate chunk embedding without an extra API call.
                float[] precomputed = MeanPool(embeddings, start, end);
                rawChunks.Add(new RawChunk(
                    Text: chunkText,
                    Offset: startOffset,
                    Tokens: tokenCount,
                    Embedding: precomputed,
                    OffsetEstimated: !offsetExact));
            }

            prevStart = startOffset;
            prevLen = chunkText.Length;
        }

        // Post-pass: merge any chunk below MinChunkSize into the preceding chunk
        // (or following, if it's the first). Re-slices the merged span from the
        // source so any whitespace/separator between merged chunks is preserved.
        List<RawChunk> merged = MergeForwardSmallChunks(
            rawChunks,
            settings.MinChunkSize,
            tokenCounter,
            content);

        // Safety net: if everything was filtered/dropped (shouldn't happen with merge-forward,
        // but guards against pathological inputs), return the whole content as one chunk
        // with the mean of all sentence embeddings.
        if (merged.Count == 0)
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
            return chunks;
        }

        int chunkIndex = 0;
        foreach (RawChunk c in merged)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string trimmed = c.Text.Trim();
            if (trimmed.Length == 0) continue;

            var metadata = new Dictionary<string, string>(parsedDocument.Metadata)
            {
                ["ChunkingStrategy"] = Name,
                ["ChunkIndex"] = chunkIndex.ToString()
            };
            if (c.OffsetEstimated)
                metadata["OffsetEstimated"] = "true";

            chunks.Add(new ChunkInfo(
                Content: trimmed,
                ChunkIndex: chunkIndex,
                TokenCount: c.Tokens,
                StartOffset: c.Offset,
                EndOffset: c.Offset + c.Text.Length,
                Metadata: metadata,
                PrecomputedEmbedding: c.Embedding));

            chunkIndex++;
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
        int count = end - start;
        int dims = embeddings[start].Length;
        float[] result = new float[dims];

        for (int i = start; i < end; i++)
        {
            float[] emb = embeddings[i];
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

    /// <summary>
    /// Computes the adaptive breakpoint threshold over <paramref name="distances"/>
    /// using the configured <paramref name="method"/>. Mirrors LangChain
    /// SemanticChunker's _calculate_breakpoint_threshold.
    ///
    /// Returns both the threshold AND the array the threshold was derived from
    /// (and which the splits loop must iterate). For Percentile / StdDev / IQR
    /// this is the distance series itself; for Gradient it's the gradient series
    /// — comparing distances against a gradient-derived threshold mixes units and
    /// produces pathological over-segmentation on smooth distance series.
    /// </summary>
    private static (double Threshold, IReadOnlyList<float> BreakpointArray) ComputeBreakpointThreshold(
        IReadOnlyList<float> distances,
        string method,
        double amount)
    {
        if (distances.Count == 0) return (0, distances);

        switch (method?.Trim() ?? "Percentile")
        {
            case "StandardDeviation":
            {
                double mean = 0;
                foreach (float d in distances) mean += d;
                mean /= distances.Count;
                double sumSq = 0;
                foreach (float d in distances) sumSq += (d - mean) * (d - mean);
                double std = Math.Sqrt(sumSq / distances.Count);
                return (mean + amount * std, distances);
            }
            case "InterQuartile":
            {
                float[] sorted = distances.OrderBy(d => d).ToArray();
                double mean = 0;
                foreach (float d in sorted) mean += d;
                mean /= sorted.Length;
                double q1 = Percentile(sorted, 25);
                double q3 = Percentile(sorted, 75);
                double iqr = q3 - q1;
                return (mean + amount * iqr, distances);
            }
            case "Gradient":
            {
                // Forward-difference gradient over the distance series, then take
                // the Nth percentile of the gradient — splits where distance is
                // changing fastest, not where it's highest. The splits loop iterates
                // the gradient series (returned here) rather than distances, since
                // the threshold is in gradient units.
                if (distances.Count < 2) return (0, distances);
                float[] grad = new float[distances.Count];
                grad[0] = distances[1] - distances[0];
                grad[distances.Count - 1] = distances[distances.Count - 1] - distances[distances.Count - 2];
                for (int i = 1; i < distances.Count - 1; i++)
                    grad[i] = (distances[i + 1] - distances[i - 1]) / 2f;
                double threshold = Percentile(grad.OrderBy(g => g).ToArray(), amount);
                return (threshold, grad);
            }
            case "Percentile":
            default:
            {
                float[] sorted = distances.OrderBy(d => d).ToArray();
                return (Percentile(sorted, amount), distances);
            }
        }
    }

    private static double Percentile(float[] sortedAscending, double percentile)
    {
        if (sortedAscending.Length == 0) return 0;
        if (percentile <= 0) return sortedAscending[0];
        if (percentile >= 100) return sortedAscending[^1];
        double rank = percentile / 100d * (sortedAscending.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sortedAscending[lower];
        double frac = rank - lower;
        return sortedAscending[lower] * (1 - frac) + sortedAscending[upper] * frac;
    }

    /// <summary>
    /// Post-pass: any chunk smaller than <paramref name="minTokens"/> is merged into
    /// the preceding chunk (or following, if it's the first). Never silently dropped.
    /// Re-slices the merged span from <paramref name="content"/> so any whitespace or
    /// separator chars between the merged chunks are preserved (so offsets still
    /// round-trip with the source). Discards any precomputed embedding from the
    /// participating chunks since the merged span no longer corresponds to a clean
    /// sentence-embedding mapping — the pipeline will re-embed.
    /// </summary>
    private static List<RawChunk> MergeForwardSmallChunks(
        List<RawChunk> input,
        int minTokens,
        ITokenCounter counter,
        string content)
    {
        if (input.Count <= 1 || minTokens <= 0) return input;

        var output = new List<RawChunk>();
        foreach (RawChunk c in input)
        {
            if (c.Tokens >= minTokens || output.Count == 0)
            {
                output.Add(c);
            }
            else
            {
                RawChunk prev = output[^1];
                int smallEnd = c.Offset + c.Text.Length;
                int sliceLen = smallEnd - prev.Offset;
                bool sliceValid = prev.Offset >= 0
                    && sliceLen > 0
                    && prev.Offset + sliceLen <= content.Length;
                string mergedText = sliceValid
                    ? content.Substring(prev.Offset, sliceLen)
                    : prev.Text + " " + c.Text;
                int mergedTokens = counter.CountTokens(mergedText);
                bool mergedEstimated = prev.OffsetEstimated || c.OffsetEstimated || !sliceValid;
                output[^1] = new RawChunk(mergedText, prev.Offset, mergedTokens, null, mergedEstimated);
            }
        }

        if (output.Count >= 2)
        {
            if (output[0].Tokens < minTokens)
            {
                RawChunk first = output[0];
                RawChunk next = output[1];
                int nextEnd = next.Offset + next.Text.Length;
                int sliceLen = nextEnd - first.Offset;
                bool sliceValid = first.Offset >= 0
                    && sliceLen > 0
                    && first.Offset + sliceLen <= content.Length;
                string mergedText = sliceValid
                    ? content.Substring(first.Offset, sliceLen)
                    : first.Text + " " + next.Text;
                int mergedTokens = counter.CountTokens(mergedText);
                bool mergedEstimated = first.OffsetEstimated || next.OffsetEstimated || !sliceValid;
                output[1] = new RawChunk(mergedText, first.Offset, mergedTokens, null, mergedEstimated);
                output.RemoveAt(0);
            }
        }

        return output;
    }

    private record RawChunk(string Text, int Offset, int Tokens, float[]? Embedding, bool OffsetEstimated = false);
}
