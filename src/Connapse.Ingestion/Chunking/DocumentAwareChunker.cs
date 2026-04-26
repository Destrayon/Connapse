using Connapse.Core;
using Connapse.Core.Interfaces;
using Markdig;
using Markdig.Syntax;

namespace Connapse.Ingestion.Chunking;

/// <summary>
/// Markdown / structure-aware chunker. Walks Markdig's AST to find heading
/// boundaries; emits one chunk per section. Falls back to RecursiveChunker
/// when the document has no Markdown structure (no headings AND no fenced
/// code blocks) and for sections that exceed MaxChunkSize.
/// </summary>
public class DocumentAwareChunker(ITokenCounter tokenCounter, RecursiveChunker recursiveChunker) : IChunkingStrategy
{
    public string Name => "DocumentAware";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .Build();

    public async Task<IReadOnlyList<ChunkInfo>> ChunkAsync(
        ParsedDocument parsedDocument,
        ChunkingSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(parsedDocument.Content))
            return Array.Empty<ChunkInfo>();

        // We deliberately don't enable trackTrivia: Markdig 1.1.3 populates Block.Span
        // (start/end source offsets) for all parses regardless, and that's all the
        // walker uses. Trivia tracking would only matter if we needed inline-level
        // whitespace preservation, which we don't.
        MarkdownDocument doc = Markdown.Parse(parsedDocument.Content, Pipeline);

        // Non-Markdown content → delegate the entire document to RecursiveChunker.
        if (!MarkdownSectionWalker.HasMarkdownStructure(doc))
        {
            return await recursiveChunker.ChunkAsync(parsedDocument, settings, cancellationToken);
        }

        List<MarkdownSection> sections = MarkdownSectionWalker.Walk(parsedDocument.Content, doc);
        var chunks = new List<ChunkInfo>();
        int chunkIndex = 0;

        foreach (MarkdownSection section in sections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string body = parsedDocument.Content.Substring(
                section.SpanStart,
                section.SpanEnd - section.SpanStart);

            if (string.IsNullOrWhiteSpace(body)) continue;

            string text = settings.PrependHeaderPath && section.HeaderPath.Length > 0
                ? $"{section.HeaderPath}\n\n{body}"
                : body;
            int tokens = tokenCounter.CountTokens(text);

            if (tokens <= settings.MaxChunkSize)
            {
                chunks.Add(BuildChunk(
                    parsedDocument,
                    text,
                    chunkIndex++,
                    tokens,
                    startOffset: section.SpanStart,
                    endOffset: section.SpanStart + body.Length,
                    section,
                    offsetEstimated: settings.PrependHeaderPath && section.HeaderPath.Length > 0));
                continue;
            }

            // Oversize section: delegate body (without prepend) to RecursiveChunker.
            // Translate sub-chunk offsets back to outer-content coordinates.
            var syntheticDoc = new ParsedDocument(body, parsedDocument.Metadata, parsedDocument.Warnings);
            IReadOnlyList<ChunkInfo> sub = await recursiveChunker.ChunkAsync(
                syntheticDoc, settings, cancellationToken);

            foreach (ChunkInfo s in sub)
            {
                int subLen = s.EndOffset - s.StartOffset;
                int absStart = section.SpanStart + s.StartOffset;
                if (absStart < 0 || absStart >= parsedDocument.Content.Length) continue;
                subLen = Math.Min(subLen, parsedDocument.Content.Length - absStart);
                if (subLen <= 0) continue;

                // Use the recursive chunker's already-trimmed Content — its TokenCount
                // is computed on the untrimmed merged text and can sit one token over
                // budget when the slice begins with separator whitespace (e.g. "\n\n").
                // Re-counting on the trimmed body keeps each emitted chunk within budget.
                string trimmed = s.Content;

                // Symmetry with the direct-emit path above: when PrependHeaderPath is set,
                // every chunk emitted from this section carries the breadcrumb. Oversize
                // sections benefit MOST from the breadcrumb (each piece needs hierarchical
                // context), so skipping the prepend here was an unintended asymmetry.
                bool prepend = settings.PrependHeaderPath && section.HeaderPath.Length > 0;
                string subText = prepend
                    ? $"{section.HeaderPath}\n\n{trimmed}"
                    : trimmed;
                int subTokens = tokenCounter.CountTokens(subText);

                chunks.Add(BuildChunk(
                    parsedDocument,
                    subText,
                    chunkIndex++,
                    subTokens,
                    startOffset: absStart,
                    endOffset: absStart + subLen,
                    section,
                    offsetEstimated: prepend));
            }
        }

        // No MergeForwardSmallChunks post-pass: section bodies are at semantic (heading)
        // boundaries. Merging tiny sub-min sections would glue across heading lines,
        // defeating the breadcrumb metadata. SentenceWindowChunker bypasses for the same
        // reason at finer granularity (intentionally tiny chunks).
        return chunks;
    }

    private ChunkInfo BuildChunk(
        ParsedDocument parsedDocument,
        string text,
        int chunkIndex,
        int tokenCount,
        int startOffset,
        int endOffset,
        MarkdownSection section,
        bool offsetEstimated)
    {
        var metadata = new Dictionary<string, string>(parsedDocument.Metadata)
        {
            ["ChunkingStrategy"] = Name,
            ["ChunkIndex"] = chunkIndex.ToString()
        };
        if (section.HeaderPath.Length > 0)
        {
            metadata["HeaderPath"] = section.HeaderPath;
            metadata["HeaderDepth"] = section.Depth.ToString();
            foreach ((string key, string value) in section.LevelMap)
            {
                metadata[key] = value;
            }
        }
        if (offsetEstimated)
            metadata["OffsetEstimated"] = "true";

        return new ChunkInfo(
            Content: text.Trim(),
            ChunkIndex: chunkIndex,
            TokenCount: tokenCount,
            StartOffset: startOffset,
            EndOffset: endOffset,
            Metadata: metadata);
    }
}
