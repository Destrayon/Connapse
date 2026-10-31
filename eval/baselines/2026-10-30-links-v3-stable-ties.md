# links-v3 with link-search ties broken by path, 2026-10-30 (CON-152)

Does link search rank links-v3 (`links-flink-atlassian`) the same way on every run once tied linked documents are
ordered by path instead of by document id? CON-149 ([2026-10-29-links-v3-rebuild.md](2026-10-29-links-v3-rebuild.md))
found that two links-on runs ranked 31 of 307 queries' top 10 differently, because the 20-document cut
broke ties by document id, a fresh random GUID on every index.

**Pass bar, set before measuring.**

- Two `hybrid-links` runs, each indexing from scratch with `--index-cache off` (so every document gets new ids),
  return the same documents in the same order, with the same scores, for all 307 queries.
- The dataset's `hybrid-links` nDCG@10, MRR@10 and `hops:1` Recall@10 each lie within CON-107's recorded value plus or
  minus the largest difference CON-149 measured from it: nDCG@10 0.323 ± 0.003, MRR@10 0.257 ± 0.004, Recall@10
  `hops:1` 0.547 ± 0.002.

## Result

Commit `a8d460a`, links-v3 rebuilt offline from its snapshot (4,238 records, 2,923 links), Ollama `nomic-embed-text`
on the GPU as CON-149 ran it, every container at 2 CPUs, 2 ingestion workers. Each pair is two from-scratch runs.

| Pair | Config | Top 10 differs | Same order, scores differ | Largest score difference |
|---|---|---|---|---|
| CON-149, before the fix | `hybrid-links` | 31 of 307 | — | — |
| Links on, HNSW index (as shipped) | `hybrid-links` | 1 of 307 | 1 | 0.042 |
| Links on, exact vector search | `hybrid-links` + `VectorIndexMinVectors` 1,000,000 | 1 of 307 | 1 | 0.014 |
| Links off, exact vector search | `hybrid` + `VectorIndexMinVectors` 1,000,000 | 0 of 307 | 0 | 0 |

All four links-on runs score nDCG@10 0.323, MRR@10 0.257 and `hops:1` Recall@10 0.547, inside the bar. The two
links-off runs score 0.220, 0.185 and 0.366.

**The first bar is not fully met.** The fix takes run-to-run differences from 31 queries to 1, but one query's top 10
still changes between runs (`jira-subtasks-0182` with the HNSW index, `jira-parent-0149` with exact search), and
`jira-blocked-by-0085` keeps its order while every score in it moves by up to 0.004.

What these runs establish about the rest: the corpus has 20,134 vectors, just over the 20,000 at which Connapse builds
an approximate HNSW index, and exact search still drifts, so the index is not needed for the drift (whether it adds
some cannot be told, since a different query changes in each pair). With links off, the top 10 of every query repeats
exactly.

The likeliest cause, not yet shown: link search reads the whole candidate pool (about 100 chunks per side), not the
top 10, and both keyword queries cut that pool with `ORDER BY score DESC LIMIT k` and no tie-breaker
(`KeywordSearchService.cs`, the BM25 query and the `ts_rank` fallback). Chunks tied at the cut then come back in heap
order, which follows ingest order and varies with two parallel workers. A different chunk at the bottom of the pool
would move its minimum, and link search min-max normalises seeds and blended scores over that pool, so every score in
the query would shift a little, as `jira-blocked-by-0085`'s do. The run results keep only final rankings, so
confirming it means recording the pools, seeds and normalisation bounds for these queries in two runs; that and the
keyword tie-break are the follow-up.

These runs measured `a8d460a`. A later commit also orders exactly tied blended results by path, chunk index and chunk
id; the drift above is in scores that differ, so that change cannot remove it.
