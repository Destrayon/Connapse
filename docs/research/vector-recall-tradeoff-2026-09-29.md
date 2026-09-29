# Is ~0.98 recall@30 from per-container HNSW acceptable for Connapse, and how could it reach ≥0.99 on PostgreSQL?
**Date:** 2026-09-29
**Status:** Reviewed
**Built on:** vector-index-strategy-2026-09-29.md; #571 scale measurements (docs/plans/vector-index-strategy-571.md)

## Executive summary
Connapse's #571 scale test measured per-container HNSW (m 16, ef_construction 64) searched with a neighbours-first query: containers of 100k–1M vectors dropped from 4–10 s to 25–45 ms p95 at recall@30 of 0.975–0.987 against exact search at ef_search 800. That recall is at the conservative end of industry practice (benchmarks and most vendors operate at 0.90–0.95 recall@10; Pinecone aims near 0.99), and the only studies measuring downstream effects found small losses (≤ ~0.02 nDCG@10, "minor" RAG QA effects even at 0.7 recall). Reaching ≥ 0.99 looks achievable within pgvector 0.8 by raising index build quality (ef_construction 200–256), building the index over `halfvec(768)` to halve its size and build time, and keeping ef_search at 2–3× the inner LIMIT. Storing embeddings as `halfvec` would also keep them out of TOAST, which likely explains why exact search is slow and could push the exact-search threshold from ~20k to ~100k vectors. Nothing measured the effect of ANN recall on hybrid (BM25 + dense) quality, so Connapse's eval should confirm it once.

## Research brief
**Question:** Is ~0.98 recall@30 an acceptable trade for 150–400× lower latency, and which PostgreSQL options (self-hosted and Azure flexible server, pgvector 0.8.2) could reach ≥ 0.99 at < 100 ms p95?
**Sub-questions:** (1) recall targets and downstream impact; (2) pgvector techniques to raise recall; (3) alternatives beyond HNSW tuning.
**Out of scope:** leaving PostgreSQL; changing the embedding model.

## Recall targets and downstream impact
Around 0.98 recall@30 is above the operating points the field uses. The BigANN NeurIPS'21 throughput track ranked entries at ≥ 90% recall@10; Qdrant compares engines across precision thresholds from 0.75 to 0.999; OpenSearch's billion-scale design targets ~0.9; Elastic tuned its default num_candidates for > 90% recall; AWS's pgvector guidance example lands at 0.958 (ef_search 400); Pinecone says average recall "should be close to 0.99". Downstream, Leto et al. 2024 (arXiv 2411.07396, "Toward Optimal Search and Retrieval for RAG") found ANN recall@10 of 0.7 cut gold-document recall by only 2–3% with "minor" QA effects, and Lin (ACL 2025 Industry, arXiv 2409.06464) found HNSW vs flat Lucene on BEIR lost −0.003 to 0.015 nDCG@10 on average (0.020 on the large BioASQ corpus). No study measures ANN recall inside hybrid dense+BM25 fusion or before a cross-encoder. Averages hide tails: per-query recall varies widely for graph indexes (Steiner-hardness, PVLDB 17, 2024); Connapse's worst queries at 0.77–0.90 are typical, and no vendor guarantees a per-query floor.

## Raising recall within pgvector 0.8
The recall plateau near 0.98 comes from graph quality and from ef_search being too small relative to the fetched candidates. Maintainer guidance raises ef_construction before m (ef_construction ≥ 2m); Jonathan Katz measured m 16 / ef_construction 256 at 0.992–0.994 recall on 1M-vector datasets, and Supabase found raising build parameters cut the ef_search needed for 0.99 recall@10 from 250 to 100. HNSW's candidate list is ef_search long, so at fixed ef_search recall@30 trails recall@10, and with ef_search below the inner LIMIT (120) the tail comes from a lower-quality iterative batch; pgvector's contributors advise ef_search ≥ LIMIT, comfortably 2–3×. An index over `embedding::halfvec(768)` kept identical recall at half the size and 2.6× faster builds (Katz, dbpedia-1M), offsetting slower high-ef_construction builds; the query's ORDER BY must match the index expression, with the outer query re-sorting by full-precision distance. Re-ranking results of a full-precision index cannot recover missed neighbours; binary quantization with re-ranking stalled near 0.93 recall on 768-dim text embeddings (AWS). strict_order vs relaxed_order did not change completeness in AWS's tests; keep relaxed_order with ef_search ≥ LIMIT.

