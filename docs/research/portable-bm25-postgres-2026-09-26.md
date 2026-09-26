# How to implement BM25 in plain PostgreSQL (Azure Flexible Server compatible)
**Date:** 2026-09-26
**Status:** Reviewed
**Built on:** bm25-only-keyword-ranking-2026-09-26.md, keyword-query-matching-2026-09-26.md

## Executive summary
Connapse can compute exact Lucene BM25 using only PostgreSQL 17 features that Azure Database for PostgreSQL Flexible Server allows.

- **Candidates** come from the existing GIN index on `chunks.search_vector`.
- **Term frequency and chunk length** are read from that tsvector's English (weight B) positions.
- **Per-container IDF statistics** live in small tables kept current by statement-level triggers. The triggers write an append-only delta table, and a background job folds it in; queries add unfolded deltas, so results are always exact.

This needs no new per-chunk storage. The scoring SQL is ported from DuckDB's `fts` extension, the formula from Lucene's `BM25Similarity`, and the golden fixtures come from `bm25s`.

The main uncertainty is latency on very large containers, because every matching chunk is scored. If containers above roughly 300k chunks prove slow, the next step is a postings table with MaxScore pruning (sub-question 3).

## Research brief
**Question:** How can BM25 be implemented for Connapse in plain PostgreSQL 17 plus pgvector, with reference code to port?

**Sub-questions:**
1. The exact variant and reference implementations.
2. Storing and maintaining term statistics.
3. Computing top-k efficiently in SQL.
4. BM25 as a pgvector `sparsevec`.

