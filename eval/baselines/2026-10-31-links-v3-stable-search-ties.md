# links-v3 with keyword and vector ties broken by path, 2026-10-31 (CON-158)

CON-152 ([2026-10-30-links-v3-stable-ties.md](2026-10-30-links-v3-stable-ties.md)) left one of 307 links-v3
(`links-flink-atlassian`) queries ranking its top 10 differently between from-scratch runs. Its likeliest cause is
that keyword and vector search cut their candidate pools with `ORDER BY score LIMIT k` and no tie-breaker, so
chunks tied at the cut come back in heap order, which varies with ingest order.

**Pass bar, set before measuring.**

- Two runs, each indexing from scratch with `--index-cache off`, rank all 307 queries' top 10 identically, with
  identical scores, under `hybrid-links` and under `hybrid` (0 of 307 differences in each). Both use exact vector
  search (`VectorIndexMinVectors` 1,000,000, CON-152's `hybrid-links-exact` and `hybrid-exact`), because the
  approximate HNSW index is built with random levels and is not expected to repeat across builds.
- nDCG@10 and MRR@10 are unchanged from CON-152's at their printed precision: `hybrid-links` 0.323 and 0.257,
  `hybrid` 0.220 and 0.185.

## Cause, confirmed before the fix

Two from-scratch runs on the pre-fix code (commit 6008a20, which only adds chunk indexes to the candidate capture),
each running `hybrid-links-exact-capture` and `hybrid-exact-capture`, which are the exact configs plus a capture of
each side's top 30 candidates:

- Keyword pools differed in 13 of 307 queries. In every one, the chunks that swapped (2, or 4 for
  `jira-parent-0143`) all score exactly the pool's 30th score in both runs, so they are ties at the cut.
- Vector pools were identical in all 307 queries.
- In these two runs no top 10 changed order, but `jira-blocked-by-0085` moved every score by up to 0.0043 links on
  and 0.0038 links off. That is the same query CON-152 saw move. A different tied chunk at the bottom of the keyword
  pool shifts the pool's normalisation and, with it, every fused score.
- Metrics match CON-152's: `hybrid-links` nDCG@10 0.323, MRR@10 0.257; `hybrid` 0.220, 0.185.

The vector cut-offs get the same tie-breaker anyway, since they have the same `ORDER BY distance LIMIT k` shape.

## Result after the fix

The fix breaks ties at both cut-offs by document path, then chunk index (63e5c82), and ranks and certifies the BM25
cut-off on the float4 score callers receive (3bf57f1). An earlier post-fix pair still swapped chunks that were tied
at float4 but not at float8; ranking on the float4 value lets the path decide those too.

Two from-scratch runs on 3bf57f1 (`20261031-122919` and `20261031-123902`, `--index-cache off`, both capture configs):

| | top-10 order differs | same order, scores differ | keyword pools differ | vector pools differ | nDCG@10 | MRR@10 |
|---|---|---|---|---|---|---|
| `hybrid-links-exact` | 0 of 307 | 0 | 0 | 0 | 0.323 | 0.257 |
| `hybrid-exact` | 0 of 307 | 0 | 0 | 0 | 0.220 | 0.185 |

Both parts of the pass bar hold, and the scores match CON-152's at printed precision.
