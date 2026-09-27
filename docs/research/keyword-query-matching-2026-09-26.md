# How should Connapse's Postgres keyword search parse and match user queries?
**Date:** 2026-09-26
**Status:** Reviewed
**Built on:** github-docs-issues-chunking-2026-09-23.md (flagged `ts_rank_cd` lacking IDF); Connapse knowledge-base tools were not available in this session.

## Executive summary
Connapse's keyword search should treat a plain query as **any-term (OR) matching ranked by score**. That is the default in Lucene/Elasticsearch, Tantivy, Weaviate, Vespa 8+ (weakAnd), Anserini's BEIR baselines and the LangChain/LlamaIndex BM25 retrievers. Quoted phrases should be **optional, higher-scoring clauses**, `-term` a **hard exclusion**, and words like "or" plain text (dropped as stopwords), not operators. In Postgres, `ts_rank` fits OR queries better than `ts_rank_cd`: under OR, cover density degenerates into an unsaturated term count. Neither function has IDF, which is the most likely reason MS MARCO fell from 0.332 to 0.255 nDCG@10 once the all-terms filter stopped acting as a crude IDF. The durable fix for ranking is BM25 via **pg_textsearch** (PostgreSQL License, GA v1.4.0, PG17/18, OR-ranked by default), tracked in #545.

## Research brief
**Question:** How should Connapse build its Postgres full-text query from a user's free text: default operator, phrases, negation, stopwords, minimum-should-match and ranking function?
**Sub-questions:** (1) how mature engines and RAG frameworks treat free text; (2) the correct Postgres construction and ranking function; (3) empirical evidence on OR vs AND, IDF, phrases and minimum-should-match; (4) which BM25 extension to adopt next.
**Out of scope:** reranking, embeddings, fusion weights.
**Success criteria:** a concrete query design for #544 and a spike target for #545.

## Findings by sub-question

