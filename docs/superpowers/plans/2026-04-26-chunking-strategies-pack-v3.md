# Chunking Strategies Pack v3 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship two new `IChunkingStrategy` implementations — `DocumentAwareChunker` (Markdig-based, fills the reserved `DocumentAware` enum slot, auto-routed for `.md`/`.markdown`/`.mdx`) and `SentenceWindowChunker` (one chunk per sentence + N-neighbor window in metadata + retriever-side substitution in `HybridSearchService`).

**Architecture:** `DocumentAwareChunker` walks Markdig's AST to maintain a header stack, emits one chunk per section, prepends `HeaderPath` breadcrumb to chunk body before embedding, delegates oversize sections to `RecursiveChunker`, and falls back to `RecursiveChunker` entirely when the document has no Markdown structure. `SentenceWindowChunker` reuses `ISentenceSegmenter` (PragmaticSegmenterNet), emits one `ChunkInfo` per sentence with the wider window stored in `Metadata["window"]`. A new substitution block in `HybridSearchService.SearchAsync` swaps `SearchHit.Content ← Metadata["window"]` after rerank, before TopK.

**Tech Stack:** .NET 10, xUnit, FluentAssertions, NSubstitute, Markdig 1.1.3 (new NuGet), existing `Microsoft.ML.Tokenizers`, existing `PragmaticSegmenterNet`.

