# How should Connapse improve hybrid-search fusion now that keyword search is strong?
**Date:** 2026-09-27
**Status:** Reviewed
**Built on:** retrieval-eval-github-sources-2026-09-24.md, bm25-sql-latency-parity-2026-09-27.md; issue #552 results (α 0.1 tuned on RAGBench dev lost on the full suite: 0.595 vs 0.602 nDCG@10). Connapse knowledge-base tools were not available in this session.

## Executive summary
Connapse's fusion design is already the one the evidence favours: a convex combination of min-max-normalized scores, with every pooled candidate scored on both sides.

- **Normalization and fusion functions are second-order.** Once α is tuned, normalizations are nearly interchangeable, and changing them moves nDCG@10 by only about ±3%.
- **Per-query weighting has large theoretical headroom but little captured gain.** An oracle choosing α per query gains about 20%. Published cheap signals capture about 0–3% zero-shot, and two careful studies found no significant gain.
- **The main problem is tuning data.** Connapse's only dev data is one domain, which is why #552's tuning backfired. Seven BEIR datasets have official dev queries disjoint from the NanoBEIR test queries, enough for a multi-domain dev suite of about 600–900 queries.

**Recommendation:**
1. Build that dev suite.
2. Add two cheap, low-risk static changes: a fixed lower bound in normalization and a larger candidate pool.
3. Test one simple query-adaptive α rule (query IDF + length) with leave-one-domain-out validation. Tune for recall@30 when the reranker is on.

## Research brief
**Question:** How should the fusion step of Connapse's hybrid search improve, given BM25 keyword search and nomic dense vectors, a fixed α = 0.3, and hybrid trailing keyword alone on 6 of 16 eval datasets?

**Sub-questions:**
1. Fusion functions and normalization (2023–2026 evidence).
2. Query-adaptive weighting without an LLM per query.
3. What production engines do.
4. How to tune without overfitting.

**Out of scope:** changing the embedding model or the reranker model.

**Success criteria:** a ranked, evidence-backed plan with a tuning protocol.

## Findings by sub-question

### 1. Fusion functions and normalization: keep the convex combination, fix the lower bound
The convex combination beats rank-based fusion (RRF) consistently. Normalization choice matters little when α is tuned, but a fixed lower bound is a cheap, evidenced gain.

**Convex combination vs RRF**
- **Bruch, Gai & Ingber, "An Analysis of Fusion Functions for Hybrid Retrieval"** (ACM TOIS 2023, primary). The convex combination beat RRF (η = 60) in every domain tested:
  - MS MARCO 0.454 vs 0.425
  - NQ 0.542 vs 0.514
  - Quora 0.901 vs 0.877
  - HotpotQA 0.699 vs 0.675
  - FEVER 0.744 vs 0.721
  - SciFact 0.753 vs 0.730
  
  These are nDCG@1000 with a 1000-deep pool and every candidate scored on both sides, as Connapse does. RRF's tuned constants did not transfer out of domain.
- **Other corroboration** that score fusion beats RRF:
  - OpenSearch: RRF scored 3.86% lower on six BEIR sets.
  - Weaviate: about 6% higher recall on FiQA.
  - Vespa: `normalize_linear` scored 0.342 against RRF's 0.323 on NFCorpus.
  - T2-RAGBench (2026): recall@5 of 0.726 against 0.695–0.716 for RRF.
  - Connapse's own #521 finding.
- **The exception.** A 2025 BRIGHT study (arXiv 2509.02558, preprint) found RRF safer when one retriever is much stronger than the other. That matches Connapse's six keyword-wins datasets.

