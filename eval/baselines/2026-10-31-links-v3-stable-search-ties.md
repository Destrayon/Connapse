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