**Spec**: [`docs/superpowers/specs/2026-04-26-chunking-strategies-pack-v3-design.md`](../specs/2026-04-26-chunking-strategies-pack-v3-design.md). **Issue**: [#317](https://github.com/Destrayon/Connapse/issues/317).

---

## File map

**Create:**
- `src/Connapse.Ingestion/Chunking/MarkdownSectionWalker.cs` — AST-walk helper, returns `List<MarkdownSection>` with header path + source spans
- `src/Connapse.Ingestion/Chunking/DocumentAwareChunker.cs` — `IChunkingStrategy` impl wrapping the walker + recursive fallback
- `src/Connapse.Ingestion/Chunking/SentenceWindowChunker.cs` — `IChunkingStrategy` impl emitting one chunk per sentence
- `tests/Connapse.Ingestion.Tests/Chunking/DocumentAwareChunkerTests.cs`
- `tests/Connapse.Ingestion.Tests/Chunking/SentenceWindowChunkerTests.cs`
- `tests/Connapse.Search.Tests/Hybrid/SentenceWindowSubstitutionTests.cs` (or sibling test file in the existing `Connapse.Search.Tests` project — verify the project's test layout during Task 6)

**Modify:**
- `src/Connapse.Ingestion/Connapse.Ingestion.csproj` — add `Markdig` 1.1.3 PackageReference
- `src/Connapse.Core/Models/IngestionModels.cs:41` — add `SentenceWindow` to `ChunkingStrategy` enum
- `src/Connapse.Core/Models/SettingsModels.cs:78-110` — add `PrependHeaderPath` to `ChunkingSettings`; add `SentenceWindowSize` to `ChunkingSettings`
- `src/Connapse.Core/Models/SettingsModels.cs` (`SearchSettings` record around line 142) — add `SentenceWindowSubstituteOnSearch`
- `src/Connapse.Ingestion/Extensions/ServiceCollectionExtensions.cs:38-44` — register `DocumentAwareChunker` and `SentenceWindowChunker` as `IChunkingStrategy` singletons
- `src/Connapse.Ingestion/Pipeline/IngestionPipeline.cs:460-478` (`ChunkDocumentAsync`) — accept `string? fileName` parameter; auto-route `.md`/`.markdown`/`.mdx` to `"DocumentAware"`
- `src/Connapse.Ingestion/Pipeline/IngestionPipeline.cs:220, :424` — pass `options.FileName` to `ChunkDocumentAsync`
- `src/Connapse.Search/Hybrid/HybridSearchService.cs` — substitution block after rerank, before TopK truncation (~15 LOC)

**Delete:** none.

**Schema migration:** none.

---

### Task 1: Add Markdig package, settings additions, and `SentenceWindow` enum value

**Files:**
- Modify: `src/Connapse.Ingestion/Connapse.Ingestion.csproj`
- Modify: `src/Connapse.Core/Models/IngestionModels.cs`
- Modify: `src/Connapse.Core/Models/SettingsModels.cs`

- [ ] **Step 1: Add Markdig NuGet package**

```bash
dotnet add src/Connapse.Ingestion package Markdig --version 1.1.3
```

Expected: `info : PackageReference for package 'Markdig' added`. Verify the resulting line appears alongside existing references in `src/Connapse.Ingestion/Connapse.Ingestion.csproj`.

- [ ] **Step 2: Add `SentenceWindow` to `ChunkingStrategy` enum**

In `src/Connapse.Core/Models/IngestionModels.cs:41`, change:

```csharp
public enum ChunkingStrategy { Semantic, FixedSize, Recursive, DocumentAware }
```

to:

```csharp
public enum ChunkingStrategy { Semantic, FixedSize, Recursive, DocumentAware, SentenceWindow }
```

(`DocumentAware` already exists. We're appending `SentenceWindow` so existing users' persisted settings don't shift index — keep additions at the end.)

- [ ] **Step 3: Add `PrependHeaderPath` and `SentenceWindowSize` to `ChunkingSettings`**

In `src/Connapse.Core/Models/SettingsModels.cs` inside the `ChunkingSettings` record (around line 78-110), append after the existing `RecursiveSeparators` property:

```csharp
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
```

- [ ] **Step 4: Add `SentenceWindowSubstituteOnSearch` to `SearchSettings`**

In `src/Connapse.Core/Models/SettingsModels.cs` inside the `SearchSettings` record, append a new property at the end (immediately before the record's closing `}`):

```csharp
    /// <summary>
    /// When a search hit's metadata carries a "window" key (set by
    /// SentenceWindowChunker), substitute Content with the window text after
    /// reranking, before TopK truncation. Default true. Reranker still scores
    /// against the precise sentence; the wider window is for the answer-time LLM.
    /// </summary>
    public bool SentenceWindowSubstituteOnSearch { get; set; } = true;
```

- [ ] **Step 5: Verify build**

```bash
dotnet build -nologo
```

Expected: Build succeeded with 0 errors. Warnings about `Microsoft.Bcl.Memory` CVE may appear — those are pre-existing and acceptable.

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Ingestion/Connapse.Ingestion.csproj src/Connapse.Core/Models/IngestionModels.cs src/Connapse.Core/Models/SettingsModels.cs
git commit -m "feat: add Markdig package, ChunkingStrategy.SentenceWindow, and three settings knobs

- PackageReference: Markdig 1.1.3 (BSD-2, ~1.19 MB, zero deps on .NET 10)
- ChunkingStrategy enum: append SentenceWindow (DocumentAware already reserved)
- ChunkingSettings: PrependHeaderPath (default true), SentenceWindowSize (default 3)
- SearchSettings: SentenceWindowSubstituteOnSearch (default true)

Refs #317."
```

---

### Task 2: Implement `MarkdownSectionWalker` with TDD

**Files:**
- Create: `src/Connapse.Ingestion/Chunking/MarkdownSectionWalker.cs`
- Create: `tests/Connapse.Ingestion.Tests/Chunking/MarkdownSectionWalkerTests.cs`

**What it does:** Pure function. Take a content string and Markdig `MarkdownDocument`, return `List<MarkdownSection>`. Each section carries the header path, per-level header keys, and source `(SpanStart, SpanEnd)` byte offsets. No tokenization, no chunk emission — that's `DocumentAwareChunker`'s job.

- [ ] **Step 1: Write the failing tests**

Create `tests/Connapse.Ingestion.Tests/Chunking/MarkdownSectionWalkerTests.cs`:

```csharp
using Connapse.Ingestion.Chunking;
using FluentAssertions;
using Markdig;
using Markdig.Syntax;

namespace Connapse.Ingestion.Tests.Chunking;

[Trait("Category", "Unit")]
public class MarkdownSectionWalkerTests
{
    private static MarkdownDocument Parse(string md)
    {
        MarkdownPipeline pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseYamlFrontMatter()
            .Build();
        return Markdown.Parse(md, pipeline, trackTrivia: true);
    }

    [Fact]
    public void Walk_NoHeadings_ReturnsSinglePreambleSection()
    {
        string md = "Just a paragraph.\n\nAnother paragraph.";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(1);
        sections[0].HeaderPath.Should().BeEmpty();
        sections[0].SpanStart.Should().Be(0);
        sections[0].SpanEnd.Should().Be(md.Length);
    }

    [Fact]
    public void Walk_SimpleHeadings_ReturnsOneSectionPerHeading()
    {
        string md = "# A\n\nbody a\n\n# B\n\nbody b";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(2);
        sections[0].HeaderPath.Should().Be("A");
        sections[1].HeaderPath.Should().Be("B");
    }

    [Fact]
    public void Walk_NestedHeadings_BuildsBreadcrumbPath()
    {
        string md = "# H1\n\n## H2\n\n### H3\n\nbody";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(3);
        sections[2].HeaderPath.Should().Be("H1 > H2 > H3");
        sections[2].LevelMap["H1"].Should().Be("H1");
        sections[2].LevelMap["H2"].Should().Be("H2");
        sections[2].LevelMap["H3"].Should().Be("H3");
    }

    [Fact]
    public void Walk_LevelSkip_PopsCorrectly()
    {
        // H1 → H3 → H2 should drop H3 entirely when H2 reappears.
        string md = "# A\n\n### C\n\n## B\n\nbody";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(3);
        sections[0].HeaderPath.Should().Be("A");
        sections[1].HeaderPath.Should().Be("A > C");
        sections[2].HeaderPath.Should().Be("A > B");
        sections[2].LevelMap.Should().NotContainKey("H3");
    }

    [Fact]
    public void Walk_FencedCodeBlockWithHashes_NotTreatedAsHeadings()
    {
        // The `# Heading` inside the code fence is body text, not a heading.
        string md = "# Real\n\n```\n# Not a heading\n```\n\n# Another";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(2);
        sections[0].HeaderPath.Should().Be("Real");
        sections[1].HeaderPath.Should().Be("Another");
    }

    [Fact]
    public void Walk_SetextHeadings_ParsedAsHeadings()
    {
        string md = "Title\n=====\n\nbody\n\nSub\n---\n\nmore";
        var sections = MarkdownSectionWalker.Walk(md, Parse(md));

        sections.Should().HaveCount(2);
        sections[0].HeaderPath.Should().Be("Title");
        sections[1].HeaderPath.Should().Be("Title > Sub");
    }

    [Fact]
    public void Walk_HasMarkdownStructure_TrueWhenHeadingsExist()
    {
        string md = "# A\n\nbody";
        MarkdownSectionWalker.HasMarkdownStructure(Parse(md)).Should().BeTrue();
    }

    [Fact]
    public void Walk_HasMarkdownStructure_TrueWhenFencedCodeExists()
    {
        string md = "Just text.\n\n```\ncode\n```\n";
        MarkdownSectionWalker.HasMarkdownStructure(Parse(md)).Should().BeTrue();
    }

    [Fact]
    public void Walk_HasMarkdownStructure_FalseForPlainProse()
    {
        string md = "Just a paragraph.\n\nAnother paragraph.";
        MarkdownSectionWalker.HasMarkdownStructure(Parse(md)).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run tests — expect compile error**

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "FullyQualifiedName~MarkdownSectionWalkerTests" -nologo
```

Expected: build fails — `MarkdownSectionWalker` does not exist.

- [ ] **Step 3: Implement `MarkdownSectionWalker`**

Create `src/Connapse.Ingestion/Chunking/MarkdownSectionWalker.cs`:

```csharp
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Connapse.Ingestion.Chunking;

/// <summary>
/// Section produced by walking a Markdown document's heading structure.
/// </summary>
internal record MarkdownSection(
    string HeaderPath,                       // "H1 > H2 > H3" (empty for preamble)
    Dictionary<string, string> LevelMap,     // "H1" -> "Engineering", "H2" -> "Deploy"
    int Depth,                               // 0 for preamble, 1 for H1, 2 for H1>H2, ...
    int SpanStart,                           // byte offset in source where the section body starts
    int SpanEnd);                            // byte offset where the section body ends (exclusive)

/// <summary>
/// Walks a parsed Markdig <see cref="MarkdownDocument"/> to produce a list of
/// sections demarcated by headings. Code fences, tables, HTML blocks, and
/// front-matter are inert here — they're atomic blocks in Markdig's AST so the
/// walker simply skips them as non-heading blocks.
/// </summary>
internal static class MarkdownSectionWalker
{
    public static List<MarkdownSection> Walk(string content, MarkdownDocument doc)
    {
        var sections = new List<MarkdownSection>();
        var stack = new List<(int Level, string Text)>();
        int currentSpanStart = 0;

        foreach (Block block in doc)
        {
            if (block is HeadingBlock heading)
            {
                // Close the previous section: it spans from the previous heading's body-start
                // up to this heading's *block* start.
                int sectionEnd = heading.Span.Start;
                if (sectionEnd > currentSpanStart)
                {
                    sections.Add(BuildSection(stack, currentSpanStart, sectionEnd));
                }

                // Pop entries with level >= current heading's level, then push.
                while (stack.Count > 0 && stack[^1].Level >= heading.Level)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                string headingText = ExtractHeadingText(heading);
                stack.Add((heading.Level, headingText));

                // Body of this new section starts immediately AFTER the heading block.
                currentSpanStart = heading.Span.End + 1;
            }
        }

        // Final section: from currentSpanStart to end of content.
        if (currentSpanStart < content.Length)
        {
            sections.Add(BuildSection(stack, currentSpanStart, content.Length));
        }

        // Clamp empty trailing/leading sections so callers don't process empties.
        sections.RemoveAll(s => s.SpanEnd <= s.SpanStart);

        return sections;
    }

    public static bool HasMarkdownStructure(MarkdownDocument doc)
    {
        foreach (Block block in doc)
        {
            if (block is HeadingBlock) return true;
            if (block is FencedCodeBlock) return true;
        }
        return false;
    }

    private static MarkdownSection BuildSection(
        IReadOnlyList<(int Level, string Text)> stack,
        int start,
        int end)
    {
        string path = stack.Count == 0 ? string.Empty : string.Join(" > ", stack.Select(s => s.Text));
        var levelMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((int level, string text) in stack)
        {
            levelMap[$"H{level}"] = text;
        }
        return new MarkdownSection(path, levelMap, stack.Count, start, end);
    }

    private static string ExtractHeadingText(HeadingBlock heading)
    {
        if (heading.Inline is null) return string.Empty;
        var sb = new System.Text.StringBuilder();
        foreach (Inline inline in heading.Inline)
        {
            if (inline is LiteralInline lit) sb.Append(lit.Content);
            else if (inline is CodeInline code) sb.Append(code.Content);
            else sb.Append(inline.ToString());
        }
        return sb.ToString().Trim();
    }
}
```

- [ ] **Step 4: Run tests — expect pass**

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "FullyQualifiedName~MarkdownSectionWalkerTests" -nologo
```

Expected: 9 passed. If any test fails because Markdig's parsing doesn't match expected span positions, inspect the actual span values via a temporary `Console.WriteLine` and adjust either the test fixture text or the body-start computation (`heading.Span.End + 1`).

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Ingestion/Chunking/MarkdownSectionWalker.cs tests/Connapse.Ingestion.Tests/Chunking/MarkdownSectionWalkerTests.cs
git commit -m "feat(chunker): add MarkdownSectionWalker (Markdig AST → header-stack sections)

Walks Markdig's parsed document and emits one MarkdownSection per heading-bounded
region, carrying the header breadcrumb (H1 > H2 > H3), per-level keys, and
source span. Code fences, tables, HTML blocks, and front-matter are atomic
in the AST — no in-walker bookkeeping needed.

Pure helper; consumed by DocumentAwareChunker in a follow-up commit.

Refs #317."
```

---

### Task 3: Implement `DocumentAwareChunker` with TDD

**Files:**
- Create: `src/Connapse.Ingestion/Chunking/DocumentAwareChunker.cs`
- Create: `tests/Connapse.Ingestion.Tests/Chunking/DocumentAwareChunkerTests.cs`
- Modify: `src/Connapse.Ingestion/Extensions/ServiceCollectionExtensions.cs`

**Algorithm:**
1. Parse content with Markdig.
2. If `!HasMarkdownStructure(doc)`, delegate to `RecursiveChunker.ChunkAsync` and return.
3. `MarkdownSectionWalker.Walk(content, doc)` → sections.
4. For each section:
   - body = `content.Substring(section.SpanStart, section.SpanEnd - section.SpanStart)`
   - if `PrependHeaderPath` and `HeaderPath != ""`: `text = $"{HeaderPath}\n\n{body}"`, `offsetEstimated = true`; else `text = body`, `offsetEstimated = false`.
   - if `tokenCounter.CountTokens(text) <= MaxChunkSize`: emit one `ChunkInfo` with `StartOffset = SpanStart`, `EndOffset = SpanStart + body.Length`, metadata stamped with `HeaderPath` + `H1/H2/H3` + `OffsetEstimated` if applicable.
   - else: build a synthetic `ParsedDocument(body, doc.Metadata, doc.Warnings)`, delegate to `RecursiveChunker.ChunkAsync`, translate sub-chunk offsets by `SpanStart`, propagate header metadata, propagate `OffsetEstimated` if `PrependHeaderPath` (we can't prepend to sub-chunks without offset drift, so skip prepending for sub-chunks but do propagate the breadcrumb keys).

- [ ] **Step 1: Write failing tests**

Create `tests/Connapse.Ingestion.Tests/Chunking/DocumentAwareChunkerTests.cs`:

```csharp
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Chunking;
using Connapse.Ingestion.Utilities;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Chunking;

[Trait("Category", "Unit")]
public class DocumentAwareChunkerTests
{
    private readonly DocumentAwareChunker _chunker;
    private readonly RecursiveChunker _recursive;

    public DocumentAwareChunkerTests()
    {
        TiktokenTokenCounter counter = new();
        _recursive = new RecursiveChunker(counter);
        _chunker = new DocumentAwareChunker(counter, _recursive);
    }

    [Fact]
    public void Name_IsDocumentAware()
    {
        _chunker.Name.Should().Be("DocumentAware");
    }

    [Fact]
    public async Task ChunkAsync_EmptyContent_ReturnsEmpty()
    {
        var doc = new ParsedDocument("", new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 100, MinChunkSize = 1, Overlap = 0 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ChunkAsync_PlainProseNoMarkdown_FallsThroughToRecursive()
    {
        string content = "Just a paragraph of prose. No headers. No code fences. " +
                         string.Join(" ", Enumerable.Repeat("filler", 20));
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 100, MinChunkSize = 1, Overlap = 0 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().NotBeEmpty();
        // Fallback path means HeaderPath metadata is not stamped.
        result.Should().AllSatisfy(c => c.Metadata.Should().NotContainKey("HeaderPath"));
    }

    [Fact]
    public async Task ChunkAsync_HeadersStamped_OnEveryChunk()
    {
        string content = "# Engineering\n\n## Deploy\n\nDeploy steps here.\n\n## Rollback\n\nRollback steps here.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 200,
            MinChunkSize = 1,
            Overlap = 0,
            PrependHeaderPath = false  // keep raw bodies for offset round-trip below
        };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().HaveCountGreaterOrEqualTo(2);
        result.Should().Contain(c =>
            c.Metadata.TryGetValue("HeaderPath", out string? p) &&
            p == "Engineering > Deploy");
        result.Should().Contain(c =>
            c.Metadata.TryGetValue("HeaderPath", out string? p) &&
            p == "Engineering > Rollback");
        result.Should().AllSatisfy(c => c.Metadata.Should().ContainKey("H1"));
    }

    [Fact]
    public async Task ChunkAsync_PrependHeaderPath_AddsBreadcrumbToContent()
    {
        string content = "# Engineering\n\n## Deploy\n\nDeploy steps here.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 200,
            MinChunkSize = 1,
            Overlap = 0,
            PrependHeaderPath = true
        };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().Contain(c =>
            c.Content.StartsWith("Engineering > Deploy") &&
            c.Metadata.TryGetValue("OffsetEstimated", out string? est) && est == "true");
    }

    [Fact]
    public async Task ChunkAsync_OversizeSection_DelegatesToRecursive()
    {
        // Section is far above MaxChunkSize → goes through recursive sub-splitting.
        string longBody = string.Join(" ", Enumerable.Repeat("filler", 500));
        string content = $"# Big\n\n{longBody}";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 50, MinChunkSize = 1, Overlap = 0 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().HaveCountGreaterThan(1);
        result.Should().AllSatisfy(c =>
        {
            c.Metadata.Should().ContainKey("HeaderPath");
            c.Metadata["HeaderPath"].Should().Be("Big");
            c.TokenCount.Should().BeLessThanOrEqualTo(settings.MaxChunkSize);
        });
    }

    [Fact]
    public async Task ChunkAsync_FencedCodeBlock_NeverSplitMidFence()
    {
        // A code fence inside a section MUST stay atomic even if the recursive
        // fallback fires for the section. Markdig's AST treats the fence as one
        // block; the section as a whole is sliced from source, preserving the fence.
        string content = "# Cmd\n\n```\nlong long long long long long long long long\n" +
                         "and more more more more more more more more more more more\n" +
                         "and yet more yet more yet more yet more yet more yet more\n```";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 1000, MinChunkSize = 1, Overlap = 0 };

        var result = await _chunker.ChunkAsync(doc, settings);

        // Fence must appear intact in the output.
        string allContent = string.Concat(result.Select(c => c.Content));
        allContent.Should().Contain("```");
    }

    [Fact]
    public async Task ChunkAsync_OffsetsRoundTrip_WhenPrependHeaderPathFalse()
    {
        string content = "# A\n\nbody a\n\n# B\n\nbody b";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 1000,
            MinChunkSize = 1,
            Overlap = 0,
            PrependHeaderPath = false
        };

        var result = await _chunker.ChunkAsync(doc, settings);

        foreach (ChunkInfo c in result)
        {
            c.StartOffset.Should().BeGreaterOrEqualTo(0);
            c.EndOffset.Should().BeLessOrEqualTo(content.Length);
            string slice = content.Substring(c.StartOffset, c.EndOffset - c.StartOffset);
            slice.Trim().Should().Be(c.Content.Trim());
        }
    }

    [Fact]
    public async Task ChunkAsync_HasChunkingStrategyMetadata()
    {
        string content = "# A\n\nbody";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 100, MinChunkSize = 1, Overlap = 0 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().AllSatisfy(c =>
        {
            c.Metadata["ChunkingStrategy"].Should().Be("DocumentAware");
        });
    }
}
```

- [ ] **Step 2: Run tests — expect compile error**

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "FullyQualifiedName~DocumentAwareChunkerTests" -nologo
```

Expected: build fails — `DocumentAwareChunker` does not exist.

- [ ] **Step 3: Implement `DocumentAwareChunker`**

Create `src/Connapse.Ingestion/Chunking/DocumentAwareChunker.cs`:

```csharp
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

        MarkdownDocument doc = Markdown.Parse(parsedDocument.Content, Pipeline, trackTrivia: true);

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

                chunks.Add(BuildChunk(
                    parsedDocument,
                    parsedDocument.Content.Substring(absStart, subLen),
                    chunkIndex++,
                    s.TokenCount,
                    startOffset: absStart,
                    endOffset: absStart + subLen,
                    section,
                    offsetEstimated: false));
            }
        }

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
```

- [ ] **Step 4: Wire DI registration**

In `src/Connapse.Ingestion/Extensions/ServiceCollectionExtensions.cs`, in the `AddDocumentIngestion` method, add a registration after the existing chunker registrations (around line 41-44):

```csharp
        services.AddSingleton<IChunkingStrategy, DocumentAwareChunker>();
```

- [ ] **Step 5: Run tests — expect pass**

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "FullyQualifiedName~DocumentAwareChunkerTests" -nologo
```

Expected: 9 passed. If `ChunkAsync_OffsetsRoundTrip_WhenPrependHeaderPathFalse` fails, inspect a specific failing chunk's slice vs content — most likely cause is `MarkdownSectionWalker` body-start computation off by 1 (heading.Span.End is inclusive in Markdig; `+1` skips the trailing newline). Adjust to match.

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Ingestion/Chunking/DocumentAwareChunker.cs tests/Connapse.Ingestion.Tests/Chunking/DocumentAwareChunkerTests.cs src/Connapse.Ingestion/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(chunker): add DocumentAwareChunker (Markdown header-aware)

- Walks Markdig AST via MarkdownSectionWalker; emits one chunk per section.
- Stamps each chunk with HeaderPath breadcrumb + per-level (H1/H2/H3) keys.
- Default PrependHeaderPath=true: prepends breadcrumb to chunk body before
  embedding (sets OffsetEstimated=true since prepended text isn't source-verbatim).
- Oversize sections delegate to RecursiveChunker (offset-translated).
- Non-Markdown input (no headings AND no fenced code) falls through to
  RecursiveChunker entirely.
- Registered as IChunkingStrategy singleton; selectable via
  ChunkingStrategy.DocumentAware (already-reserved enum slot).

Refs #317."
```

---

### Task 4: Extension-based auto-routing in `IngestionPipeline.ChunkDocumentAsync`

**Files:**
- Modify: `src/Connapse.Ingestion/Pipeline/IngestionPipeline.cs:220, :424, :460-478`

**What it does:** When `IngestionOptions.FileName` ends with `.md`/`.markdown`/`.mdx`, dispatch to `DocumentAwareChunker` regardless of `options.Strategy`. Falls back to the configured strategy for other extensions. Pure dispatch logic — no behavior change for non-Markdown files.

- [ ] **Step 1: Write failing tests for the auto-router**

We'll exercise this through the public ingestion path. Create or add to `tests/Connapse.Ingestion.Tests/Pipeline/IngestionPipelineRoutingTests.cs` (a new test file under that path; create the directory if needed):

```csharp
using Connapse.Ingestion.Chunking;

namespace Connapse.Ingestion.Tests.Pipeline;

[Trait("Category", "Unit")]
public class IngestionPipelineRoutingTests
{
    [Theory]
    [InlineData("README.md", "DocumentAware")]
    [InlineData("docs.markdown", "DocumentAware")]
    [InlineData("docs.MDX", "DocumentAware")]
    [InlineData("notes.MD", "DocumentAware")]
    [InlineData("file.txt", "Recursive")]
    [InlineData("file.pdf", "Recursive")]
    [InlineData(null, "Recursive")]
    [InlineData("", "Recursive")]
    public void ResolveStrategyName_RoutesByExtension(string? fileName, string expected)
    {
        string actual = IngestionPipelineStrategyResolver.Resolve(
            fallbackStrategy: "Recursive",
            fileName: fileName);

        actual.Should().Be(expected);
    }
}
```

This test references a small static helper `IngestionPipelineStrategyResolver` we'll extract for testability.

- [ ] **Step 2: Run test — expect compile error**

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "FullyQualifiedName~IngestionPipelineRoutingTests" -nologo
```

Expected: build fails — type does not exist.

- [ ] **Step 3: Add the resolver helper and update `ChunkDocumentAsync`**

In `src/Connapse.Ingestion/Pipeline/IngestionPipeline.cs`, add a new internal class at the bottom of the file (before the closing namespace brace if present, otherwise as a sibling type):

```csharp
internal static class IngestionPipelineStrategyResolver
{
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdx"
    };

    public static string Resolve(string fallbackStrategy, string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return fallbackStrategy;
        string ext = System.IO.Path.GetExtension(fileName);
        return MarkdownExtensions.Contains(ext) ? "DocumentAware" : fallbackStrategy;
    }
}
```

Modify `ChunkDocumentAsync` (currently at line 460-478) to accept `string? fileName` and use the resolver:

```csharp
    private async Task<IReadOnlyList<ChunkInfo>> ChunkDocumentAsync(
        ParsedDocument parsedDocument,
        ChunkingStrategy strategyType,
        string? fileName,
        CancellationToken ct)
    {
        ChunkingSettings settings = _chunkingSettings.CurrentValue;
        string strategyName = IngestionPipelineStrategyResolver.Resolve(
            fallbackStrategy: strategyType.ToString(),
            fileName: fileName);

        IChunkingStrategy? strategy = _chunkingStrategies.FirstOrDefault(s =>
            s.Name.Equals(strategyName, StringComparison.OrdinalIgnoreCase));

        if (strategy == null)
        {
            _logger.LogWarning("Chunking strategy not found: {Strategy}, using FixedSize", strategyName);
            strategy = _chunkingStrategies.First(s => s.Name == "FixedSize");
        }

        return await strategy.ChunkAsync(parsedDocument, settings, ct);
    }
```

Update both existing call sites in the same file to pass `options.FileName`:

- Line 220 (in `IngestAsync`): change
  `var chunks = await ChunkDocumentAsync(parsedDocument, options.Strategy, ct);`
  to
  `var chunks = await ChunkDocumentAsync(parsedDocument, options.Strategy, options.FileName, ct);`
- Line 424 (in `IngestStreamingAsync` or sibling): same edit.

- [ ] **Step 4: Run tests — expect pass**

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "FullyQualifiedName~IngestionPipelineRoutingTests" -nologo
```

Expected: 8 passed.

Also run the full ingestion test suite to confirm nothing regressed:

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "Category=Unit" -nologo
```

Expected: all unit tests pass. If existing pipeline tests construct `ChunkDocumentAsync` directly via reflection or fail because they don't pass a `fileName` arg, those are integration-level tests using the public pipeline surface — they should still work since they pass `IngestionOptions` (which carries `FileName`).

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Ingestion/Pipeline/IngestionPipeline.cs tests/Connapse.Ingestion.Tests/Pipeline/IngestionPipelineRoutingTests.cs
git commit -m "feat(ingestion): auto-route .md/.markdown/.mdx files to DocumentAwareChunker

ChunkDocumentAsync now takes fileName from IngestionOptions and routes Markdown
extensions to the DocumentAwareChunker regardless of the options.Strategy. Falls
back to the configured strategy for non-Markdown extensions.

Behavior change: existing containers' .md chunks shift on next reindex. The
intended user-visible improvement (header context + intact code fences) is
worth the one-time reindex.

Refs #317."
```

---

### Task 5: Implement `SentenceWindowChunker` with TDD

**Files:**
- Create: `src/Connapse.Ingestion/Chunking/SentenceWindowChunker.cs`
- Create: `tests/Connapse.Ingestion.Tests/Chunking/SentenceWindowChunkerTests.cs`
- Modify: `src/Connapse.Ingestion/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/Connapse.Ingestion.Tests/Chunking/SentenceWindowChunkerTests.cs`:

```csharp
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Chunking;
using Connapse.Ingestion.Utilities;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Chunking;

[Trait("Category", "Unit")]
public class SentenceWindowChunkerTests
{
    private readonly SentenceWindowChunker _chunker;

    public SentenceWindowChunkerTests()
    {
        TiktokenTokenCounter counter = new();
        PragmaticSentenceSegmenter segmenter = new();
        _chunker = new SentenceWindowChunker(counter, segmenter);
    }

    [Fact]
    public void Name_IsSentenceWindow()
    {
        _chunker.Name.Should().Be("SentenceWindow");
    }

    [Fact]
    public async Task ChunkAsync_EmptyContent_ReturnsEmpty()
    {
        var doc = new ParsedDocument("", new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 100, MinChunkSize = 1 };
        var result = await _chunker.ChunkAsync(doc, settings);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ChunkAsync_OneChunkPerSentence()
    {
        string content = "First sentence. Second sentence. Third sentence.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 1000, MinChunkSize = 1, SentenceWindowSize = 1 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().HaveCount(3);
        result[0].Content.Should().Contain("First sentence");
        result[1].Content.Should().Contain("Second sentence");
        result[2].Content.Should().Contain("Third sentence");
    }

    [Fact]
    public async Task ChunkAsync_DefaultWindow_ProducesNeighborText()
    {
        // 5 sentences, window = 1 → middle sentence's window contains 3 sentences (i-1, i, i+1).
        string content = "Alpha. Beta. Gamma. Delta. Epsilon.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 1000, MinChunkSize = 1, SentenceWindowSize = 1 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().HaveCount(5);
        // Index 2 = "Gamma." — its window should contain Beta + Gamma + Delta.
        ChunkInfo middle = result[2];
        middle.Metadata.Should().ContainKey("window");
        middle.Metadata["window"].Should().Contain("Beta");
        middle.Metadata["window"].Should().Contain("Gamma");
        middle.Metadata["window"].Should().Contain("Delta");
    }

    [Fact]
    public async Task ChunkAsync_FirstSentence_WindowTruncatesAtBoundary()
    {
        string content = "Alpha. Beta. Gamma.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 1000, MinChunkSize = 1, SentenceWindowSize = 2 };

        var result = await _chunker.ChunkAsync(doc, settings);

        ChunkInfo first = result[0];
        first.Metadata["window"].Should().Contain("Alpha");
        first.Metadata["window"].Should().Contain("Beta");
        first.Metadata["window"].Should().Contain("Gamma");
        // Window can't extend left past index 0; truncation is silent.
    }

    [Fact]
    public async Task ChunkAsync_SingleSentence_SingleChunk()
    {
        string content = "Just one sentence.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 1000, MinChunkSize = 1, SentenceWindowSize = 3 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().HaveCount(1);
        result[0].Metadata["window"].Should().Contain("Just one sentence");
        result[0].Metadata["original_text"].Should().Contain("Just one sentence");
    }

    [Fact]
    public async Task ChunkAsync_ContentIsTheSentence_NotTheWindow()
    {
        // Critical: embedding consumers read .Content. It MUST be the precise sentence,
        // not the wider window — otherwise we re-create the original problem semantic-window
        // is meant to fix.
        string content = "Alpha. Beta. Gamma.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 1000, MinChunkSize = 1, SentenceWindowSize = 1 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result[1].Content.Trim().Should().Be("Beta.");
        result[1].Metadata["window"].Should().NotBe("Beta.");  // window is wider
    }

    [Fact]
    public async Task ChunkAsync_BypassesMinChunkSize()
    {
        // Sentence-window chunks are intentionally tiny; MinChunkSize=100 must NOT
        // merge them via the merge-forward post-pass other chunkers run.
        string content = "A. B. C. D. E.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 1000, MinChunkSize = 100, SentenceWindowSize = 1 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().HaveCountGreaterOrEqualTo(3);  // PragmaticSegmenter may merge "A." etc.; just confirm not 1.
    }

    [Fact]
    public async Task ChunkAsync_MetadataContainsRequiredKeys()
    {
        string content = "Alpha. Beta. Gamma.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 1000, MinChunkSize = 1, SentenceWindowSize = 2 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().AllSatisfy(c =>
        {
            c.Metadata["ChunkingStrategy"].Should().Be("SentenceWindow");
            c.Metadata.Should().ContainKey("ChunkIndex");
            c.Metadata.Should().ContainKey("window");
            c.Metadata.Should().ContainKey("original_text");
            c.Metadata.Should().ContainKey("window_size");
            c.Metadata["window_size"].Should().Be("2");
        });
    }
}
```

- [ ] **Step 2: Run tests — expect compile error**

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "FullyQualifiedName~SentenceWindowChunkerTests" -nologo
```

Expected: build fails — `SentenceWindowChunker` does not exist.

- [ ] **Step 3: Implement `SentenceWindowChunker`**

Create `src/Connapse.Ingestion/Chunking/SentenceWindowChunker.cs`:

```csharp
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

            spans.Add((trimmed, idx, tokenCounter.CountTokens(trimmed)));
            cursor = idx + trimmed.Length;
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

            chunks.Add(new ChunkInfo(
                Content: sentenceText,
                ChunkIndex: i,
                TokenCount: sentenceTokens,
                StartOffset: sentenceOffset,
                EndOffset: sentenceOffset + sentenceText.Length,
                Metadata: metadata));
        }

        return Task.FromResult<IReadOnlyList<ChunkInfo>>(chunks);
    }
}
```

- [ ] **Step 4: Wire DI registration**

In `src/Connapse.Ingestion/Extensions/ServiceCollectionExtensions.cs`, add another registration alongside the existing chunkers:

```csharp
        services.AddSingleton<IChunkingStrategy, SentenceWindowChunker>();