**Normalization**
- **Interchangeable once α is tuned.** Min-max, z-score and TMM (theoretical minimum with the observed maximum) are linear rescalings; Bruch showed a rank-equivalent convex combination exists under each. With one global α across 16 domains, though, normalization shifts the effective per-query weight.
- **A fixed lower bound helps.** An OpenSearch RFC (neural-search #1189) found that setting a fixed lower bound instead of the observed minimum raised nDCG@10 by about 3.5% on average; TREC-COVID went from 0.600 to 0.693. For Connapse that means 0 for BM25 and the cosine floor (−1, or 0 in practice) for vectors, while keeping the observed maximum.
- **Z-score is inconclusive.** It gained +2.08% in OpenSearch's development tests but lost 0.53% in production, and OpenSearch concluded min-max is more reliable.
- **DBSF has no BEIR-scale comparison.** The only data point is AutoRAG on 107 queries.
- **No evidence for a theoretical BM25 maximum.** It is also risky: it would squeeze keyword scores on long queries, which are exactly the datasets where keyword wins.

**Pool size**
Connapse pools 30 per side, where Bruch used 1000. No study measures pool size for score fusion, but a small pool makes the observed minimum and standard deviation noisy, which is the mechanism behind RFC #1189.

### 2. Query-adaptive weighting: big headroom, small captured gains
Choosing α per query can gain a lot in principle, but the cheap published methods deliver about 0–3%, and two careful studies found none.

**Evidence for it**
- **DAT** (arXiv 2503.23013, 2025): an LLM grades each side's top-1 document and α is set from the grades. P@1 gains of 2.8–3.3 points on SQuAD and DRCD, and about 6–7.5 points on queries where the two sides disagree. It costs one LLM call per query, and it was not tested on BEIR.
- **vstash** (arXiv 2604.15484, 2026): a sigmoid of mean query IDF sets the RRF weights. BEIR nDCG@10 gains: SciFact +0.1%, NFCorpus +1.8%, SciDocs +1.7%, FiQA +3.4%. Its ArguAna +21% came from a query-length cutoff, not IDF. This is the only cross-domain ablation of a cheap rule.
- **MoR** (CMU, arXiv 2506.15862, 2025): training-free weights from query-to-corpus distance and the coherence of retrieved results. NDCG@20 of 58.7 against 54.2 for RRF, on science datasets only, with a hand-set blend.
- **LTRR** (SIGIR 2026): a learned XGBoost router with query and post-retrieval features, +2.1% (significant). Train-free heuristics were not significant.

**Evidence against it**
- **FinDER** (arXiv 2608.00183, 2026): the per-query oracle beat fixed fusion by 21.8% Hit@10, but heuristic, random-forest and ridge routers gained nothing significant under grouped cross-validation.
- **Query performance prediction** (Chifu et al., ACM TOIS 2025): predictor accuracy is collection-dependent, and QPP-driven selective processing gives "only marginal gains".

**Coverage gap:** no study covers the datasets where Connapse's hybrid trails keyword (DBpedia, FEVER, HotpotQA, Touché).

### 3. Production engines: RRF by default, score fusion as the quality option
Most engines default to RRF (k = 60) because it needs no tuning. Every vendor that published benchmarks shows score fusion beating RRF by roughly 1–8% nDCG@10 once weighted.

| Engine | Default fusion | Details |
|---|---|---|
| Azure AI Search | RRF | The only option; k = 60 |
| Elasticsearch | RRF | A `linear` retriever with minmax or l2 normalizers is available |
| OpenSearch | Min-max + arithmetic mean | z-score added in 3.0; RRF added in 2.19 |
| Weaviate | relativeScoreFusion (min-max) | Default since v1.24; α 0.75 toward vector |
| Vespa | — | Hybrid expressions in any phase; `normalize_linear` scored best in its tutorial |
| Qdrant | — | RRF (k = 2) and DBSF |
| Milvus | — | Weighted (arctan normalization) or RRF |
| Supabase, Tiger Data, ParadeDB | RRF | — |

All sources are official docs, verified 2026-09-27. Two points apply to Connapse:
- **Normalization is always over the query's own candidate window,** and cross-encoders always rerank after fusion.
- **Connapse's design matches the "quality" camp** of OpenSearch, Weaviate, Elastic `linear` and Vespa. It scores missing candidates on both sides, which is more thorough than the field. It lacks an RRF option, even as a baseline.

### 4. Tuning without overfitting: build a multi-domain dev suite
The failure in #552, tuning on one domain, is avoidable. BEIR provides dev queries for seven domains that are disjoint from the NanoBEIR test queries.

**Official dev queries.** Counted by distinct query ID in the HF `BeIR/<name>-qrels` files, and checked to have zero overlap with each dataset's test queries:

| Dataset | Dev queries |
|---|---|
| MS MARCO | 6,980 |
| HotpotQA | 5,447 |
| FEVER | 6,666 |
| Quora | 5,000 |
| FiQA | 500 (plus 5,500 train) |
| NFCorpus | 324 |
| DBpedia | 67 |
| SciFact | train only, 809 |

- **Trap: NanoMSMARCO's 50 test queries come from MS MARCO dev.** Exclude those IDs.
- **Quora** needs text-level deduplication, because its query IDs are disjoint but texts may repeat.
- **NanoBEIR** itself has no dev split.

**Methodology**
- **BEIR's convention** is to tune once and apply zero-shot.
- **Bruch** tuned α on the dev splits of MS MARCO, Quora and NQ jointly (one α worked for all three), and found α converges within a domain from under 5% of its train split.
- **Leave-one-domain-out selection** (Gulrajani & Lopez-Paz, ICLR 2021) and worst-group objectives (Group DRO, ICLR 2020) are the standard guards against overfitting.
- **Anserini** averages parameters chosen on bootstrap samples, as regularization.

**Reranker interaction**
- Anserini tunes the first stage for recall when a reranker follows.
- Rosa et al. (arXiv 2212.06121) found BM25 and GTR converge to 0.496 nDCG@10 after monoT5 reranking.
- Jacob et al. (arXiv 2411.11767) found cross-encoders degrade with deeper lists.
- So with the reranker on, tune fusion for recall@30 (the rerank depth). Two α values may be warranted: one with the reranker, one without.

**Statistics**
- **Wilcoxon signed-rank or a sign test over datasets** (Demšar, JMLR 2006; Soboroff, CIKM 2018) for the overall decision. A sign test needs 13 of 16 wins for p < 0.05.
- **Keep per-dataset Holm tests** for diagnosis. The minimum detectable effect at 50 queries is about 0.04 nDCG@10 (σ_d = 0.10, 80% power), so most real gains of 1–3% are invisible per NanoBEIR dataset. Only CQADupStack (876 queries) is well powered.

## Recommended plan (for #552)
1. **Build a multi-domain dev suite.** Use 50–100 dev queries from each of MS MARCO (excluding the NanoMSMARCO IDs), HotpotQA, FEVER, Quora, FiQA, NFCorpus, DBpedia and SciFact (train), plus the RAGBench dev splits, for about 10 domains and 600–900 queries. Build Nano-style pooled corpora (the top-100 BM25 and dense results plus the judged relevant documents) and remove the NanoBEIR test documents. Everything else depends on this.
2. **Make cheap static changes, evaluated on the dev suite first:**
   - a fixed lower bound (TMM: BM25 minimum 0, cosine minimum at its floor, observed maximum);
   - a pool of 100 per side instead of 30;
   - an RRF option as a baseline.
3. **Re-select the global α on the dev suite.** Use macro nDCG@10 without the reranker and macro recall@30 with it, prefer the best worst-domain score among near-ties, and average over bootstrap samples. Leave-one-domain-out tells whether one α is stable: if the chosen α moves more than about 0.2 across folds, that justifies adaptive weighting.
4. **Test one query-adaptive rule:** α(q) = σ(a − b·meanIDF_norm + c·log len), clipped to [0.1, 0.5], fitting only a, b and c on the dev suite with leave-one-domain-out. Expect about +1–3% if it works. Score-confidence gating and a small-model DAT-lite are follow-ups only if this works.
5. **Accept only after one pass on the 16-dataset test suite** with no macro loss, no significant Wilcoxon loss across datasets, and no Holm-significant regression. CQADupStack must not be worse.

## Conflicts and uncertainties
- **Does normalization matter?** Bruch says normalizations are interchangeable under tuned α, while the OpenSearch RFC shows +3.5% from a fixed lower bound. **Resolved:** the interchangeability holds only when α is tuned per domain, which one global α across 16 domains is not.
- **Is query-adaptive weighting worth it?** vstash, MoR and DAT show gains; FinDER and the TOIS 2025 QPP study show none significant. The positive results are preprints on few or narrow datasets. **Treat as unproven for Connapse** until the leave-one-domain-out test says otherwise.
- **Z-score:** positive in OpenSearch's development tests, negative in production.
- **Construction of the NanoBEIR corpora** (the top-100 pooling) is from community descriptions, not an official write-up.

## Gaps — what we did not find
- A BEIR-scale comparison of DBSF against min-max.
- Any evaluation of a theoretical BM25 maximum for normalization.
- Pool-size studies for score fusion.
- Query-adaptive results on DBpedia, FEVER, HotpotQA or Touché.
- A learned fusion model trained on MS MARCO with per-dataset zero-shot BEIR results against a tuned fixed α.
- Whether BEIR's `nq-train` overlaps the NQ test queries.

## Source quality assessment
- **Peer-reviewed:** Bruch (TOIS 2023), Chifu (TOIS 2025), LTRR (SIGIR 2026), BEIR (NeurIPS 2021), and the statistics and domain-generalization references.
- **Official vendor docs and blogs:** the production-engine facts; the benchmark numbers in vendor blogs are secondary.
- **Preprints (lower confidence):** most 2025–2026 query-adaptive results (DAT, vstash, MoR, FinDER).
- **Verified directly:** the dev-split counts and the absence of overlap, from the HF qrels files.
- **From memory, not re-fetched:** Gulrajani & Lopez-Paz, Group DRO, Demšar.

## Sources
**Primary:**
- https://arxiv.org/abs/2210.11934
- https://arxiv.org/abs/2104.08663
- https://arxiv.org/abs/2504.01101
- https://arxiv.org/abs/2506.13743
- https://github.com/beir-cellar/beir
- https://ir-datasets.com/nano-beir.html
- https://www.elastic.co/docs/reference/elasticsearch/rest-apis/retrievers/linear-retriever
- https://docs.opensearch.org/latest/vector-search/ai-search/hybrid-search/rrf/
- https://docs.vespa.ai/en/ranking/phased-ranking.html
- https://docs.weaviate.io/weaviate/concepts/search/hybrid-search
- https://qdrant.tech/documentation/concepts/hybrid-queries/
- https://milvus.io/docs/weighted-ranker.md
- https://learn.microsoft.com/en-us/azure/search/hybrid-search-ranking
- https://supabase.com/docs/guides/ai/hybrid-search
- https://github.com/castorini/anserini/blob/master/docs/experiments-msmarco-passage.md
- https://dl.acm.org/doi/10.1145/3269206.3271719

**Secondary and preprints:**
- https://arxiv.org/abs/2503.23013
- https://arxiv.org/html/2604.15484v1
- https://arxiv.org/abs/2506.15862
- https://arxiv.org/abs/2608.00183
- https://arxiv.org/html/2509.02558
- https://arxiv.org/html/2604.01733v1
- https://arxiv.org/abs/2212.06121
- https://arxiv.org/abs/2411.11767
- https://github.com/opensearch-project/neural-search/issues/1189
- https://opensearch.org/blog/introducing-the-z-score-normalization-technique-for-hybrid-search/
- https://opensearch.org/blog/introducing-reciprocal-rank-fusion-hybrid-search/
- https://weaviate.io/blog/hybrid-search-fusion-algorithms
- https://www.elastic.co/search-labs/blog/improving-information-retrieval-elastic-stack-hybrid
- https://github.com/cognica-io/bayesian-bm25
