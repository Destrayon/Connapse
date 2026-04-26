# RecursiveChunker Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix four confirmed defects in `RecursiveChunker` — silent overlap discard, silent content loss below `MinChunkSize`, `StartOffset/EndOffset` drift when overlap is applied, and the chars-times-0.25 token-count heuristic — by adopting LangChain's `_merge_splits` pattern, swapping in `Microsoft.ML.Tokenizers.TiktokenTokenizer` behind a new `ITokenCounter` abstraction, reworking offset tracking, and converting `MinChunkSize` from "silent discard" into "merge-forward".

**Architecture:** Introduce `ITokenCounter` (`Connapse.Core.Interfaces`) registered as singleton; default implementation `TiktokenTokenCounter` uses `cl100k_base`. All three chunkers (`FixedSize`, `Recursive`, `Semantic`) take `ITokenCounter` via constructor injection and the legacy static `Connapse.Ingestion.Utilities.TokenCounter` is deleted. The `RecursiveChunker` merge loop is rewritten to treat the running buffer as `List<(text, offset, tokens)>`; overlap is preserved by popping from the head until either total ≤ overlap-tokens or there's room for the next split. Oversized splits trigger sub-recursion that emits its own merged sub-chunks directly to the output (matching LangChain's pattern — overlap is not carried across recursion boundaries, which is the documented trade-off). Offset tracking is moved into the splitter itself: each split carries its `(text, sourceOffset)` and a chunk's `StartOffset` is the first member's offset, `EndOffset` is `StartOffset + chunkText.Length`. Round-trip with the source is exact because we always join with the same separator we split on.

**Tech Stack:** .NET 10, xUnit, FluentAssertions, NSubstitute, `Microsoft.ML.Tokenizers` (ML.NET 4.0+).

**Out of scope (separate tickets):**
- `SemanticChunker` has the same `IndexOf` offset bug at `SemanticChunker.cs:120-141` — not fixed here. This plan only does the mechanical `ITokenCounter` migration in that file.
- Anthropic Contextual Retrieval, Jina late chunking, layout-aware upstream — roadmap items, not blockers.

---

## File map

**Create:**
- `src/Connapse.Core/Interfaces/ITokenCounter.cs`
- `src/Connapse.Ingestion/Utilities/TiktokenTokenCounter.cs`
- `tests/Connapse.Ingestion.Tests/Utilities/TiktokenTokenCounterTests.cs`

**Modify:**
- `src/Connapse.Ingestion/Connapse.Ingestion.csproj` — add `Microsoft.ML.Tokenizers`
- `src/Connapse.Ingestion/Extensions/ServiceCollectionExtensions.cs` — register `ITokenCounter`
- `src/Connapse.Ingestion/Chunking/RecursiveChunker.cs` — full rewrite
- `src/Connapse.Ingestion/Chunking/FixedSizeChunker.cs` — constructor injection of `ITokenCounter`
- `src/Connapse.Ingestion/Chunking/SemanticChunker.cs` — constructor injection of `ITokenCounter` (mechanical)
- `tests/Connapse.Ingestion.Tests/Chunking/RecursiveChunkerTests.cs` — add 3 regression tests; instantiate with counter
- `tests/Connapse.Ingestion.Tests/Chunking/FixedSizeChunkerTests.cs` — instantiate with counter
- `tests/Connapse.Ingestion.Tests/Chunking/SemanticChunkerTests.cs` — instantiate with counter

**Delete (after all migrations land):**
- `src/Connapse.Ingestion/Utilities/TokenCounter.cs`

---

### Task 1: Add `Microsoft.ML.Tokenizers` package and define `ITokenCounter`

**Files:**
- Modify: `src/Connapse.Ingestion/Connapse.Ingestion.csproj`
- Create: `src/Connapse.Core/Interfaces/ITokenCounter.cs`

- [ ] **Step 1: Add NuGet package**

```bash
dotnet add src/Connapse.Ingestion package Microsoft.ML.Tokenizers
```

Expected: `info : PackageReference for package 'Microsoft.ML.Tokenizers' added`. Verify the resulting `<PackageReference>` line appears alongside the existing references in `Connapse.Ingestion.csproj`.