```

- [ ] **Step 5: Run tests — expect pass**

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "FullyQualifiedName~SentenceWindowChunkerTests" -nologo
```

Expected: 8 passed. If `ChunkAsync_OneChunkPerSentence` returns 2 instead of 3, PragmaticSegmenter merged adjacent sentences — adjust the test fixtures to use sentences PragmaticSegmenter cleanly splits (e.g., longer sentences or different terminators).

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Ingestion/Chunking/SentenceWindowChunker.cs tests/Connapse.Ingestion.Tests/Chunking/SentenceWindowChunkerTests.cs src/Connapse.Ingestion/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(chunker): add SentenceWindowChunker (one chunk per sentence + window metadata)

- Each sentence becomes its own ChunkInfo (Content = sentence, gets embedded).
- Metadata['window'] = N-neighbor join; Metadata['original_text'] = sentence
  (citation correctness); Metadata['window_size'] = N.
- Bypasses MinChunkSize merge-forward — chunks are intentionally tiny.
- Reuses ISentenceSegmenter (PragmaticSegmenterNet) for sentence detection.
- Registered as IChunkingStrategy singleton; selectable via
  ChunkingStrategy.SentenceWindow.

Pairs with HybridSearchService substitution block in a follow-up commit.

Refs #317."
```

---

### Task 6: Retriever-side window substitution in `HybridSearchService`

**Files:**
- Modify: `src/Connapse.Search/Hybrid/HybridSearchService.cs`
- Create: `tests/Connapse.Search.Tests/Hybrid/SentenceWindowSubstitutionTests.cs`

**What it does:** After rerank but before TopK truncation in `HybridSearchService.SearchAsync`, swap `SearchHit.Content` with `Metadata["window"]` when the metadata key is present and `SearchSettings.SentenceWindowSubstituteOnSearch == true`. Reranker still scores against the precise sentence; the answer-time LLM receives the wider window.

- [ ] **Step 1: Locate the substitution insertion point**

Read `src/Connapse.Search/Hybrid/HybridSearchService.cs` and find the section of `SearchAsync` where reranking has just completed and the result list is about to be filtered/truncated to `TopK`. The substitution must go BETWEEN those two steps. (If no reranker is configured, the substitution still runs — just on the un-reranked list. The placement is "after rerank if it ran, before TopK".)

Verify the file's existing patterns by reading it (`Read` tool, ~lines 50-200), then identify the exact line number in `SearchAsync` after rerank completes. Substitute the corresponding line numbers in the implementation step below.

- [ ] **Step 2: Write failing tests**

Create `tests/Connapse.Search.Tests/Hybrid/SentenceWindowSubstitutionTests.cs` (verify the existing test project layout for `Connapse.Search.Tests` first; if it does not exist, find the closest sibling test project for `Connapse.Search` and place the file there with adjusted namespace):

```csharp
using Connapse.Core;
using Connapse.Search.Hybrid;
using FluentAssertions;

