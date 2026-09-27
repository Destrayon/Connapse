# How can plain-SQL BM25 match pg_textsearch's latency?
**Date:** 2026-09-27
**Status:** Reviewed
**Built on:** portable-bm25-postgres-2026-09-26.md, bm25-only-keyword-ranking-2026-09-26.md

## Executive summary
Connapse's plain-SQL BM25 (#548) matched pg_textsearch on quality but not on speed when every query term was common. On a synthetic container of 100k–300k chunks it took 0.95–3.5 s, where the extension took 9–16 ms; the cause was scoring every chunk that matched a common term.

The fix adds **frequency-tier markers**: positionless lexemes `term\x1Fn` for n ∈ {2,3,4,5,6,8,11,16}, stored in the existing `search_vector`. With them, the existing GIN index can bound every chunk's BM25 score.

Each query runs a few rounds. Each round asks GIN, through an OR-of-AND tsquery, only for chunks whose bound can beat a threshold. The loop stops when the k-th best score proves every other chunk can't enter the top k.

In a prototype on identical data this ran at 1.9 / 3.8 / 5.9 ms (100k) and 6.1 / 8.2 / 12.8 ms (300k) for rare / mixed / common-only queries. pg_textsearch ran at 4 / 5 / 9 and 4 / 6 / 16 ms, so the two are at parity, the SQL version is exact, and it needs no new table. The measured numbers for the shipped implementation are in the last section.

## Research brief
**Question:** Can Connapse's plain-SQL BM25 (PostgreSQL 17 + pgvector, compatible with Azure Flexible Server) come within about 2× of pg_textsearch's latency at 100k–300k chunks per container, for rare, mixed and common-only queries, while staying exact or provably exact?

**Sub-questions:**
1. How pg_textsearch achieves its speed.
2. A postings table with block-max or impact pruning, benchmarked.
3. How far the tsvector/GIN design can be pushed, benchmarked.
4. How often real questions hit the slow path.

**Success criteria:** a design within about 2× on all three query types, or a documented reason none exists.

## Findings by sub-question

### 1. How pg_textsearch achieves its speed: block maxima, not magic
pg_textsearch (Tiger Data) gets its speed from storing, for every block of 128 postings, the block's highest term frequency and its shortest document. At query time those two values bound the score of every document in the block, so whole blocks that can't beat the current k-th score are skipped.