- [ ] **Step 2: Verify build still succeeds**

```bash
dotnet build src/Connapse.Ingestion -nologo
```

Expected: `Build succeeded.` with 0 errors, 0 warnings.

- [ ] **Step 3: Create the interface**

Create `src/Connapse.Core/Interfaces/ITokenCounter.cs`:

```csharp
namespace Connapse.Core.Interfaces;

/// <summary>
/// Counts tokens using a real tokenizer (BPE/tiktoken). Replaces the legacy
/// chars-times-0.25 heuristic in <c>Connapse.Ingestion.Utilities.TokenCounter</c>.
/// </summary>
public interface ITokenCounter
{
    int CountTokens(string text);

    /// <summary>
    /// Returns the character index in <paramref name="text"/> at which approximately
    /// <paramref name="tokenCount"/> tokens have been consumed (0-based, exclusive).
    /// Used by chunkers when no separator applies and a character-aligned split is needed.
    /// </summary>
    int GetIndexAtTokenCount(string text, int tokenCount);
}
```

- [ ] **Step 4: Verify build**

```bash
dotnet build src/Connapse.Core -nologo
```

Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Ingestion/Connapse.Ingestion.csproj src/Connapse.Core/Interfaces/ITokenCounter.cs
git commit -m "feat: add ITokenCounter abstraction and Microsoft.ML.Tokenizers package"
```

---

### Task 2: Implement `TiktokenTokenCounter` with tests

**Files:**
- Create: `tests/Connapse.Ingestion.Tests/Utilities/TiktokenTokenCounterTests.cs`
- Create: `src/Connapse.Ingestion/Utilities/TiktokenTokenCounter.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Connapse.Ingestion.Tests/Utilities/TiktokenTokenCounterTests.cs`:

```csharp
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Utilities;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Utilities;

[Trait("Category", "Unit")]
public class TiktokenTokenCounterTests
{
    private readonly ITokenCounter _counter = new TiktokenTokenCounter();

    [Fact]
    public void CountTokens_EmptyString_ReturnsZero()
    {
        _counter.CountTokens("").Should().Be(0);
    }

    [Fact]
    public void CountTokens_KnownEnglishPhrase_MatchesTiktokenReference()
    {
        // "Hello world" tokenizes to exactly 2 tokens under cl100k_base.
        _counter.CountTokens("Hello world").Should().Be(2);
    }