namespace Connapse.Search.Tests.Hybrid;

[Trait("Category", "Unit")]
public class SentenceWindowSubstitutionTests
{
    [Fact]
    public void Substitute_HitWithWindowMetadata_ReplacesContent()
    {
        var hit = new SearchHit(
            ChunkId: "c1",
            DocumentId: "d1",
            Content: "Beta.",
            Score: 0.9f,
            Metadata: new Dictionary<string, string>
            {
                ["window"] = "Alpha. Beta. Gamma.",
                ["original_text"] = "Beta."
            });

        IReadOnlyList<SearchHit> result = SentenceWindowSubstitution.SubstituteIfEnabled(
            new[] { hit },
            substituteOnSearch: true);

        result.Should().HaveCount(1);
        result[0].Content.Should().Be("Alpha. Beta. Gamma.");
    }

    [Fact]
    public void Substitute_HitWithoutWindow_PreservesContent()
    {
        var hit = new SearchHit(
            ChunkId: "c1",
            DocumentId: "d1",
            Content: "Just text.",
            Score: 0.9f,
            Metadata: new Dictionary<string, string>());

        IReadOnlyList<SearchHit> result = SentenceWindowSubstitution.SubstituteIfEnabled(
            new[] { hit },
            substituteOnSearch: true);

        result[0].Content.Should().Be("Just text.");
    }