### 1. How search engines treat free-text queries
Any-term matching with score ranking is the industry default for natural-language input (all primary sources).
- Elasticsearch `match`, `simple_query_string` and `query_string` all default to OR. `minimum_should_match` is off by default. Elastic's own hybrid RRF example sends the raw question through a plain `match`. ([match](https://www.elastic.co/docs/reference/query-languages/query-dsl/query-dsl-match-query), [hybrid](https://www.elastic.co/docs/solutions/search/hybrid-semantic-text))
- In Lucene, bare terms and quoted phrases are optional (SHOULD) clauses, `+` makes a clause required, and `-` excludes. A query of only exclusions matches nothing. ([BooleanClause.Occur](https://lucene.apache.org/core/9_0_0/core/org/apache/lucene/search/BooleanClause.Occur.html))
- Elastic documents a trap: with OR as the default, `foo bar -baz` also returns every document without `baz`. Exclusions must stay required even when the other terms are optional. ([simple_query_string](https://www.elastic.co/docs/reference/query-languages/query-dsl/query-dsl-simple-query-string-query))
- Elastic advises against `query_string` (where AND/OR/NOT are operators) for search boxes. Treating "or" in a question as an operator is what Connapse's first fix did, and it misrouted 11 of 14 still-empty ArguAna queries.
- Vespa 8 changed its default from `all` to `weakAnd` (an OR that skips documents unable to reach the top hits). Its stopwords are terms above a corpus document-frequency limit. ([Vespa 8 notes](https://docs.vespa.ai/en/vespa8-release-notes.html), [query API](https://docs.vespa.ai/en/reference/api/query.html))
- Weaviate BM25 defaults to `or` with an optional `minimumOrTokensMatch`. ([bm25](https://docs.weaviate.io/weaviate/search/bm25))
- Tantivy defaults to OR. Quickwit defaults to AND and is the lone outlier, which suits its use case of log search. ([Tantivy](https://docs.rs/tantivy/latest/tantivy/query/struct.QueryParser.html), [Quickwit](https://quickwit.io/docs/reference/query-language))

### 2. Correct Postgres construction and ranking
Build the OR query from per-clause tsqueries and rank with `ts_rank`. Each point below was verified on pg17 or taken from the Postgres docs or source.
- **Text-replacing `' & '` with `' | '` is fragile.** It works on websearch output because lexemes never contain spaces, but it turns `a -b c` into `!'b' | 'c'`, which matches almost every row. It also relies on an undocumented text form. ([tsquery.c](https://github.com/postgres/postgres/blob/REL_17_STABLE/src/backend/utils/adt/tsquery.c))
- **Building from lexemes with `quote_literal` corrupts backslashes.** Re-parsing lexemes through `to_tsquery` also re-expands hyphenated words.
- **Robust approach:** tokenize in the application into phrases, words and exclusions. Build each clause server-side with `plainto_tsquery` or `phraseto_tsquery` (no quoting needed), drop empty stopword clauses with `numnode(q) > 0`, and OR the clauses together. Exclusions are ANDed as `!!clause`.
- **Under OR, `ts_rank_cd` loses its proximity signal:** each cover is a single occurrence, so "cat cat cat cat" outscored "cat dog" (5.6 vs 2.8). `ts_rank`'s OR path saturates repeats, and "cat dog" won 0.669 to 0.445. Neither has IDF. ([tsrank.c](https://github.com/postgres/postgres/blob/REL_17_STABLE/src/backend/utils/adt/tsrank.c), [docs](https://www.postgresql.org/docs/17/textsearch-controls.html))
- **Weighting quirk.** Connapse's `search_vector` concatenates a `simple` copy (weight A = 1.0) and an `english` copy (weight B = 0.4). A stemmed query term hits the A copy only when its stem equals its surface form ("calcium" does; "intake" → "intak" does not), so words are weighted arbitrarily. Ranking with weights `{0,0,1,0}` (D,C,B,A) counts only the english copy.
- **Performance.** An OR query with a common term becomes a scan that ranks every matching row (58 ms on 200k small rows versus 0.03 ms for the strict AND). The container filter bounds this today. BM25 extensions with Block-Max WAND solve it properly.
- **Minimum-should-match** has no native operator. It can be emulated with a per-term count recheck, at extra cost.

### 3. Empirical evidence
Any-term matching is the baseline for BM25 first-stage retrieval. The remaining gaps are ranking problems, not matching problems.
- **Anserini/Pyserini baselines are OR.** Their BM25 baselines add every query term as a SHOULD clause. ([BagOfWordsQueryGenerator](https://github.com/castorini/anserini/blob/master/src/main/java/io/anserini/search/query/BagOfWordsQueryGenerator.java))
- **Reference BM25 on full BEIR (nDCG@10):** MS MARCO about 0.228, FiQA about 0.236, SciFact about 0.665 (Thakur et al., NeurIPS 2021, [arXiv](https://arxiv.org/abs/2104.08663)). These are full-corpus numbers and are not directly comparable to NanoBEIR.
- **IDF and length normalization track effectiveness closely.** Fang, Tao & Zhai (SIGIR 2004) showed this formally ([pdf](https://timan.cs.illinois.edu/czhai/pub/sigir04-formal.pdf)). A snippet reports `ts_rank` at 0.07 against BM25 at 0.69 on SciFact; this is unverified.
- **Phrases work as scoring signals.** The Sequential Dependence Model (Metzler & Croft, SIGIR 2005) scores them as boosts. No evidence was found for phrases as hard filters.
- **AND vs OR.** Asadi & Lin (SIGIR 2013) found conjunctive and disjunctive candidate generation similar end-to-end, but that was on 2–3-term web queries with learning-to-rank on top. It does not carry over to question-length queries.

### 4. BM25 extension for #545
**pg_textsearch** is the spike target.
- **pg_textsearch (Tiger Data):** PostgreSQL License, GA (v1.4.0, 2026-08-18), PG17/18, OR-ranked `<@>` operator with Block-Max WAND, concurrent writes, partial indexes. ([repo](https://github.com/timescale/pg_textsearch))
- **Risks to test in the spike:**
  - IDF statistics are computed per index, so they would span all containers unless each container gets its own partial index.
  - The extension has to be preloaded in a custom image built on `pgvector/pgvector:pg17`.
  - Phrases need a post-filter, because the index stores no positions.
- **ParadeDB pg_search:** AGPL, still 0.x, and physical replication is Enterprise-only.
- **VectorChord-bm25:** AGPL or ELv2, pre-1.0, with development stalled since April 2026.

## Conflicts and uncertainties
- **Quoted phrases: strict or optional?** The Postgres researcher advised keeping strict `websearch_to_tsquery` whenever a query contains quotes, because per-word OR loses phrases. The engine survey found phrases are optional clauses everywhere. **Resolved:** building a phrase clause with `phraseto_tsquery` keeps the phrase intact as one OR alternative, so both concerns are met.
- **Why MS MARCO regressed** is an inference (no IDF, with AND having stood in for it). It was not isolated experimentally.
- **Minimum-should-match:** no evidence was found for a default above one term in RAG pipelines. One tertiary product-search experiment found all-terms matching works better as a ranking boost than as a filter.

## Gaps — what we did not find
- Published BM25 reference scores for NanoBEIR.
- Any peer-reviewed comparison of minimum-should-match settings for RAG.
- Whether pg_textsearch installs cleanly on the pgvector image.
- A verified `ts_rank` vs BM25 benchmark on BEIR.

## Source quality assessment
Most claims rest on primary sources: official docs, source code, and papers in peer-reviewed venues. The Postgres behaviours were reproduced on pg17. The MS MARCO explanation, the `ts_rank` SciFact number, and the minimum-should-match experiment are secondary or tertiary and marked as such.
