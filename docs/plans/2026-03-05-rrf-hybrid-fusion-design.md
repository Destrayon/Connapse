# RRF as Built-In Hybrid Fusion — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Move RRF fusion from an optional reranker into a built-in step of hybrid search, so hybrid always deduplicates and fuses results correctly.

**Architecture:** Inline the RRF algorithm directly into `HybridSearchService.PerformHybridSearchAsync`. Remove the separate `RrfReranker` class entirely. The optional reranker pipeline (CrossEncoder) still runs after fusion.

**Tech Stack:** C# / .NET, xUnit + FluentAssertions + NSubstitute

---

## Task 1: Write failing tests for hybrid RRF fusion

**Files:**
- Create: `tests/Connapse.Core.Tests/Search/HybridSearchFusionTests.cs`

**Step 1: Write the test file**

These tests call `PerformHybridSearchAsync` indirectly via `SearchAsync` with `SearchMode.Hybrid`. Since `PerformHybridSearchAsync` is private, we test through the public `IKnowledgeSearch.SearchAsync` interface using mocked `VectorSearchService` and `KeywordSearchService`.

However, `HybridSearchService` resolves those via `IServiceScopeFactory` — so we need to mock the scope factory to return our mocked services. Here's the full test class:

```csharp
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search.Hybrid;
using Connapse.Search.Keyword;
using Connapse.Search.Vector;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Search;

[Trait("Category", "Unit")]
public class HybridSearchFusionTests
{
    private readonly VectorSearchService _vectorSearch;
    private readonly KeywordSearchService _keywordSearch;
    private readonly IOptionsMonitor<SearchSettings> _searchSettings;
    private readonly HybridSearchService _sut;

    public HybridSearchFusionTests()
    {
        _vectorSearch = Substitute.For<VectorSearchService>();
        _keywordSearch = Substitute.For<KeywordSearchService>();
        _searchSettings = Substitute.For<IOptionsMonitor<SearchSettings>>();
        _searchSettings.CurrentValue.Returns(new SearchSettings { RrfK = 60, Reranker = "None" });

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(VectorSearchService)).Returns(_vectorSearch);
        serviceProvider.GetService(typeof(KeywordSearchService)).Returns(_keywordSearch);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);

        var asyncScope = Substitute.For<AsyncServiceScope>(scope);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _sut = new HybridSearchService(
            scopeFactory,
            Enumerable.Empty<ISearchReranker>(),
            _searchSettings,
            Substitute.For<ILogger<HybridSearchService>>());
    }

    private SearchOptions HybridOptions(int topK = 20) =>
        new() { Mode = SearchMode.Hybrid, TopK = topK, MinScore = 0f };

    [Fact]
    public async Task HybridSearch_BothSourcesReturnResults_FusesAndDeduplicates()
    {
        _vectorSearch.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchHit>
            {
                Hit("c1", 0.9f), Hit("c2", 0.8f), Hit("c3", 0.7f)
            });
        _keywordSearch.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchHit>
            {
                Hit("c2", 0.95f), Hit("c4", 0.85f), Hit("c5", 0.75f)
            });

        var result = await _sut.SearchAsync("test", HybridOptions());

        result.Hits.Select(h => h.ChunkId).Should().OnlyHaveUniqueItems();
        result.Hits.Should().HaveCount(5); // c1,c2,c3,c4,c5
        // c2 in both lists → highest RRF score → first
        result.Hits.First().ChunkId.Should().Be("c2");
        result.Hits.First().Metadata["source"].Should().Be("both");
    }

    [Fact]
    public async Task HybridSearch_VectorReturnsEmpty_ReturnsKeywordOnly()
    {
        _vectorSearch.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchHit>());
        _keywordSearch.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchHit> { Hit("c1", 0.9f), Hit("c2", 0.8f) });

        var result = await _sut.SearchAsync("test", HybridOptions());

        result.Hits.Should().HaveCount(2);
        result.Hits.Should().AllSatisfy(h => h.Metadata["source"].Should().Be("keyword"));
    }

    [Fact]
    public async Task HybridSearch_KeywordReturnsEmpty_ReturnsVectorOnly()
    {
        _vectorSearch.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchHit> { Hit("c1", 0.9f) });
        _keywordSearch.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchHit>());

        var result = await _sut.SearchAsync("test", HybridOptions());

        result.Hits.Should().HaveCount(1);
        result.Hits[0].Metadata["source"].Should().Be("vector");
    }

    [Fact]
    public async Task HybridSearch_DisjointResults_AllIncluded()
    {
        _vectorSearch.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchHit> { Hit("c1", 0.9f), Hit("c2", 0.8f) });
        _keywordSearch.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchHit> { Hit("c3", 0.9f), Hit("c4", 0.8f) });

        var result = await _sut.SearchAsync("test", HybridOptions());

        result.Hits.Should().HaveCount(4);
        result.Hits.Should().AllSatisfy(h =>
            h.Metadata["source"].Should().BeOneOf("vector", "keyword"));
    }

    [Fact]
    public async Task HybridSearch_OverlappingChunk_TaggedBoth()
    {
        _vectorSearch.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchHit> { Hit("c1", 0.9f) });
        _keywordSearch.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchHit> { Hit("c1", 0.8f) });

        var result = await _sut.SearchAsync("test", HybridOptions());

        result.Hits.Should().HaveCount(1);
        result.Hits[0].ChunkId.Should().Be("c1");
        result.Hits[0].Metadata["source"].Should().Be("both");
    }

    [Fact]
    public async Task HybridSearch_ScoresNormalizedTo01()
    {
        _vectorSearch.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchHit> { Hit("c1", 0.9f), Hit("c2", 0.8f) });
        _keywordSearch.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchHit> { Hit("c3", 0.7f) });

        var result = await _sut.SearchAsync("test", HybridOptions());

        result.Hits.Should().AllSatisfy(h =>
        {
            h.Score.Should().BeGreaterThanOrEqualTo(0f);
            h.Score.Should().BeLessThanOrEqualTo(1f);
        });
        result.Hits.Max(h => h.Score).Should().Be(1.0f);
    }

    [Fact]
    public async Task HybridSearch_ResultsSortedByScoreDescending()
    {
        _vectorSearch.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchHit> { Hit("c1", 0.9f), Hit("c2", 0.5f) });
        _keywordSearch.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchHit> { Hit("c3", 0.8f), Hit("c4", 0.4f) });

        var result = await _sut.SearchAsync("test", HybridOptions());

        for (int i = 1; i < result.Hits.Count; i++)
            result.Hits[i - 1].Score.Should().BeGreaterThanOrEqualTo(result.Hits[i].Score);
    }

    private static SearchHit Hit(string chunkId, float score) =>
        new(chunkId, $"doc-{chunkId}", $"Content {chunkId}", score, new Dictionary<string, string>());
}
```