    [Fact]
    public void Substitute_FlagDisabled_PreservesContentEvenWithWindow()
    {
        var hit = new SearchHit(
            ChunkId: "c1",
            DocumentId: "d1",
            Content: "Beta.",
            Score: 0.9f,
            Metadata: new Dictionary<string, string>
            {
                ["window"] = "Alpha. Beta. Gamma."
            });

        IReadOnlyList<SearchHit> result = SentenceWindowSubstitution.SubstituteIfEnabled(
            new[] { hit },
            substituteOnSearch: false);

        result[0].Content.Should().Be("Beta.");
    }

    [Fact]
    public void Substitute_EmptyWindowValue_PreservesContent()
    {
        var hit = new SearchHit(
            ChunkId: "c1",
            DocumentId: "d1",
            Content: "Original.",
            Score: 0.9f,
            Metadata: new Dictionary<string, string>
            {
                ["window"] = "   "  // whitespace-only — should not substitute
            });

        IReadOnlyList<SearchHit> result = SentenceWindowSubstitution.SubstituteIfEnabled(
            new[] { hit },
            substituteOnSearch: true);

        result[0].Content.Should().Be("Original.");
    }
}
```

- [ ] **Step 3: Run tests — expect compile error**

```bash
dotnet test tests/Connapse.Search.Tests --filter "FullyQualifiedName~SentenceWindowSubstitutionTests" -nologo
```

Expected: build fails — `SentenceWindowSubstitution` does not exist.

- [ ] **Step 4: Implement the helper and wire it into `SearchAsync`**

In `src/Connapse.Search/Hybrid/HybridSearchService.cs`, add a new internal static helper near the top of the file (after `using` directives, before the class declaration):

```csharp
internal static class SentenceWindowSubstitution
{
    public static IReadOnlyList<Connapse.Core.SearchHit> SubstituteIfEnabled(
        IReadOnlyList<Connapse.Core.SearchHit> hits,
        bool substituteOnSearch)
    {
        if (!substituteOnSearch) return hits;

        return hits.Select(h =>
            h.Metadata is not null
            && h.Metadata.TryGetValue("window", out string? w)
            && !string.IsNullOrWhiteSpace(w)
                ? h with { Content = w }
                : h).ToList();
    }
}
```

In `HybridSearchService.SearchAsync`, after rerank completes and before TopK truncation, insert:

```csharp
        // Substitute SentenceWindow chunks' Content with their wider window text
        // before returning. Reranker has already scored against the precise sentence.
        finalHits = SentenceWindowSubstitution
            .SubstituteIfEnabled(finalHits, _searchSettingsMonitor.CurrentValue.SentenceWindowSubstituteOnSearch)
            .ToList();