## Alternatives beyond HNSW tuning
Faster exact search is the most interesting alternative. pgvector declares `vector` and `halfvec` with `STORAGE = external`; a 768-dim `vector` (3,080 bytes) exceeds the ~2 KB TOAST threshold, so every exact-scan row costs an extra TOAST lookup, while `halfvec(768)` (~1,544 bytes) fits inline. Together with keeping a container's rows physically together (list partitioning or CLUSTER on owner_id) and parallel workers, exact search could plausibly stay under 100 ms up to ~100k–200k vectors (estimate, unmeasured). Per-container IVFFlat reaches 0.99 only with probes high enough to approach an exact scan and was ~30× slower than HNSW at 0.99 on dbpedia-1M. Azure's pg_diskann (Azure-only; vendor claims of 10× lower latency at 95% recall, no 0.99 data) and pgvectorscale (self-hosted only) show advantages only at tens of millions of vectors and would require two code paths. BM25 fusion is not a recall guarantee, though dense and BM25 often miss different documents.

## Recommendation for Connapse
1. Accept ~0.98 as sufficient for shipping the per-container design, and confirm with the eval harness that hybrid nDCG@10 matches exact search.
2. In the implementation, build per-container HNSW over `halfvec(768)` with ef_construction 200–256 (m 16) and search with ef_search ≥ 2–3× the inner LIMIT; re-measure with the scale probe, expecting ≥ 0.99.
3. Evaluate storing embeddings as `halfvec` (a column migration) to take exact search out of TOAST and raise the exact-search threshold; measure first.
4. Skip IVFFlat, binary quantization, pg_diskann and pgvectorscale for now.

## Conflicts and uncertainties
- Expected ≥ 0.99 at ef_construction 256 is extrapolated from 1536-dim and SIFT datasets; GIST-960 reached only ~0.92 at the same settings, so it must be measured on nomic embeddings.
- The ef 400 p95 (65 ms) above ef 800 (45 ms) in Connapse's run looks like cache noise.
- The TOAST explanation for slow exact search is inferred from storage rules, not profiled.

## Gaps — what we did not find
- Any measurement of ANN recall's effect on hybrid dense+BM25 fusion or on cross-encoder output.
- A public ef_construction sweep for 768-dim text embeddings in pgvector.
- Published 0.99-recall numbers for pg_diskann.

## Source quality assessment
Mostly primary: pgvector README and source, maintainer/contributor benchmarks (Jonathan Katz), Supabase and AWS engineering posts with numbers, Pinecone/Elastic/Qdrant/OpenSearch docs, peer-reviewed or preprint papers (Leto et al., Lin ACL 2025, Steiner-hardness PVLDB 2024, BigANN). Vendor latency claims (pg_diskann, pgvectorscale) are unverified; exact-scan latency estimates are the researchers' own.

## Sources
**Primary:** github.com/pgvector/pgvector (README, sql/vector.sql, issue #678); jkatz05.com (pgvector 0.5.0 overview, 150× speedup, scalar/binary quantization); supabase.com/blog/increase-performance-pgvector-hnsw; aws.amazon.com database blog (pgvector 0.8.0 on Aurora, binary quantization, pgvector in production); docs.pinecone.io recall troubleshooting; pinecone.io/blog/serverless-architecture; qdrant.tech/benchmarks; github.com/opensearch-project/k-NN/issues/1779; proceedings.mlr.press/v176/simhadri22a (BigANN); arXiv 2411.07396 (Leto et al.); arXiv 2409.06464 (Lin); arXiv 2108.11480 (Macdonald & Tonellotto); PVLDB 17 p4668 (Steiner-hardness); learn.microsoft.com Azure pg_diskann and pgvector optimisation docs; github.com/timescale/pgvectorscale.
**Secondary:** dbi-services pgvector DBA guide; Hugging Face embedding-quantization blog; Tiger Data pgvector vs Qdrant.
**Tertiary / preprint:** arXiv 2608.25185 (per-query recall on pgvector); mydba.dev.