    [Fact]
    public void CountTokens_PunctuationAndUnicode_DoesNotThrow()
    {
        Action act = () => _counter.CountTokens("Hello, 世界! 🎉");
        act.Should().NotThrow();
        _counter.CountTokens("Hello, 世界! 🎉").Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetIndexAtTokenCount_ReturnsMonotonicallyIncreasingIndex()
    {
        string text = string.Join(" ", Enumerable.Range(0, 50).Select(i => $"word{i}"));
        int idx10 = _counter.GetIndexAtTokenCount(text, 10);
        int idx20 = _counter.GetIndexAtTokenCount(text, 20);

        idx10.Should().BeGreaterThan(0);
        idx20.Should().BeGreaterThan(idx10);
        idx20.Should().BeLessThanOrEqualTo(text.Length);
    }

    [Fact]
    public void GetIndexAtTokenCount_RequestExceedsText_ReturnsTextLength()
    {
        string text = "short";
        _counter.GetIndexAtTokenCount(text, 9999).Should().Be(text.Length);
    }
}
```

- [ ] **Step 2: Run tests — expect compile failure**

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "FullyQualifiedName~TiktokenTokenCounterTests" -nologo
```

Expected: build fails — `TiktokenTokenCounter` does not exist.

- [ ] **Step 3: Implement `TiktokenTokenCounter`**

Create `src/Connapse.Ingestion/Utilities/TiktokenTokenCounter.cs`:

```csharp
using Connapse.Core.Interfaces;
using Microsoft.ML.Tokenizers;

namespace Connapse.Ingestion.Utilities;

/// <summary>
/// Real tiktoken-based token counter. Defaults to cl100k_base (matches
/// every OpenAI-compatible embedding model deployed in Connapse today).
/// </summary>
public class TiktokenTokenCounter : ITokenCounter
{
    private readonly Tokenizer _tokenizer;

    public TiktokenTokenCounter(string encodingName = "cl100k_base")
    {
        _tokenizer = TiktokenTokenizer.CreateForEncoding(encodingName);
    }

    public int CountTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return _tokenizer.CountTokens(text);
    }

    public int GetIndexAtTokenCount(string text, int tokenCount)
    {
        if (string.IsNullOrEmpty(text) || tokenCount <= 0) return 0;
        IReadOnlyList<EncodedToken> tokens = _tokenizer.EncodeToTokens(text, out _);
        if (tokens.Count == 0) return 0;
        if (tokenCount >= tokens.Count) return text.Length;
        return tokens[tokenCount - 1].Offset.End.Value;
    }
}
```

If the `EncodeToTokens` overload signature differs in the installed `Microsoft.ML.Tokenizers` version, prefer the simplest overload that returns `IReadOnlyList<EncodedToken>` and exposes per-token `Offset` ranges. The behavior we need is: "give me the source-text index where the N-th token ends."

- [ ] **Step 4: Run tests — expect pass**

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "FullyQualifiedName~TiktokenTokenCounterTests" -nologo
```

Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Ingestion/Utilities/TiktokenTokenCounter.cs tests/Connapse.Ingestion.Tests/Utilities/TiktokenTokenCounterTests.cs
git commit -m "feat: add TiktokenTokenCounter (cl100k_base default)"
```

---

### Task 3: Migrate `FixedSizeChunker` to `ITokenCounter` and register in DI

**Files:**
- Modify: `src/Connapse.Ingestion/Chunking/FixedSizeChunker.cs`
- Modify: `src/Connapse.Ingestion/Extensions/ServiceCollectionExtensions.cs`
- Modify: `tests/Connapse.Ingestion.Tests/Chunking/FixedSizeChunkerTests.cs`

- [ ] **Step 1: Update `FixedSizeChunkerTests` to inject the counter**

In `tests/Connapse.Ingestion.Tests/Chunking/FixedSizeChunkerTests.cs`, replace the `_chunker` field declaration:

```csharp
private readonly FixedSizeChunker _chunker = new(new TiktokenTokenCounter());
```

Add `using Connapse.Ingestion.Utilities;` if not already present.

- [ ] **Step 2: Run tests — expect compile failure**

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "FullyQualifiedName~FixedSizeChunkerTests" -nologo
```

Expected: build fails — `FixedSizeChunker` has no constructor accepting `ITokenCounter`.

- [ ] **Step 3: Refactor `FixedSizeChunker` to use primary constructor + `ITokenCounter`**

Replace the class declaration in `src/Connapse.Ingestion/Chunking/FixedSizeChunker.cs`:

```csharp
public class FixedSizeChunker(ITokenCounter tokenCounter) : IChunkingStrategy
{
    public string Name => "FixedSize";

    public Task<IReadOnlyList<ChunkInfo>> ChunkAsync(
        ParsedDocument parsedDocument,
        ChunkingSettings settings,
        CancellationToken cancellationToken = default)
    {
        // ... existing body, but every static call replaced ...
    }