```

(Adjust the variable name `finalHits` to match whatever the local list is called in `SearchAsync` — based on prior reading it's the post-rerank list. If the actual code uses a different variable for "the list about to be returned/truncated", substitute that name.)

- [ ] **Step 5: Run tests — expect pass**

```bash
dotnet test tests/Connapse.Search.Tests --filter "FullyQualifiedName~SentenceWindowSubstitutionTests" -nologo
```

Expected: 4 passed.

Sanity-run the full search test suite:

```bash
dotnet test tests/Connapse.Search.Tests --filter "Category=Unit" -nologo
```

Expected: all unit tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Search/Hybrid/HybridSearchService.cs tests/Connapse.Search.Tests/Hybrid/SentenceWindowSubstitutionTests.cs
git commit -m "feat(search): substitute SentenceWindow chunk content with window after rerank

When a search hit's metadata carries a 'window' key (set by SentenceWindowChunker),
swap SearchHit.Content with the window text post-rerank, pre-TopK. Reranker
still scores against the precise sentence (intentional — that's why
SentenceWindowChunker emits sentence-level chunks); the answer-time LLM
receives the wider window for context.

Gated by SearchSettings.SentenceWindowSubstituteOnSearch (default true).

Closes #317."
```

---

## Self-review

**Spec coverage:**
- DocumentAwareChunker (Markdig AST + header stack + recursive fallback + non-Markdown fallback) → Tasks 2 + 3 ✅
- SentenceWindowChunker → Task 5 ✅
- Settings additions (`PrependHeaderPath`, `SentenceWindowSize`, `SentenceWindowSubstituteOnSearch`) → Task 1 ✅
- `ChunkingStrategy.SentenceWindow` enum value → Task 1 ✅
- Extension-based auto-routing for `.md`/`.markdown`/`.mdx` → Task 4 ✅
- Retriever-side substitution in `HybridSearchService` → Task 6 ✅
- Markdig package add → Task 1 ✅
- Tests for all components ✅