> **Note:** `VectorSearchService` and `KeywordSearchService` may not be directly mockable with NSubstitute if they're concrete classes without virtual methods. If so, the tests will need to use `IServiceScopeFactory` mocking more carefully. Verify this compiles in Step 2 and adjust — the test logic is correct regardless of the mocking mechanism.

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~HybridSearchFusionTests" -v n`
Expected: FAIL — current `PerformHybridSearchAsync` doesn't do RRF fusion, doesn't tag sources with `"both"`, doesn't deduplicate.

**Step 3: Commit**

```bash
git add tests/Connapse.Core.Tests/Search/HybridSearchFusionTests.cs
git commit -m "test: add failing tests for hybrid RRF fusion (#90)"
```

---

## Task 2: Implement RRF fusion in `PerformHybridSearchAsync`

**Files:**
- Modify: `src/Connapse.Search/Hybrid/HybridSearchService.cs:138-187`

**Step 1: Replace the concatenation block**

Replace `PerformHybridSearchAsync` body (after `Task.WhenAll` and the debug log) with RRF fusion:

```csharp
// --- RRF fusion ---
var k = _searchSettingsMonitor.CurrentValue.RrfK;

// Build RRF scores by ChunkId
var rrfScores = new Dictionary<string, (SearchHit hit, double rrfScore, bool inVector, bool inKeyword)>();

// Process vector results (ranked by original score descending)
for (int i = 0; i < vectorResults.Count; i++)
{
    var hit = vectorResults[i];
    var rank = i + 1; // 1-indexed
    var contribution = 1.0 / (k + rank);

    rrfScores[hit.ChunkId] = (hit, contribution, true, false);
}

// Process keyword results and merge
for (int i = 0; i < keywordResults.Count; i++)
{
    var hit = keywordResults[i];
    var rank = i + 1;
    var contribution = 1.0 / (k + rank);

    if (rrfScores.TryGetValue(hit.ChunkId, out var existing))
    {
        // Chunk in both lists — sum contributions
        rrfScores[hit.ChunkId] = (existing.hit, existing.rrfScore + contribution, true, true);
    }
    else
    {
        rrfScores[hit.ChunkId] = (hit, contribution, false, true);
    }
}

if (rrfScores.Count == 0)
    return [];

// Normalize to 0-1
var maxScore = rrfScores.Values.Max(v => v.rrfScore);
var minScore = rrfScores.Values.Min(v => v.rrfScore);
var range = maxScore - minScore;

return rrfScores.Values
    .Select(v =>
    {
        var normalized = range > 0 ? (float)((v.rrfScore - minScore) / range) : 1.0f;
        var source = (v.inVector, v.inKeyword) switch
        {
            (true, true) => "both",
            (true, false) => "vector",
            _ => "keyword"
        };

        return v.hit with
        {
            Score = normalized,
            Metadata = new Dictionary<string, string>(v.hit.Metadata)
            {
                ["source"] = source,
                ["rrfScore"] = v.rrfScore.ToString("F6")
            }
        };
    })
    .OrderByDescending(h => h.Score)
    .ToList();
```

