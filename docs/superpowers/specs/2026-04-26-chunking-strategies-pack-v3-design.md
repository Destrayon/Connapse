# Chunking Strategies Pack v3 — Design

**Issue**: [#317](https://github.com/Destrayon/Connapse/issues/317)
**Branch**: `feature/317-chunking-strategies-pack-v3`
**Status**: Design

## Goal

Ship two new chunking strategies that close clear gaps left by the v0.4 chunker overhaul (PR #312):

1. **`DocumentAwareChunker`** — fills the reserved `ChunkingStrategy.DocumentAware` enum slot. Markdig-based AST walk with header-stack metadata, code-fence atomicity, and recursive fallback for oversize sections. Auto-dispatched for `.md`/`.markdown`/`.mdx` files regardless of the container's configured `Strategy`.
2. **`SentenceWindowChunker`** — opt-in chunker that emits one chunk per sentence, with N-neighbor window text in metadata. Paired with a substitution block in `HybridSearchService.SearchAsync` so the reranker scores precise sentences while the answer-time LLM receives wider context.

Both ship in one PR. Anthropic Contextual Retrieval (the third deferred item from #313) is filed separately as #316.

## Why this scope

- Q1-Q2 2026 industry landscape research: no managed competitor that exposes knobs to users (Bedrock, Azure AI Search, Pinecone, Cohere, Mongo) ships native Markdown-aware or sentence-window primitives. Microsoft Kernel Memory's `MarkDownChunker` is fence-aware fixed-size — does not track header path. Connapse would be the first .NET RAG with these shipped natively.
- Independent benchmarks: Snowflake finance RAG (Feb 2026, SEC filings) reports +5-6 pts context recall for header-aware vs recursive. Premai 2026 corroborates 5-10 pt range. Sentence-window has thinner backing (ARAGOG 2024, qualitative win on precision; LlamaIndex's +10-20 P@5 is vendor-blog territory).
- Connapse-specific impact mapping: MarkdownChunker is high-lift on org docs / runbooks / API docs; SentenceWindowChunker is large-lift on sales playbooks but small on code-heavy content. The two cover non-overlapping sweet spots.

## Architecture

### MarkdownChunker (`DocumentAwareChunker`)

```
ParsedDocument
    │
    ▼
Markdig.Markdown.Parse(content, pipeline, trackTrivia: true)
    │   pipeline = MarkdownPipelineBuilder
    │       .UseAdvancedExtensions()      // tables, footnotes, autolinks
    │       .UseYamlFrontMatter()          // skip YAML front-matter
    │       .Build();
    ▼
MarkdownDocument (AST)
    │
    ▼
Walk top-level blocks, maintain Stack<(int level, string text)>
    │   On HeadingBlock: pop ≥ current level, push new, emit previous section
    │   On FencedCodeBlock / Table / HtmlBlock: atomic, never split
    ▼
List<Section { headerPath, levelMap, spanStart, spanEnd }>
    │
    ▼
For each section:
    body = content.Substring(spanStart, spanEnd - spanStart)
    if PrependHeaderPath && headerPath.Length > 0:
        toEmbed = $"{headerPath}\n\n{body}"
    if tokenCounter.CountTokens(toEmbed) <= MaxChunkSize:
        emit ChunkInfo(toEmbed, ..., metadata = {HeaderPath, H1, H2, H3, ...})
    else:
        delegate body to RecursiveChunker; propagate header path to sub-chunks
    │
    ▼
MergeForwardSmallChunks post-pass (existing helper)
    │
    ▼
IReadOnlyList<ChunkInfo>
```

**Non-Markdown fallback**: before walking, check `doc.Descendants<HeadingBlock>().Count() == 0 && doc.Descendants<FencedCodeBlock>().Count() == 0`. If true, delegate the whole document to `RecursiveChunker` and return its output. Catches PDFs/DOCX that came through `PdfParser`/`OfficeParser` (which strip Markdown structure) and any plain text accidentally routed here.

### SentenceWindowChunker

```
ParsedDocument
    │
    ▼
ISentenceSegmenter.Split(content)         // PragmaticSegmenterNet
    │
    ▼
For each sentence at index i:
    lo = max(0, i - SentenceWindowSize)
    hi = min(N, i + SentenceWindowSize + 1)
    window = string.Join(" ", sentences[lo..hi])
    emit ChunkInfo(
        Content = sentence,                // embedded as-is
        StartOffset / EndOffset = sentence span in source,
        Metadata = {
            "ChunkingStrategy" = "SentenceWindow",
            "ChunkIndex" = i,
            "window" = window,
            "original_text" = sentence,
            "window_size" = N
        })
    │
    ▼  // No MergeForwardSmallChunks — chunks are intentionally tiny.
IReadOnlyList<ChunkInfo>
```

### Retriever-side substitution

In `HybridSearchService.SearchAsync`, after rerank but before TopK truncation:

```csharp
if (searchSettings.SentenceWindowSubstituteOnSearch)
{
    finalHits = finalHits.Select(h =>
        h.Metadata.TryGetValue("window", out var w) && !string.IsNullOrWhiteSpace(w)
            ? h with { Content = w }
            : h).ToList();
}
```

Reranker scores against the precise sentence (intentional — that's the whole point of sentence-window). The substitution happens AFTER rerank, so the answer-time LLM receives the wider window. `original_text` stays in metadata for citation correctness.

### Extension-based auto-routing

In `IngestionPipeline.ChunkDocumentAsync` (or sibling), before resolving the strategy from `ChunkingSettings.Strategy`, check the document's source extension:

```csharp
private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
{
    ".md", ".markdown", ".mdx"
};

string strategyName = MarkdownExtensions.Contains(Path.GetExtension(parsedDocument.SourcePath ?? ""))
    ? "DocumentAware"
    : settings.Strategy;
```

(The actual `parsedDocument` field name for source path — `Source`, `Filename`, etc. — will be confirmed during plan-writing. The mechanism is the contribution; the field name is implementation detail.)

This is a documented behavior change for existing containers — Markdown chunks will shift on next reindex.

## Settings additions

```csharp
// ChunkingSettings (in Connapse.Core.Models.SettingsModels)
/// <summary>
/// For DocumentAware (Markdown) chunking: prepend the header breadcrumb
/// (e.g., "Engineering > Deploy > Rollback") to the chunk body before
/// embedding. Default true — biggest single retrieval-quality win at
/// ~10-30 token tax per chunk. Sets Metadata["OffsetEstimated"]="true"
/// since prepended text isn't source-verbatim.
/// </summary>
public bool PrependHeaderPath { get; set; } = true;

/// <summary>
/// For SentenceWindow chunking: number of sentences on each side of the
/// indexed sentence to include in Metadata["window"] (total window = 2N+1).
/// Default 3 matches LlamaIndex's SentenceWindowNodeParser default.
/// </summary>
public int SentenceWindowSize { get; set; } = 3;

// SearchSettings (same file)
/// <summary>
/// When a search hit's metadata carries a "window" key (set by
/// SentenceWindowChunker), substitute Content with the window text after
/// reranking, before TopK truncation. Default true. Reranker still scores
/// against the precise sentence; the wider window is for the answer-time LLM.
/// </summary>
public bool SentenceWindowSubstituteOnSearch { get; set; } = true;
```

## File map

**Create:**
- `src/Connapse.Ingestion/Chunking/DocumentAwareChunker.cs` (~280 LOC)
- `src/Connapse.Ingestion/Chunking/MarkdownSectionWalker.cs` (~120 LOC) — separable for unit-testing the AST walk in isolation
- `src/Connapse.Ingestion/Chunking/SentenceWindowChunker.cs` (~120 LOC)
- `tests/Connapse.Ingestion.Tests/Chunking/DocumentAwareChunkerTests.cs` (~250 LOC, 10 tests)
- `tests/Connapse.Ingestion.Tests/Chunking/SentenceWindowChunkerTests.cs` (~200 LOC, 8 tests)
- `tests/Connapse.Search.Tests/Hybrid/SentenceWindowSubstitutionTests.cs` (~80 LOC, 3 tests) — new test project file or sibling depending on existing test layout

**Modify:**
- `src/Connapse.Ingestion/Connapse.Ingestion.csproj` — add `<PackageReference Include="Markdig" Version="1.1.3" />`
- `src/Connapse.Ingestion/Extensions/ServiceCollectionExtensions.cs` — register `DocumentAwareChunker` and `SentenceWindowChunker` as `IChunkingStrategy` singletons
- `src/Connapse.Core/Models/SettingsModels.cs` — `PrependHeaderPath`, `SentenceWindowSize`, `SentenceWindowSubstituteOnSearch`
- `src/Connapse.Search/Hybrid/HybridSearchService.cs` — substitution block (~15 LOC)
- `src/Connapse.Ingestion/Pipeline/IngestionPipeline.cs` — extension-based auto-routing (~10 LOC, location confirmed during plan-writing)

**No DB migration.** `chunks.metadata` is already JSONB.

## Behavior changes

1. **Existing containers' `.md`/`.markdown`/`.mdx` chunks shift on next reindex** — the auto-router overrides `Strategy` for these extensions. This is the intended user-visible improvement (broken code fences and missing header context get fixed) but it IS a behavior change. PR description must call this out.
2. **`PrependHeaderPath = true` default** changes Markdown chunk content shape — the breadcrumb prefix is in the chunk body. Round-trip with source no longer holds for these chunks; `Metadata["OffsetEstimated"] = "true"` flags the case (same convention `SentenceAwareFixedSizeChunker` already uses for the `IndexOf`-fallback case).
3. **Sentence-window chunks bypass `MinChunkSize` merge-forward**. Documented in `MinChunkSize` XML doc — already weakened to "depends on strategy" in PR #312, this just adds another exception.

## Out of scope (separate follow-ups)

- Razor settings-UI fields for the three new knobs — backend-first PR; UI follows in a small doc-PR.
- **Anthropic Contextual Retrieval** — tracked in [#316](https://github.com/Destrayon/Connapse/issues/316). Biggest single retrieval-quality lever (~35-67% recall@20). Deserves its own focused PR.
- **Custom non-`#` headers** in MarkdownChunker (LangChain's `custom_header_patterns` knob).
- **Per-row table sub-splitting** in oversize tables. Markdig keeps the `Table` block atomic; if it exceeds `MaxChunkSize`, recursive fallback hacks it on `\n`. Real fix needs row-aware sub-splitting.
- **Window deduplication** when adjacent SentenceWindow hits have overlapping windows. LlamaIndex production gotcha; mitigation defer.
- **Markdown-emitting PDF/DOCX parsers** so structure survives parsing. Different workstream entirely.
- **HTML chunker**. Defer until an HTML connector exists.
- **Late chunking** (Jina). Gated on per-token embeddings; no current provider exposes them.

## Tests

### `DocumentAwareChunkerTests` (~10 tests)

- `Name_IsDocumentAware`
- `ChunkAsync_HeaderStack_PropagatesAcrossH1H2H3`
- `ChunkAsync_HeaderStack_HandlesH1ToH3SkipCorrectly`
- `ChunkAsync_FencedCodeBlock_NeverSplitMidFence` (` ``` ` and `~~~`)
- `ChunkAsync_SetextHeaders_ParsedAsHeadings` (`====` and `----`)
- `ChunkAsync_YamlFrontMatter_StrippedFromOutput`
- `ChunkAsync_TablesAndHtmlBlocks_StayAtomic`
- `ChunkAsync_OversizeSection_DelegatesToRecursive_HeaderPathPropagated`
- `ChunkAsync_NonMarkdownContent_FallsThroughToRecursive`
- `ChunkAsync_OffsetsRoundTrip_WhenPrependHeaderPathIsFalse`

### `SentenceWindowChunkerTests` (~8 tests)

- `Name_IsSentenceWindow`
- `ChunkAsync_EmittsOneChunkPerSentence`
- `ChunkAsync_DefaultWindowSize3_ProducesExpectedNeighborText`
- `ChunkAsync_FirstSentence_WindowTruncatesAtBoundary`
- `ChunkAsync_LastSentence_WindowTruncatesAtBoundary`
- `ChunkAsync_SingleSentenceDocument_ReturnsOneChunk`
- `ChunkAsync_EmbedsTheSentence_NotTheWindow` (verify via mock counter / embed)
- `ChunkAsync_BypassesMinChunkSizeMerge`

### `SentenceWindowSubstitutionTests` (~3 tests)

- `SearchAsync_HitWithWindowMetadata_ContentReplacedWithWindow`
- `SearchAsync_HitWithoutWindowMetadata_ContentUnchanged`
- `SearchAsync_SubstituteOnSearchFalse_ContentUnchanged`

## Dependencies / risks

- **Markdig** is a single new NuGet, BSD-2-licensed, ~1.19 MB nupkg, zero direct deps on .NET 10. Active maintenance (1.1.3 released 2026-04-20). Risk: low.
- **Existing tests** may need recalibration if `DocumentAwareChunker` becomes the default for `.md` test fixtures used elsewhere. Likely scope: a couple of test inputs in `Connapse.Ingestion.Tests`. Same pattern of recalibration we've done before in PR #312.
- **Reranker interaction** is documented but untested — sentence-window's marginal value drops with `CrossEncoderReranker` enabled. No code change needed; just user-facing documentation.

## Effort estimate

| Component | LOC |
|---|---|
| `DocumentAwareChunker` + `MarkdownSectionWalker` | ~400 |
| `SentenceWindowChunker` | ~120 |
| Tests (chunkers + retriever) | ~530 |
| Pipeline auto-routing + DI + settings + search-side substitution | ~50 |
| **Total** | **~1100 LOC** |

Comparable to PR #312 (~2500 LOC including the plan and CodeRabbit fixes); fits one PR cleanly.

## Approval gates

- [ ] User approves design (this document)
- [ ] Transition to `superpowers:writing-plans` skill to produce step-by-step implementation plan
- [ ] Implementation via subagent-driven-development per the same workflow that delivered PR #312