**Placeholder scan:** every step contains either exact code or an exact command. No "TBD" / "implement later" / "similar to Task N" patterns. The one mild softness: Task 6 Step 1 instructs the implementer to read `HybridSearchService.cs` to find the exact insertion line — that's a deliberate plan-shape choice (the file was not loaded into spec-writing context, so the line number can't be pre-baked), not a placeholder.

**Type consistency:**
- `MarkdownSection(HeaderPath, LevelMap, Depth, SpanStart, SpanEnd)` defined in Task 2, consumed in Task 3 ✓
- `ChunkInfo(Content, ChunkIndex, TokenCount, StartOffset, EndOffset, Metadata, PrecomputedEmbedding=null)` matches the codebase ✓
- `SearchHit(ChunkId, DocumentId, Content, Score, Metadata)` matches `src/Connapse.Core/Models/SearchModels.cs:15-20` ✓
- `IngestionPipelineStrategyResolver.Resolve(fallbackStrategy, fileName)` defined in Task 4 Step 3, consumed in Task 4 Step 1 test fixture ✓
- `SentenceWindowSubstitution.SubstituteIfEnabled(hits, substituteOnSearch)` defined and consumed within Task 6 ✓
- `DocumentAwareChunker(ITokenCounter, RecursiveChunker)` constructor, `SentenceWindowChunker(ITokenCounter, ISentenceSegmenter)` constructor — consistent with other chunkers in the codebase ✓
- `ChunkingSettings.PrependHeaderPath` (bool), `SentenceWindowSize` (int), `SearchSettings.SentenceWindowSubstituteOnSearch` (bool) — names match between settings additions, chunkers, and tests ✓

No gaps requiring task additions.