    // FindNaturalBreakpoint stays static — it doesn't touch tokens
}
```

Replace the two static-helper call sites:
- `TokenCounter.GetCharacterPositionForTokens(content[currentPosition..], maxChunkSize)` → `tokenCounter.GetIndexAtTokenCount(content[currentPosition..], maxChunkSize)`
- `TokenCounter.EstimateTokenCount(chunkText)` → `tokenCounter.CountTokens(chunkText)`
- `TokenCounter.GetCharacterPositionForTokens(chunkText, overlap)` → `tokenCounter.GetIndexAtTokenCount(chunkText, overlap)`

Add `using Connapse.Core.Interfaces;` at the top.

- [ ] **Step 4: Register `ITokenCounter` in DI**

In `src/Connapse.Ingestion/Extensions/ServiceCollectionExtensions.cs`, add the singleton registration as the first line of `AddDocumentIngestion`:

```csharp
services.AddSingleton<ITokenCounter, TiktokenTokenCounter>();
```

Add `using Connapse.Core.Interfaces;` and `using Connapse.Ingestion.Utilities;` if not already present.

- [ ] **Step 5: Run tests — expect pass**

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "FullyQualifiedName~FixedSizeChunkerTests" -nologo
```

Expected: all `FixedSizeChunkerTests` pass.

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Ingestion/Chunking/FixedSizeChunker.cs src/Connapse.Ingestion/Extensions/ServiceCollectionExtensions.cs tests/Connapse.Ingestion.Tests/Chunking/FixedSizeChunkerTests.cs
git commit -m "refactor: inject ITokenCounter into FixedSizeChunker and register in DI"
```

---

### Task 4: Migrate `SemanticChunker` to `ITokenCounter` (mechanical replacement)

**Files:**
- Modify: `src/Connapse.Ingestion/Chunking/SemanticChunker.cs`
- Modify: `tests/Connapse.Ingestion.Tests/Chunking/SemanticChunkerTests.cs`

- [ ] **Step 1: Update `SemanticChunkerTests` to inject the counter**

In `tests/Connapse.Ingestion.Tests/Chunking/SemanticChunkerTests.cs`, change every `new SemanticChunker(...)` construction site to also pass a `TiktokenTokenCounter`:

```csharp
new SemanticChunker(embeddingProvider, new TiktokenTokenCounter())
```

Add `using Connapse.Ingestion.Utilities;` if missing.

- [ ] **Step 2: Run tests — expect compile failure**

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "FullyQualifiedName~SemanticChunkerTests" -nologo
```

Expected: build fails — constructor mismatch.

- [ ] **Step 3: Refactor `SemanticChunker`**

In `src/Connapse.Ingestion/Chunking/SemanticChunker.cs`:

- Replace the class declaration and constructor with a primary constructor:

```csharp
public class SemanticChunker(IEmbeddingProvider embeddingProvider, ITokenCounter tokenCounter) : IChunkingStrategy
{
    public string Name => "Semantic";

    // body unchanged below — but replace every TokenCounter.* call
}
```

- Replace all four call sites:
  - Line 46: `TokenCounter.EstimateTokenCount(sentences[0])` → `tokenCounter.CountTokens(sentences[0])`
  - Line 108: `TokenCounter.EstimateTokenCount(chunkText)` → `tokenCounter.CountTokens(chunkText)`
  - Line 117: `TokenCounter.EstimateTokenCount(subChunk)` → `tokenCounter.CountTokens(subChunk)`
  - Line 169: `TokenCounter.EstimateTokenCount(content)` → `tokenCounter.CountTokens(content)`
  - In `SplitLargeChunk`, line 256: `TokenCounter.GetCharacterPositionForTokens(text, maxTokens)` → call instance method (pass `tokenCounter` as a parameter to `SplitLargeChunk`, since it's `static`).
  - In `SplitLargeChunk`, line 263: `TokenCounter.EstimateTokenCount(chunk)` → same.

- Refactor `SplitLargeChunk` to take `ITokenCounter`:

```csharp
private static List<string> SplitLargeChunk(string text, int maxTokens, int minTokens, ITokenCounter counter)
{
    var result = new List<string>();
    int chunkSize = counter.GetIndexAtTokenCount(text, maxTokens);
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
```

Update the call site (around line 114) to pass `tokenCounter`:

```csharp
var subChunks = SplitLargeChunk(chunkText, settings.MaxChunkSize, settings.MinChunkSize, tokenCounter);
```

Add `using Connapse.Core.Interfaces;` at the top.

- [ ] **Step 4: Run tests — expect pass**

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "FullyQualifiedName~SemanticChunkerTests" -nologo
```

Expected: all `SemanticChunkerTests` pass.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Ingestion/Chunking/SemanticChunker.cs tests/Connapse.Ingestion.Tests/Chunking/SemanticChunkerTests.cs
git commit -m "refactor: inject ITokenCounter into SemanticChunker (mechanical migration)"
```

---

### Task 5: Add three regression tests for `RecursiveChunker` (must fail against the current implementation)

**Files:**
- Modify: `tests/Connapse.Ingestion.Tests/Chunking/RecursiveChunkerTests.cs`

- [ ] **Step 1: Update the existing field to use the constructor**

In `tests/Connapse.Ingestion.Tests/Chunking/RecursiveChunkerTests.cs`, change:

```csharp
private readonly RecursiveChunker _chunker = new();
```

to:

```csharp
private readonly RecursiveChunker _chunker = new(new TiktokenTokenCounter());
```

Add `using Connapse.Ingestion.Utilities;` if missing.

(Note: this will cause the existing tests to fail to compile until Task 6 lands. That's intentional — Task 5 and Task 6 are paired.)

- [ ] **Step 2: Add the three regression tests**

Append these three `[Fact]` tests to `RecursiveChunkerTests`:

```csharp
[Fact]
public async Task ChunkAsync_AdjacentChunksShareOverlap_WhenChunksAreMultiUnit()
{
    // 12 short sentences ~5 tokens each, max=20: each chunk holds several sentences,
    // so overlap can be exercised. Settings.Overlap=8.
    string content = string.Join(" ",
        Enumerable.Range(1, 12).Select(i => $"Item {i} is short."));
    var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
    var settings = new ChunkingSettings
    {
        MaxChunkSize = 20,
        Overlap = 8,
        MinChunkSize = 1,
        RecursiveSeparators = new[] { ". ", " " }
    };

    var result = await _chunker.ChunkAsync(doc, settings);

    result.Should().HaveCountGreaterThan(1, "test inputs should force multi-chunk output");
    for (int i = 0; i < result.Count - 1; i++)
    {
        string prev = result[i].Content;
        string next = result[i + 1].Content;

        bool found = false;
        int maxLen = Math.Min(prev.Length, next.Length);
        for (int len = maxLen; len >= 6; len--)
        {
            string suffix = prev.Substring(prev.Length - len);
            if (next.StartsWith(suffix, StringComparison.Ordinal))
            {
                found = true;
                break;
            }
        }

        found.Should().BeTrue(
            $"chunks {i}->{i + 1} should share overlap text. " +
            $"prev tail='...{prev.Substring(Math.Max(0, prev.Length - 30))}' | " +
            $"next head='{next.Substring(0, Math.Min(next.Length, 30))}...'");
    }
}

[Fact]
public async Task ChunkAsync_DoesNotSilentlyDropContentBetweenLargeChunks()
{
    // Long paragraph + tiny middle paragraph + long paragraph. With a non-trivial
    // MinChunkSize the middle MUST NOT be silently lost.
    string longA = string.Join(" ", Enumerable.Repeat("alpha", 30));
    string tinyB = "x marker.";
    string longC = string.Join(" ", Enumerable.Repeat("charlie", 30));
    string content = longA + "\n\n" + tinyB + "\n\n" + longC;

    var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
    var settings = new ChunkingSettings
    {
        MaxChunkSize = 30,
        Overlap = 0,
        MinChunkSize = 5,
        RecursiveSeparators = new[] { "\n\n", "\n", ". ", " " }
    };

    var result = await _chunker.ChunkAsync(doc, settings);

    bool tinyPresent = result.Any(c => c.Content.Contains("marker"));
    tinyPresent.Should().BeTrue(
        "MinChunkSize must not silently drop document content; " +
        "small segments must be merged into a neighbour, not discarded");
}

[Fact]
public async Task ChunkAsync_OffsetsRoundTripWithSourceText()
{
    string content = string.Join("\n\n",
        Enumerable.Range(1, 8).Select(i => $"Paragraph number {i} with some words."));
    var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
    var settings = new ChunkingSettings
    {
        MaxChunkSize = 10,
        Overlap = 3,
        MinChunkSize = 1,
        RecursiveSeparators = new[] { "\n\n", "\n", ". ", " " }
    };

    var result = await _chunker.ChunkAsync(doc, settings);

    foreach (ChunkInfo c in result)
    {
        c.StartOffset.Should().BeGreaterThanOrEqualTo(0);
        c.EndOffset.Should().BeLessThanOrEqualTo(content.Length,
            "endOffset must never exceed the source length");
        c.EndOffset.Should().BeGreaterThan(c.StartOffset);

        string slice = content.Substring(c.StartOffset, c.EndOffset - c.StartOffset);
        slice.Trim().Should().Be(c.Content,
            "the substring at the recorded offsets should equal the chunk's content");
    }
}
```

- [ ] **Step 3: Run tests — expect both compile and assertion failures**

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "FullyQualifiedName~RecursiveChunkerTests" -nologo
```

Expected: build error first (constructor `RecursiveChunker(ITokenCounter)` doesn't exist) — or, if the build succeeds, the three new tests fail with the patterns documented in the research synthesis. **Do not commit yet — Task 6 fixes this.**

---

### Task 6: Rewrite `RecursiveChunker` (LangChain `_merge_splits` pattern + offset tracking + merge-forward)

**Files:**
- Modify: `src/Connapse.Ingestion/Chunking/RecursiveChunker.cs` (full rewrite)

- [ ] **Step 1: Replace the file contents**

Overwrite `src/Connapse.Ingestion/Chunking/RecursiveChunker.cs` with:

```csharp
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
            tokenCounter);

        // Safety net: if every segment was empty, return the whole document as one chunk.
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
    /// Splits <paramref name="text"/> into chunks, recursing through <paramref name="separators"/>
    /// until each chunk fits in <paramref name="maxTokens"/>. Each returned tuple is
    /// (chunk text, chunk's offset within the original document — derived from
    /// <paramref name="baseOffset"/> + position-in-text).
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
            // No separator applies: token-aligned character fallback.
            int chunkChars = counter.GetIndexAtTokenCount(text, maxTokens);
            if (chunkChars <= 0) chunkChars = text.Length;
            for (int i = 0; i < text.Length; i += chunkChars)
            {
                int len = Math.Min(chunkChars, text.Length - i);
                output.Add((text.Substring(i, len), baseOffset + i));
            }
            return output;
        }