- **Skip entry.** `TpSkipEntry` records `block_max_tf` and `block_max_norm` (`src/segment/format.h`, v1.4.0; primary).
- **Algorithm.** It runs Block-Max WAND (`src/scoring/bmw.c`), choosing a pivot document from summed term maxima and checking blocks with `block_upper + non_pivot_max <= threshold`.
- **Common-only queries.** They stay fast because low-IDF terms have small maxima: documents matching only a few of them never become pivots.
- **Not exact.** It is not exact against true lengths. Documents store a one-byte quantized length ("fieldnorm", as in Lucene/Tantivy), so lengths 145–151 all score as 144.
- **Not per container by default.** Its IDF is per index, so it is per container only if each container has its own partial index.
- **Published speed.** On MS MARCO (8.8M passages) its p50 latency is 0.7 ms for a 1-token query and 16.7 ms for 7 tokens (the repo's benchmark page; secondary).

### 2. A postings table with block-max pruning: exact, but 2–7× slower
A separate inverted index in ordinary tables is exact but does not reach parity. All figures are from a benchmark on the shared synthetic corpus, median of 3 warm runs.

- **Design.** Postings are grouped into impact tiers of `floor(16·tf-part)` and chunked into rows of about 128 postings. Each row stores `cids int[]`, `fs smallint[]` and `dls smallint[]`; a forward index holds per-chunk term arrays.
- **Latency.** 1.1 / 9.5 / 32 ms at 100k and 1.9 / 20 / 92–109 ms at 300k.
- **The limit.** The SQL executor spends about 0.7 µs per posting and 8–10 µs per forward-index lookup. Disjunctive MaxScore bounds cannot supply the pivoting BMW uses on flat-IDF queries.
- **Size and write cost.** Storage is 338 MB of postings plus 255 MB of forward index at 300k, and each chunk insert costs 29–39 ms because about 120 array rows are rewritten.
- **Row-per-posting variant.** A Threshold Algorithm version over a row-per-posting btree needed 3.7 GB and 285–1,124 ms.

Rejected.

### 3. The tsvector/GIN design with frequency markers: parity, exact
Adding frequency markers to the tsvector lets GIN itself answer "chunks that could score above θ". That reached parity on the synthetic benchmark with exact results.

**Variants tried**
- **Rarest-term-first candidates with per-term max-tf and min-length bounds:** 1.6 / 15 / 128 ms at 100k, 4.5 / 50 / 347 ms at 300k.
- **Champion lists** (a precomputed top-R per term): the exactness certificate failed at every R, because short chunks give flat tf distributions.
- **Parallel exhaustive scoring:** at best about 6× faster.
- **Markers plus a DNF candidate tsquery:** 1.9 / 3.8 / 5.9 ms at 100k and 6.1 / 8.2 / 12.8 ms at 300k. GIN grows about 12%.

**How the marker bound works**
- **The bound.** A chunk at tier j for term t has tf < next tier (capped at the term's max tf). The BM25 contribution grows with f and shrinks with length, so it is at most `w_t · tfpart(min(next−1, maxtf_t), minlen_t)`.
- **The candidate query.** It is the OR of every minimal combination of (term, tier) whose summed bounds exceed θ. Markers are cumulative, so a chunk matches a clause exactly when its tiers dominate it.
- **Slack.** Terms whose combined maximum is under 30% of θ are left out of the clauses and assumed to contribute their maximum. Without this, near-zero-weight common terms generated 69 clauses and took 47 ms in GIN.

**Where the time goes.** Exhaustive scoring costs about 7.5 µs per chunk, mostly detoasting the ~1.9 KB tsvector twice.

### 4. Real questions rarely take the slow path, but long questions need pruning anyway
Across 16 eval datasets (1,748 test questions), 4.6% of questions contained only common terms, meaning their rarest term was in more than 5% of chunks.

- **Where it happens.** It concentrates in narrow-domain collections: a single product manual (29%), finance Q&A (12%), debate topics (8%). Ten of the 16 datasets had none.
- **Scale.** df/N doesn't depend on corpus size, and the share held or fell as the corpus grew.
- **Long questions.** Whole arguments or support tickets match 95–97% of chunks with at least one term, even though their rarest term is rare. A per-chunk bound pruner could skip 71–96% of those candidates.
- **Conclusion.** Pruning helps well beyond the rare common-only case.

## Implemented design (#549)
- **Migration.** `bm25_markers(tsvector)` is appended to the `search_vector` expression. `bm25_length` becomes a generated column. `bm25_term_stats` gains `max_tf` (only ever raised) and `min_len` (only ever lowered). Stale values after deletes are looser bounds, never unsafe.
- **`Bm25Pruning`.** Computes the per-level bounds, the slack, the minimal clause enumeration (DFS with suffix-max pruning, dominated clauses dropped) and the tsquery text. A brute-force unit test checks that every level vector with a bound above θ matches a clause.
- **`KeywordSearchService`.** Queries whose postings sum to 4,000 or fewer are scored in one pass. Otherwise θ starts at 70% of the summed maxima, and each round scores the new DNF's chunks minus those already scored, limited to k. It stops when the k-th score ≥ θ; otherwise θ becomes the k-th score, or drops 40% while fewer than k are found. After 6 rounds the remaining matches are scored in full.

### Measured: the shipped implementation (commit dbac6a5)
The timings are end to end through `KeywordSearchService.SearchAsync`: the statistics lookup, the pruning rounds and the EF round trips. Setup: one container of synthetic chunks, top 30, median of 5 runs on the integration-test Postgres.

| chunks | rare | mixed | common-only |
|---|---|---|---|
| 100k | 9.1 ms | 11.6 ms | 13.1 ms |
| 300k | 14.3 ms | 13.8 ms | 21.0 ms |

**Comparison:**
- **pg_textsearch, bare SQL:** 4 / 5 / 9 ms at 100k and 4 / 6 / 16 ms at 300k.
- **The first plain-SQL version:** 3 / 40 / 950 ms at 100k and 9 / 70–146 / 3,400 ms at 300k.

**Reading the gap:** the service numbers carry about 5 ms of per-request overhead that the bare-SQL extension timings do not. With that in mind, the common-only case is within 1.3× of the extension and the others within about 2–3×.

**The fold bottleneck.** Benchmarking also exposed a flaw: a bulk load of 300k chunks wrote about 39M delta rows, one per term per chunk.
- **Fold.** The single-statement fold's memory and running time grew with that backlog, it hit the 30 s command timeout, and it retried forever. Searches, which add unfolded deltas, timed out too, and the test Postgres was eventually killed for running out of memory.
- **Fix.** The triggers now aggregate deltas per statement, one row per term, and the fold runs in batches of 50k rows, each in its own transaction.
- **Result.** Seeding 300k chunks took 165 s, down from over 300 s; the fold then took 1 s; Postgres peaked at about 360 MB.

## Conflicts and uncertainties
- **Exactness bar.** pg_textsearch is itself not exact (quantized lengths), so Connapse's exactness bar is stricter than the reference it is compared against.
- **Chunk-length variation.** Bounds use each term's shortest chunk, so real corpora with very short chunks (a document's last chunk) loosen them. The synthetic corpus has uniform lengths, so it does not test this.
- **Clause growth.** The clause count grows with query length; it is capped at 64, above which the search falls back to scoring every match.
- **Starting threshold and slack.** 0.7 and 0.3 are hand-tuned from the prototype.

## Gaps — what we did not find
- How pg_textsearch behaves on the same real corpora; its `log_bmw_stats` counters were not collected.
- A pruning-rounds profile on real eval queries, as opposed to synthetic ones.

## Source quality assessment
- **Primary:** the pg_textsearch source (v1.4.0 at commit 7a93250).
- **Reproduced locally:** every benchmark in throwaway pg17 containers on one machine (i9-13900K). Numbers varied about ±15% while several agents benchmarked concurrently.
- **Literature (peer-reviewed, cited from memory):** MaxScore (Turtle & Flood 1995), WAND (Broder et al. 2003), BMW (Ding & Suel 2011), impact layers (Anh & Moffat 2006), TA/NRA (Fagin et al. 2001).

## Sources
- **Primary:**
  - https://github.com/timescale/pg_textsearch/blob/7a932505b537d50ad8d2d068a053cd1dd7b646ea/src/segment/format.h
  - https://github.com/timescale/pg_textsearch/blob/7a932505b537d50ad8d2d068a053cd1dd7b646ea/src/scoring/bmw.c
  - https://github.com/timescale/pg_textsearch/blob/7a932505b537d50ad8d2d068a053cd1dd7b646ea/src/scoring/bm25.c
  - Turtle & Flood, IP&M 31(6), 1995
  - Broder et al., CIKM 2003
  - Ding & Suel, SIGIR 2011
  - Anh & Moffat, SIGIR 2006
  - Fagin, Lotem & Naor, PODS 2001
- **Secondary:** the pg_textsearch benchmark page (`benchmarks/gh-pages/comparison.html`); Manning, Raghavan & Schütze, *Introduction to Information Retrieval* §7.
