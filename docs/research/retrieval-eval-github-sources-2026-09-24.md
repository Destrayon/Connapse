# Can GitHub sources be optimised, and does the optimum differ per source?
**Date:** 2026-09-24
**Status:** Reviewed
**Built on:** github-docs-issues-chunking-2026-09-23.md

## Executive summary
Two offline retrieval evaluations on Destrayon/Connapse (87 markdown docs, 506 issues/PRs, 160 model-written questions) found that retrieval for GitHub sources can be improved substantially, and that the improvement holds on held-out questions: docs MRR@10 0.606 → 0.745 (+13.9, 95% CI +2.1 to +25.8) and issues 0.772 → 0.971 (+19.9, CI +10.5 to +30.1). Almost all of the gain comes from three changes that help every source alike — a local cross-encoder reranker, blending keyword and vector scores instead of reciprocal rank fusion, and the qwen3-embedding:0.6b model. Tuning numeric knobs (fusion weight, chunk size) separately per source did not beat one shared value on held-out questions. Per-source content handling (dropping bot comments from issues, breadcrumbs in docs) moves MRR by 1–3 points, within noise at this sample size.

## Method
Round 1 (2026-09-23) compared current chunking with a prototype (bots dropped, repo named per chunk, fence-safe splits, breadcrumbs) on a mixed docs+issues index with Connapse-like RRF hybrid search. Round 2 (2026-09-24) searched each source type on its own index and ran a grid: 8 docs and 7 issue chunkings × 4 embedding setups (nomic-embed-text with and without task prefixes, qwen3-embedding:0.6b, snowflake-arctic-embed2) × 9 first stages (BM25, vector, RRF at keyword weight 1–3, min-max score blending at α 0.3–0.9) plus a reranker over the top 30. Questions were written by Claude from whole documents, paraphrased, and kept only if their quoted answer appears in the target. Questions were split into dev and held-out halves by a fixed seed before any run; winners were chosen on dev and scored once on held-out. Scoring is by document (MRR@10, hit@3, hit@10), with paired bootstrap CIs and permutation tests. The go rule was written down before results existed.

## Findings
- **Reranker (largest lever).** `Alibaba-NLP/gte-reranker-modernbert-base` (149M, Apache-2.0, 8k context) served by Hugging Face TEI (`ghcr.io/huggingface/text-embeddings-inference:120-1.9` for RTX 50-series/sm_120, `cpu-1.9` fallback). Alone on today's setup: docs 0.602 → 0.757, issues 0.733 → 0.934 MRR. Cost: +45 ms per search (p95 53 ms) reranking 30 chunks on an RTX 5070 Ti; 1.4 GB.
- **Score blending instead of RRF.** Docs 0.602 → 0.651, issues 0.733 → 0.859 MRR with nomic. Matches Bruch et al. 2023 (convex combination beats RRF). No runtime cost. Keyword search alone had beaten equal-weight RRF in round 1 — the vector side was pulling good keyword hits down.
- **qwen3-embedding:0.6b.** Beat nomic-embed-text on both sources (docs 0.651 → 0.688 blended; issues 0.859 → 0.905). Indexing is ~3.7× slower (26 vs 7 ms per chunk on GPU); switching means re-embedding. nomic's task prefixes made no measurable difference.
- **Per-source numeric tuning.** Fusion weight and chunk size chosen per source on dev never beat a shared value on held-out; per-source fusion weight overfit once (docs −6.7, CI −15.2 to +0.8).
- **Per-source content handling.** Dropping bot comments from issues: +0.5 to +3.3 MRR depending on setup (round 1, mixed index: +9.9 issue MRR). Title+heading breadcrumbs in docs: +2.9 (CI −0.9 to +7.0). The full docs prototype helped without a reranker (+5.2, CI −1.4 to +11.8) and not with one (−0.4). Bot removal also cut issue chunks from 2,895 to 849 on the real instance.

## Limitations
One repository; 40 held-out questions per source, so differences of a few points cannot be resolved; model-written questions are easier than real ones, so absolute scores are inflated and only differences are meaningful; the hybrid search is an approximation of Connapse's, not the product code path.

## Reproducing
The harness (corpus export, chunkers, question sets, grid runner, analysis, reports) was built in a session scratch folder and is not in the repository. Tracking issues cover bringing a repeatable version of it into the repo.