Also remove the per-result source tagging from the `vectorTask` and `keywordTask` lambdas (lines 151-154 and 164-167) — RRF fusion handles tagging now.

**Step 2: Run tests to verify they pass**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~HybridSearchFusionTests" -v n`
Expected: PASS (all 7 tests)

**Step 3: Run full test suite**

Run: `dotnet test -v n`
Expected: All tests pass. Some old RrfReranker tests may still pass (they test the class that still exists).

**Step 4: Commit**

```bash
git add src/Connapse.Search/Hybrid/HybridSearchService.cs
git commit -m "feat: inline RRF fusion into hybrid search (#90)"
```

---

## Task 3: Remove `RrfReranker` and update settings

**Files:**
- Delete: `src/Connapse.Search/Reranking/RrfReranker.cs`
- Delete: `tests/Connapse.Core.Tests/Search/RrfRerankerTests.cs`
- Modify: `src/Connapse.Search/Extensions/ServiceCollectionExtensions.cs:26` — remove `RrfReranker` registration
- Modify: `src/Connapse.Core/Models/SettingsModels.cs:126` — change default from `"RRF"` to `"None"`
- Modify: `src/Connapse.Core/Models/SettingsModels.cs:124` — update docstring
- Modify: `src/Connapse.Web/Components/Settings/SearchSettingsTab.razor:27` — remove RRF option from dropdown

**Step 1: Delete files**

```bash
rm src/Connapse.Search/Reranking/RrfReranker.cs
rm tests/Connapse.Core.Tests/Search/RrfRerankerTests.cs
```

**Step 2: Remove DI registration**

In `ServiceCollectionExtensions.cs`, remove line 26:
```csharp
services.AddScoped<ISearchReranker, RrfReranker>();
```

Also remove the `using Connapse.Search.Reranking;` if `CrossEncoderReranker` is in the same namespace (it is — keep the using).

**Step 3: Update `SearchSettings.Reranker` default**

In `SettingsModels.cs`, change:
```csharp
/// <summary>
/// Reranking strategy: None | CrossEncoder
/// </summary>
public string Reranker { get; set; } = "None";
```

**Step 4: Update SearchSettingsTab.razor**

Remove line 27 (`<option value="RRF">RRF (Reciprocal Rank Fusion)</option>`).

The K-value field (lines 33-36) stays — it controls hybrid fusion. Update the label and help text:
```razor
<label class="form-label">Hybrid Fusion K-value</label>
<InputNumber @bind-Value="localSettings.RrfK" class="form-control" />
<small class="form-text text-muted">Controls RRF rank fusion in hybrid search (typically 60)</small>
```

**Step 5: Run full test suite**

Run: `dotnet test -v n`
Expected: All tests pass (RrfReranker tests deleted, no compilation errors).

**Step 6: Commit**

```bash
git add -A
git commit -m "refactor: remove RrfReranker, RRF is now built into hybrid fusion (#90)"
```

---

## Task 4: Verify and clean up

**Step 1: Search for any remaining references to `RrfReranker`**

```bash
grep -r "RrfReranker" src/ tests/ --include="*.cs" --include="*.razor"
```

Expected: No results.

**Step 2: Run full test suite one final time**

Run: `dotnet test -v n`
Expected: All tests pass.

**Step 3: Build**

Run: `dotnet build -v q`
Expected: Build succeeded, 0 warnings related to our changes.
