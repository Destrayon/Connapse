# Design: Dual-Config tsvector Keyword Search (#89)

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
