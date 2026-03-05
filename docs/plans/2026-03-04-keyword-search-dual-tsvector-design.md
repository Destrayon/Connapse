# Dual-Config tsvector Keyword Search (#89) — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix keyword search for technical terms by combining `simple` (exact) and `english` (stemmed) tsvector configs, and switch to `websearch_to_tsquery` for native phrase/negation support.

**Architecture:** EF migration changes the stored computed column on `chunks.search_vector` from single-config to dual-config weighted tsvector. `KeywordSearchService` switches from manual OR-joined `to_tsquery` to `websearch_to_tsquery` with both configs, and from `ts_rank` with min-max normalization to `ts_rank_cd` with normalization flag 32. Tests are rewritten to match the new API surface.

**Tech Stack:** PostgreSQL FTS (tsvector/tsquery), EF Core migrations, C#

---

## Tasks

### Task 1: Create EF migration for dual-config search_vector

**Files:**
- Modify: `src/Connapse.Storage/Data/KnowledgeDbContext.cs:226-229`
- Create: new migration via `dotnet ef` CLI

**Step 1: Update KnowledgeDbContext computed column definition**

In `src/Connapse.Storage/Data/KnowledgeDbContext.cs`, replace line 229:
```csharp
.HasComputedColumnSql("to_tsvector('english', content)", stored: true);
```
With:
```csharp
.HasComputedColumnSql("setweight(to_tsvector('simple', coalesce(content, '')), 'A') || setweight(to_tsvector('english', coalesce(content, '')), 'B')", stored: true);
```

**Step 2: Generate the EF migration**

Run:
```bash
cd src/Connapse.Web && dotnet ef migrations add DualConfigSearchVector --project ../Connapse.Storage/Connapse.Storage.csproj
```
Expected: Migration file created in `src/Connapse.Storage/Migrations/`

**Step 3: Verify the generated migration**

Open the generated migration `.cs` file. It should contain:
- `AlterColumn` on `chunks.search_vector` changing the computed SQL
- The GIN index (`idx_chunks_fts`) should be preserved automatically by EF

If EF doesn't regenerate the index, manually add to `Up()`:
```csharp
migrationBuilder.Sql("DROP INDEX IF EXISTS idx_chunks_fts;");
migrationBuilder.Sql("CREATE INDEX idx_chunks_fts ON chunks USING GIN (search_vector);");
```

Also verify the snapshot file (`KnowledgeDbContextModelSnapshot.cs`) was updated with the new computed SQL.

**Step 4: Commit**

```bash
git add src/Connapse.Storage/Data/KnowledgeDbContext.cs src/Connapse.Storage/Migrations/
git commit -m "feat: add migration for dual-config search_vector (simple + english)"
```

---

### Task 2: Rewrite KeywordSearchService to use websearch_to_tsquery + ts_rank_cd

**Files:**
- Modify: `src/Connapse.Search/Keyword/KeywordSearchService.cs`

**Step 1: Replace the full SearchAsync method body and remove BuildOrQuery/SanitizeTerm**

Replace the entire `KeywordSearchService.cs` with:

```csharp
using Connapse.Core;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Search.Keyword;

public class KeywordSearchService
{
    private readonly KnowledgeDbContext _context;
    private readonly ILogger<KeywordSearchService> _logger;

    public KeywordSearchService(
        KnowledgeDbContext context,
        ILogger<KeywordSearchService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<List<SearchHit>> SearchAsync(
        string query,
        SearchOptions options,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogWarning("Empty query provided to keyword search");
            return [];
        }

        // Build WHERE clause for filters
        var whereClauses = new List<string> { "1=1" };
        var parameters = new List<object> { query }; // {0} = raw query string

        if (!string.IsNullOrEmpty(options.ContainerId))
        {
            var idx = parameters.Count;
            whereClauses.Add($"d.container_id = {{{idx}}}");
            parameters.Add(Guid.Parse(options.ContainerId));
        }

        if (options.Filters != null && options.Filters.TryGetValue("documentId", out var documentId))
        {
            if (Guid.TryParse(documentId, out var docId))
            {
                var idx = parameters.Count;
                whereClauses.Add($"c.document_id = {{{idx}}}");
                parameters.Add(docId);
            }
        }

        if (options.Filters != null && options.Filters.TryGetValue("pathPrefix", out var pathPrefix))
        {
            if (!string.IsNullOrWhiteSpace(pathPrefix))
            {
                var idx = parameters.Count;
                whereClauses.Add($"d.path LIKE {{{idx}}}");
                parameters.Add(pathPrefix + "%");
            }
        }

        var topKIdx = parameters.Count;
        parameters.Add(options.TopK);

        var whereClause = string.Join(" AND ", whereClauses);

        // websearch_to_tsquery handles user input natively: quoted phrases, negation, OR.
        // Query both 'simple' (exact tokens) and 'english' (stemmed) configs so that
        // technical terms like "README" match exactly while "running" still matches "run".
        // ts_rank_cd uses cover density ranking; normalization flag 32 = rank/(rank+1) for 0-1 range.
        var sql = @$"
            SELECT
                c.id as ChunkId,
                c.document_id as DocumentId,
                c.content as Content,
                c.chunk_index as ChunkIndex,
                ts_rank_cd(c.search_vector,
                    websearch_to_tsquery('simple', {{{0}}}) || websearch_to_tsquery('english', {{{0}}}),
                    32) as Rank,
                d.file_name as FileName,
                d.content_type as ContentType,
                d.container_id as ContainerId
            FROM chunks c
            INNER JOIN documents d ON c.document_id = d.id
            WHERE {whereClause}
              AND c.search_vector @@ (websearch_to_tsquery('simple', {{{0}}}) || websearch_to_tsquery('english', {{{0}}}))
            ORDER BY Rank DESC
            LIMIT {{{topKIdx}}}";

        var results = await _context.Database
            .SqlQueryRaw<KeywordSearchRow>(sql, parameters.ToArray())
            .ToListAsync(ct);

        var hits = results
            .Select(r => new SearchHit(
                ChunkId: r.ChunkId.ToString(),
                DocumentId: r.DocumentId.ToString(),
                Content: r.Content,
                Score: r.Rank,
                Metadata: new Dictionary<string, string>
                {
                    { "documentId", r.DocumentId.ToString() },
                    { "fileName", r.FileName },
                    { "contentType", r.ContentType ?? "" },
                    { "containerId", r.ContainerId.ToString() },
                    { "chunkIndex", r.ChunkIndex.ToString() },
                    { "rawRank", r.Rank.ToString("F6") }
                }))
            .ToList();

        _logger.LogInformation(
            "Keyword search for query '{Query}' returned {Count} results (topK={TopK})",
            query,
            hits.Count,
            options.TopK);

        return hits;
    }

    private record KeywordSearchRow(
        Guid ChunkId,
        Guid DocumentId,
        string Content,
        int ChunkIndex,
        float Rank,
        string FileName,
        string? ContentType,
        Guid ContainerId);
}
```

Key changes from current code:
- `BuildOrQuery` and `SanitizeTerm` removed — `websearch_to_tsquery` handles input
- Raw query string passed directly as parameter (parameterized, safe from injection)
- `ts_rank` → `ts_rank_cd` with normalization 32
- Min-max normalization removed — `r.Rank` used directly as score
- `rawRank` format changed to F6 (scores are small)
- Log message simplified (no minScore — not relevant here)

**Step 2: Commit**

```bash
git add src/Connapse.Search/Keyword/KeywordSearchService.cs
git commit -m "feat: switch keyword search to websearch_to_tsquery + ts_rank_cd"
```

---

### Task 3: Rewrite tests for new API surface

**Files:**
- Modify: `tests/Connapse.Core.Tests/Search/KeywordSearchQueryTests.cs`

**Step 1: Replace test file**

`BuildOrQuery` and `SanitizeTerm` no longer exist. The new `KeywordSearchService` passes raw query strings directly to PostgreSQL's `websearch_to_tsquery`, so there are no internal static methods to unit test.

However, we should verify that the service gracefully handles edge cases (empty/whitespace queries return empty). The `SearchAsync` method itself requires a DB context, so pure unit tests are limited. Replace the file with tests that validate the remaining testable behavior:

```csharp
using Connapse.Search.Keyword;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Connapse.Core.Tests.Search;

public class KeywordSearchServiceTests
{
    private readonly KeywordSearchService _service;

    public KeywordSearchServiceTests()
    {
        var options = new DbContextOptionsBuilder<KnowledgeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var context = new KnowledgeDbContext(options);
        var logger = Substitute.For<ILogger<KeywordSearchService>>();
        _service = new KeywordSearchService(context, logger);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SearchAsync_EmptyOrWhitespaceQuery_ReturnsEmpty(string? query)
    {
        var options = new Connapse.Core.SearchOptions { TopK = 10, ContainerId = Guid.NewGuid().ToString() };

        var results = await _service.SearchAsync(query!, options);

        results.Should().BeEmpty();
    }
}
```