        // Split on activeSep, preserving each piece's offset within the original text.
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

        // Merge phase (LangChain _merge_splits).
        // For oversized splits, recurse and emit immediately (no overlap across recursion).
        int sepLen = counter.CountTokens(activeSep);
        var current = new List<(string Text, int Offset, int Tokens)>();
        int total = 0;

        void Flush()
        {
            if (current.Count == 0) return;
            string joined = string.Join(activeSep, current.Select(c => c.Text));
            output.Add((joined, current[0].Offset));
        }

        void Pop()
        {
            int delta = current[0].Tokens + (current.Count > 1 ? sepLen : 0);
            total -= delta;
            current.RemoveAt(0);
        }

        foreach ((string text, int offset) split in splits)
        {
            int splitTokens = counter.CountTokens(split.text);

            // Oversized atomic split → recurse with remaining separators
            if (splitTokens > maxTokens)
            {
                if (current.Count > 0) { Flush(); current.Clear(); total = 0; }
                if (activeIdx + 1 < separators.Length)
                {
                    var sub = SplitRecursive(
                        split.text,
                        split.offset,
                        separators[(activeIdx + 1)..],
                        maxTokens,
                        overlapTokens,
                        counter,
                        ct);
                    output.AddRange(sub);
                }
                else
                {
                    // No more separators; char-fallback within this split.
                    int chunkChars = counter.GetIndexAtTokenCount(split.text, maxTokens);
                    if (chunkChars <= 0) chunkChars = split.text.Length;
                    for (int i = 0; i < split.text.Length; i += chunkChars)
                    {
                        int len = Math.Min(chunkChars, split.text.Length - i);
                        output.Add((split.text.Substring(i, len), split.offset + i));
                    }
                }
                continue;
            }

            int join = current.Count > 0 ? sepLen : 0;
            if (total + splitTokens + join > maxTokens && current.Count > 0)
            {
                Flush();
                // Pop from head until either total <= overlap, OR there's room for the next split.
                while (total > overlapTokens
                    || (current.Count > 0 && total + splitTokens + (current.Count > 1 ? sepLen : 0) > maxTokens))
                {
                    if (current.Count == 0) break;
                    Pop();
                }
            }

            current.Add((split.text, split.offset, splitTokens));
            total += splitTokens + (current.Count > 1 ? sepLen : 0);
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
        ITokenCounter counter)
    {
        if (input.Count <= 1 || minTokens <= 0) return input;

        var output = new List<(string Text, int Offset)>();
        foreach ((string text, int offset) c in input)
        {
            int tokens = counter.CountTokens(c.text);
            if (tokens >= minTokens || output.Count == 0)
            {
                output.Add((c.text, c.offset));
            }
            else
            {
                // Merge into previous: extend its text from prev.Offset to (c.Offset + c.Length).
                (string prevText, int prevOffset) = output[^1];
                int spanEnd = c.offset + c.text.Length;
                int spanStart = prevOffset;
                int spanLen = spanEnd - spanStart;
                // Defensive: if offsets are non-contiguous or invalid, fall back to concat.
                if (spanLen <= 0 || spanLen < prevText.Length)
                {
                    output[^1] = (prevText + c.text, prevOffset);
                }
                else
                {
                    // We can't reach into the original content here, so keep the merged chunk
                    // as the concatenation of prev + small. This preserves all source content.
                    output[^1] = (prevText + c.text, prevOffset);
                }
            }
        }

        // If the FIRST chunk is undersized and there's a next, fold it forward.
        if (output.Count >= 2)
        {
            int firstTokens = counter.CountTokens(output[0].Text);
            if (firstTokens < minTokens)
            {
                (string firstText, int firstOffset) = output[0];
                (string nextText, int nextOffset) = output[1];
                output[1] = (firstText + nextText, firstOffset);
                output.RemoveAt(0);
            }
        }

        return output;
    }
}
```

- [ ] **Step 2: Run all `RecursiveChunkerTests`**

```bash
dotnet test tests/Connapse.Ingestion.Tests --filter "FullyQualifiedName~RecursiveChunkerTests" -nologo
```

Expected: all 18 `RecursiveChunkerTests` pass (15 original + 3 new regression tests). If `ChunkAsync_OffsetsRoundTripWithSourceText` fails on a specific chunk because the recursive boundary join used a different separator than the source, inspect that chunk's text vs source slice — most likely cause is whitespace handling around separators. The fix is to ensure splits exclude the separator and that the chunk's `EndOffset` reflects the joined-text length.

- [ ] **Step 3: Run the full ingestion test suite**

```bash
dotnet test tests/Connapse.Ingestion.Tests -nologo
```

Expected: all tests pass (RecursiveChunker + FixedSize + Semantic + TiktokenTokenCounter).

- [ ] **Step 4: Commit**

```bash
git add src/Connapse.Ingestion/Chunking/RecursiveChunker.cs tests/Connapse.Ingestion.Tests/Chunking/RecursiveChunkerTests.cs
git commit -m "fix: rewrite RecursiveChunker merge loop, offset tracking, and small-chunk handling