**Out of scope:** pg_textsearch (covered by #545).

**Success criteria:** a design with ported, verified code and known limits.

## Findings by sub-question

### 1. The BM25 variant: Lucene, ported from DuckDB's SQL
Connapse should implement Lucene's BM25:

`score(q,d) = Σ_{t∈q} qtf(t) · ln(1 + (N − df + 0.5)/(df + 0.5)) · f/(f + k1·(1 − b + b·dl/avgdl))`

with k1 = 1.2 and b = 0.75.

- **Source.** Lucene `BM25Similarity.java` (Apache-2.0, `apache/lucene@e357029`, L138–147 and the scorer), primary. The `1 +` inside the log keeps IDF positive, so no clamping is needed. Robertson's original IDF goes negative when a term is in more than half the documents; rank_bm25 patches that with an epsilon.
- **No `(k1+1)` factor.** Lucene dropped it in 8.0 because it rescales every score equally. `bm25s` `method="lucene"` omits it too; DuckDB and rank_bm25 keep it.
- **Document length.** dl is the number of tokens left after analysis: stop words removed, stems counted with duplicates. Lucene's `IndexingChain` counts only emitted tokens, and DuckDB counts rows of its stemmed terms table.
- **Lossy length encoding.** Lucene rounds lengths above about 40 to 3 significant bits (SmallFloat). Kamphuis et al. (ECIR 2020) found exact and lossy lengths equivalent. Connapse stores exact lengths.
- **Repeated query terms add.** Lucene with k3 off, and `bm25s`, sum a term that appears twice in the query; DuckDB de-duplicates. Connapse follows Lucene.
- **Parameters.** Anserini's BEIR baselines use k1 = 0.9 and b = 0.4, tuned by Trotman et al. 2012 (`SearchCollection.java`, primary). Kamphuis et al. found the choice of variant matters far less than k1 and b. Connapse defaults to 1.2 and 0.75 and exposes both as settings so the eval can compare.
- **SQL reference.** DuckDB `match_bm25`, from duckdb v1.1.3 `extension/fts/fts_indexing.cpp` L152–214 (MIT, © Stichting DuckDB Foundation):
  ```sql
  term_tf AS (SELECT termid, docid, COUNT(*) AS tf FROM qterms GROUP BY docid, termid),
  subscores AS (
    SELECT docs.docid, len, term_tf.termid, tf, df,
     (log(((SELECT num_docs FROM stats) - df + 0.5) / (df + 0.5) + 1)
      * ((tf * (k + 1)/(tf + k * (1 - b + b * (len / (SELECT avgdl FROM stats)))))))
     AS subscore
    FROM term_tf, cdocs, docs, dict WHERE ...),
  scores AS (SELECT docid, sum(subscore) AS score FROM subscores GROUP BY docid)
  ```
  **Porting trap:** `log()` is base 10 in both DuckDB and PostgreSQL, so the port uses `ln()`.
- **Golden fixtures.** Generated with `bm25s` 0.3.11 (`method="lucene"`, k1 = 1.2, b = 0.75) on a five-document corpus that includes a 47-token document. Every value matched an independent Python implementation of the formula to 1e-6.

  Corpus:
  1. `quick brown fox`
  2. `lazy dog`
  3. `quick brown dog quick`
  4. `dog` + 45 × `filler` + `quick`
  5. `brown brown brown brown brown cat`

  | query | doc 1 | doc 2 | doc 3 | doc 4 | doc 5 |
  |---|---|---|---|---|---|
  | `quick dog` | 0.355131 | 0.372966 | 0.755084 | 0.228811 | 0 |
  | `brown` | 0.355131 | 0 | 0.338923 | 0 | 0.469879 |
  | `cat quick zebra` | 0.355131 | 0 | 0.416162 | 0.114405 | 0.798794 |
  | `dog dog` | 0 | 0.745933 | 0.677846 | 0.228811 | 0 |

### 2. Statistics: read tf from the tsvector, keep df in delta-folded tables
Per-chunk term frequencies should come from the existing tsvector. Document frequencies should live in per-owner tables maintained through an append-only delta table.

**Reading tf and length from the tsvector.** All points below were verified on pg17.
- `chunks.search_vector` concatenates a `simple` copy (weight A) and an `english` copy (weight B). `||` merges shared lexemes, so tf and dl must count only B positions: `unnest(ts_filter(search_vector, '{b}'))`, then `cardinality(positions)`. Counting every position double-counts.
- Postgres keeps at most 255 positions per lexeme. `to_tsany.c` `uniqueWORD` stops at `MAXNUMPOS − 1`, although the docs say 256.
- Positions above 16383 are clamped and then de-duplicated, so tf is undercounted only in chunks longer than about 16k tokens.
- `length(tsvector)` counts distinct lexemes, not tokens.
- `strip()` destroys tf, so `search_vector` must never be stripped.

**Counter maintenance**
- Row-level upserts on df deadlocked when two transactions touched the same terms in opposite order (reproduced). Common terms also serialise every concurrent ingest; depesz 2016 measured direct counter updates at about 20 s against about 1.3 s for queued, batched inserts.
- The chosen design:
  - Statement-level `AFTER INSERT/DELETE/UPDATE` triggers with transition tables append ±1 rows to `bm25_delta`.
  - One advisory-locked job folds the deltas into `bm25_term_stats(owner_id, term, df)` and `bm25_owner_stats(owner_id, n_docs, total_len)` in sorted order.
  - Queries add unfolded deltas, so they are exact without waiting for the fold. This was verified exact against `ts_stat` across an insert followed by a delete.
- Computing df at query time from the GIN index cost about 150 ms per common term at 200k chunks. `ts_stat` cost about 4 s per 150k chunks. Both are audit or rebuild tools, not query-path tools.

**Rejected designs**
- A postings table `(owner_id, term, chunk_id, tf)` measured 1.5 GB heap plus 1.3 GB btree at 200k chunks, against 513 MB for `chunks`. That is about 5× the footprint and about 115 extra rows per chunk written.
- Existing pure-SQL projects have gaps. `plpgsql_bm25` stores O(vocabulary × documents), has a negative IDF, and needs a full rebuild on any change. The Billups 2025 post has no maintenance or delete story.

### 3. Top-k efficiency
Scoring every GIN match is exact and fast enough for Connapse's current container sizes. MaxScore over an impact-ordered postings table is the proven scale-up path.

- **IR in SQL.** OldDog (Mühleisen et al., SIGIR 2014; Kamphuis & de Vries, OSIRRC 2019) runs BM25 over `dict`/`terms`/`docs` tables and scores every match, with no pruning.
  - Conjunctive (AND) scoring hurt Robust04 MAP: 0.1736 against 0.2434 for disjunctive (OR).
  - Dropping terms with df above 10% of N cost MAP 0.2434 → 0.2285 on Robust04 and 0.2381 → 0.1907 on Core18.
  - Vespa's gentler `stopword-limit 0.6` changed BEIR nDCG@10 by only −0.17% to +0.48% (Vespa blog, 2025).
- **MaxScore in SQL.** Candidates come from rare terms only, common terms are added only where their stored upper bound could change the top-k, and a Threshold-Algorithm certificate proves the result exact.
  - On a synthetic benchmark (300k chunks, Zipf vocabulary, i9-13900K): 27 ms median against 232 ms exhaustive, with 20/20 queries certified exact.
  - Queries made only of mid- and high-frequency terms took 1.85 s, slower than exhaustive, so it needs a cost-based fallback.
  - It depends on a postings table (the ~5× storage above), and an `avgdl` baked into stored weights must be refreshed or the bounds stop holding.
- **Unsafe shortcuts.** "Top-M per term, then sum" is fast (about 22 ms) but was never certified exact. A hard GIN candidate cap returns rows in physical order, not by score.
- **IDF must be `float8`.** Computing it as `numeric` made scoring about 5× slower.

### 4. BM25 as pgvector `sparsevec`
BM25 as `sparsevec` is exact only as a sequential scan and has no advantage over the tsvector design.

- **What pgvector offers.** Azure Flexible Server ships pgvector 0.8.2 (MS Learn, 2026-07-10), which includes `sparsevec` (added in 0.7.0; at most 1e9 dimensions and 16,000 non-zeros per vector). HNSW caps vectors at 1,000 non-zeros.
- **HNSW recall.** HNSW recall@10 against exact results was 0–0.4 in a benchmark, matching pg_bestmatch's own warning.
- **Exact scan.** About 59 ms for 50k owner rows, 118 ms for 1M rows.
- **Stored weights go stale.** They bake in `avgdl`; Qdrant pins it to a constant of 256. A 20% growth in `avgdl` shifts weights by about 7%.
- **Vocabulary.** Term ids need hashing (murmur3 mod 999,999,937) or a dictionary table. pg_bestmatch's rank-based ids shift on every refresh.

## Chosen design for #548
- **Candidates:** the existing OR tsquery over `search_vector`, restricted to the owner; exclusions as today.
- **Score:** the formula in sub-question 1, with `tf` and `dl` from B positions and `df` and `N`/`avgdl` from the stats tables plus unfolded deltas.
- **Fallbacks:** stop-word-only queries use ts_rank; a missing stats table (migration not run) also falls back.
- **Parameters:** `Bm25K1` and `Bm25B` settings.

## Conflicts and uncertainties
- **Postings table.** The sparsevec research recommends one; the statistics research rejects it on storage. The two answer different questions: speed for very large containers against storage and write cost. It is resolved by deferring postings and MaxScore until measured latency needs them.
- **Duplicate query terms.** The formula research both said Lucene sums duplicates (k3 off) and recommended de-duplicating "like Lucene". Lucene and Elasticsearch create one clause per term occurrence, and `bm25s` sums, so Connapse sums.
- **Unverified Lucene fixtures.** No published worked example exists. The fixtures come from `bm25s`, not Lucene `explain()`.
- **Synthetic benchmarks.** The MaxScore numbers come from synthetic data where tf is almost always 1.

## Gaps — what we did not find
- Real-corpus latency of exhaustive tsvector BM25 at 100k–1M chunks per container; measured in this work only at eval scale.
- A published NanoBEIR BM25 baseline.
- Azure documentation that explicitly mentions `sparsevec`.

## Source quality assessment
- **Primary sources:** the formula and variants rest on Lucene, Anserini, `bm25s`, rank_bm25 and DuckDB source code, plus peer-reviewed papers (Kamphuis et al. ECIR 2020; Mühleisen et al. SIGIR 2014; Kamphuis & de Vries OSIRRC 2019; Lin & Trotman ICTIR 2015). The Postgres limits come from the PG17 source and docs, and the Azure versions from MS Learn.
- **Local reproductions:** the tsvector behaviour, deadlocks, storage sizes and top-k latencies were reproduced in throwaway pg17 containers.
- **Secondary sources:** depesz and Cybertec on counters, the Vespa blog, and the plpgsql_bm25 and Billups write-ups.

## Sources
**Primary:**
- https://github.com/apache/lucene/blob/e357029271b3de560a6ff13c9cab4d4c8103b53b/lucene/core/src/java/org/apache/lucene/search/similarities/BM25Similarity.java
- https://github.com/duckdb/duckdb/blob/19864453f7d0ed095256d848b46e7b8630989bac/extension/fts/fts_indexing.cpp
- https://github.com/duckdb/duckdb-fts
- https://github.com/xhluca/bm25s
- https://github.com/dorianbrown/rank_bm25
- https://github.com/castorini/anserini
- https://hannes.muehleisen.org/publications/SIGIR2014-column-stores-ir-prototyping.pdf
- https://ceur-ws.org/Vol-2409/docker07.pdf
- https://cs.uwaterloo.ca/~jimmylin/publications/Lin_Trotman_ICTIR2015.pdf
- https://www.postgresql.org/docs/17/textsearch-limitations.html
- https://github.com/postgres/postgres/blob/REL_17_STABLE/src/backend/tsearch/to_tsany.c
- https://github.com/pgvector/pgvector
- https://learn.microsoft.com/en-us/azure/postgresql/extensions/concepts-extensions-versions
- https://github.com/qdrant/fastembed/blob/main/fastembed/sparse/bm25.py

**Secondary:**
- https://www.depesz.com/2016/06/14/incrementing-counters-in-database/
- https://www.cybertec-postgresql.com/en/postgresql-count-made-fast/
- https://blog.vespa.ai/tripling-the-query-performance-of-lexical-search/
- https://github.com/jankovicsandras/plpgsql_bm25
- https://toranbillups.com/blog/archive/2025/08/16/building-keyword-search-in-postgres/
- https://github.com/tensorchord/pg_bestmatch.rs