Note: The in-memory provider won't support `SqlQueryRaw` with PostgreSQL FTS functions, so we only test the early-return guard. Full search behavior is covered by integration tests (which run against real PostgreSQL via Testcontainers).

**Step 2: Run tests to verify they pass**

Run: `dotnet test tests/Connapse.Core.Tests/ --filter "KeywordSearchServiceTests" -v n`
Expected: PASS (3 tests)

**Step 3: Commit**

```bash
git add tests/Connapse.Core.Tests/Search/KeywordSearchQueryTests.cs
git commit -m "test: rewrite keyword search tests for websearch_to_tsquery API"
```

---

### Task 4: Run full test suite and verify

**Step 1: Run all tests**

Run: `dotnet test -v n`
Expected: All tests pass. If any `BuildOrQuery` or `SanitizeTerm` references exist elsewhere, they'll fail — fix any remaining references.

**Step 2: Commit any fixes**

If fixes were needed:
```bash
git add -A
git commit -m "fix: resolve remaining references to removed keyword search methods"
```

---

## Design Reference

## Problem

PostgreSQL's `english` text search config aggressively stems technical terms:
- "README" -> "readm"
- "template" -> "templat"
- "structure" -> "structur"

This causes zero results for valid developer queries like "README template structure". Additionally, `plainto_tsquery` (replaced by `to_tsquery` with manual OR-joining in PR #85) doesn't support quoted phrase search or negation.

## Solution

Three coordinated changes:

### 1. Migration: Dual-config stored computed column

Replace:
```sql
to_tsvector('english', content)
```

With:
```sql
setweight(to_tsvector('simple', coalesce(content, '')), 'A') ||
setweight(to_tsvector('english', coalesce(content, '')), 'B')
```

- `simple` config: preserves exact tokens (no stemming), assigned weight A (higher rank contribution)
- `english` config: adds stemmed variants, assigned weight B (lower rank contribution)
- `coalesce` prevents NULL content from producing NULL vectors
- GIN index recreated on the same column

### 2. KeywordSearchService: websearch_to_tsquery + ts_rank_cd

**Query matching:**
```sql
search_vector @@ (websearch_to_tsquery('simple', $1) || websearch_to_tsquery('english', $1))
```

- `websearch_to_tsquery` handles user input natively: quoted phrases, negation (`-term`), OR
- Querying both configs ensures exact and stemmed matches are found
- Replaces `BuildOrQuery` + `SanitizeTerm` + `to_tsquery` (all removed)

**Ranking:**
```sql
ts_rank_cd(search_vector, websearch_to_tsquery('simple', $1) || websearch_to_tsquery('english', $1), 32)
```

- `ts_rank_cd`: cover density ranking (rewards proximity of matching terms)
- Normalization flag 32: divides rank by `rank + 1`, producing 0-1 range inherently
- Removes need for client-side min-max normalization

### 3. KnowledgeDbContext: Update computed column definition

Update `HasComputedColumnSql` to match the migration's new expression.

## Files Changed

| File | Change |
|------|--------|
| New migration | ALTER column computed expression, recreate GIN index |
| `KnowledgeDbContext.cs` | Update `HasComputedColumnSql` |
| `KeywordSearchService.cs` | Replace query/ranking logic, remove `BuildOrQuery`/`SanitizeTerm` |
| `KeywordSearchQueryTests.cs` | Remove/rewrite tests for removed methods |

## What This Enables

- "README template structure" -> finds results (simple config matches exact tokens)
- `"cloud sync"` -> phrase search
- `README -template` -> negation
- "running" still matches "run" (english config provides stemming)

## What This Doesn't Change

- GIN index method (still GIN)
- Parameter binding pattern (dynamic index tracking)
- SearchHit output shape
- No changes to hybrid/semantic search

## Decision: No min-max normalization

`ts_rank_cd` with normalization 32 produces inherently 0-1 scores. These scores will cluster low (typical range 0.001-0.1 after normalization), which is fine for ordering within keyword results. Hybrid search fusion will use RRF (#90) which operates on ranks, not raw scores.