- Adopt LangChain _merge_splits pattern: overlap is preserved by popping from
  the head of the running buffer until either total <= overlap or there's room
  for the next split. Never zero out unless physically necessary.
- Track (text, offset) per split so chunk StartOffset/EndOffset round-trip
  exactly with the source text, even when overlap is applied.
- Replace MinChunkSize silent-discard with a merge-forward post-pass — small
  trailing or interstitial segments are now folded into a neighbour, never lost.
- Use ITokenCounter (real tiktoken) instead of chars * 0.25 heuristic."
```

---

### Task 7: Delete the legacy static `TokenCounter` and run the full test suite

**Files:**
- Delete: `src/Connapse.Ingestion/Utilities/TokenCounter.cs`

- [ ] **Step 1: Verify no remaining references**

```bash
dotnet build -nologo
```

Then search for stragglers:

Use the Grep tool with pattern `TokenCounter\.` across the repo. The only matches should be inside `TiktokenTokenCounter` (which doesn't reference the static type) and test classes that name their tests after token counting. If any source file still calls `TokenCounter.EstimateTokenCount` or `TokenCounter.GetCharacterPositionForTokens`, fix it before deleting.

- [ ] **Step 2: Delete the file**

```bash
git rm src/Connapse.Ingestion/Utilities/TokenCounter.cs
```

- [ ] **Step 3: Build + full test run**

```bash
dotnet build -nologo && dotnet test --filter "Category=Unit" -nologo
```

Expected: build succeeds, all unit tests pass. (Integration tests require Docker per `CLAUDE.md`; they're not part of this plan's verification.)

- [ ] **Step 4: Commit**

```bash
git commit -m "refactor: remove legacy TokenCounter heuristic (replaced by TiktokenTokenCounter)"
```

---

### Task 8: Audit `ChunkingSettings` defaults against industry baseline

**Files:**
- (Possibly) Modify: `src/Connapse.Core/Models/SettingsModels.cs:78-110`

- [ ] **Step 1: Read current defaults**

Open `src/Connapse.Core/Models/SettingsModels.cs` and inspect the `ChunkingSettings` record (lines 78-110). Record current values:

- `Strategy = "Semantic"`
- `MaxChunkSize = 512`
- `Overlap = 50`
- `MinChunkSize = 100`
- `SemanticThreshold = 0.5`
- `RecursiveSeparators = ["\n\n", "\n", ". ", " "]`

- [ ] **Step 2: Compare against research synthesis**

The research synthesis recommends ~512 tokens / 10–20% overlap as the modal default. Current 512 / 50 (~10%) is fine. Current `MinChunkSize = 100` was a workaround for the silent-drop bug — with merge-forward semantics it now means "merge small chunks forward up to this floor", which is benign and useful. **Recommendation: leave defaults unchanged.** Document what `MinChunkSize` now means in the XML doc comment.

- [ ] **Step 3: Update the XML doc comment for `MinChunkSize`**

Replace the existing comment on `MinChunkSize`:

```csharp
/// <summary>
/// Minimum chunk size in tokens (default: 100). Chunks smaller than this are
/// merged into a neighbour rather than emitted alone — they are NEVER discarded.
/// </summary>
public int MinChunkSize { get; set; } = 100;
```

- [ ] **Step 4: Build**

```bash
dotnet build -nologo
```

Expected: succeeds, no test changes needed.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Models/SettingsModels.cs
git commit -m "docs: clarify MinChunkSize semantics (merge-forward, never discard)"
```

---

## Self-review

**Spec coverage:**
- Defect 1 (overlap discard) → Task 6, regression test in Task 5 (`ChunkAsync_AdjacentChunksShareOverlap_WhenChunksAreMultiUnit`). ✅
- Defect 2 (small-chunk content drop) → Task 6 `MergeForwardSmallChunks`, regression test in Task 5 (`ChunkAsync_DoesNotSilentlyDropContentBetweenLargeChunks`). ✅
- Defect 3 (offset drift) → Task 6 split-offset tracking, regression test in Task 5 (`ChunkAsync_OffsetsRoundTripWithSourceText`). ✅
- Defect 4 (chars × 0.25 heuristic) → Tasks 1–4 (`Microsoft.ML.Tokenizers` via `ITokenCounter`/`TiktokenTokenCounter`, all three chunkers migrated, legacy class deleted in Task 7). ✅
- Default-tuning audit → Task 8. ✅

**Type consistency:**
- `ITokenCounter.CountTokens(string)` — used in Tasks 2/3/4/6 with consistent signature.
- `ITokenCounter.GetIndexAtTokenCount(string, int)` — used in Tasks 2/3/4/6 with consistent signature.
- `RecursiveChunker(ITokenCounter)` primary constructor — referenced in Tasks 5 and 6 identically.
- `FixedSizeChunker(ITokenCounter)` — Task 3 only.
- `SemanticChunker(IEmbeddingProvider, ITokenCounter)` — Task 4 only.

**No placeholders:** every step contains the actual file path, the actual code, and the exact command to run.

---

## Open questions to flag during execution

- The exact `Microsoft.ML.Tokenizers` API for token-end offsets (`EncodedToken.Offset.End.Value` vs `GetIndexByTokenCount(...)`) may shift between minor versions. If `EncodeToTokens` doesn't expose offsets in the installed version, fall back to `GetIndexByTokenCount(text, tokenCount, out _, out _)` — both achieve the same goal.
- The `MergeForwardSmallChunks` post-pass keeps the source-aligned offset of the *previous* chunk when merging. This is the conservative choice; if a future requirement needs an exact post-merge text-from-source slice (e.g., for highlighting), revisit.
- `SemanticChunker` still has the `IndexOf(chunkText, currentOffset)` offset bug on its own internal path (`SemanticChunker.cs:120` and `:141`). Fixing it follows the same pattern but is out of scope for this plan — file a follow-up ticket once this lands.
