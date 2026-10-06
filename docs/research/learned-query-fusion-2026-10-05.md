# Can a model trained on per-query "best fusion weight" labels improve Connapse's hybrid search?
**Date:** 2026-10-05
**Status:** Reviewed
**Built on:** hybrid-fusion-improvements-2026-09-27.md, adaptive-fusion-feedback-2026-09-28.md, enterprise-retrieval-weaknesses-2026-10-05.md

## Executive summary
Training a model on per-query oracle labels (the fusion weight that ranked the known-relevant documents best) is the obvious idea, and it has been tried. No study found a learned per-query predictor that beats a well-tuned fixed weight on queries from a domain it was not trained on.

The ~20% oracle headroom is mostly an artefact:
- It picks the best of about 11 noisy, highly correlated outcomes per query, using the answers.
- With one relevant document per query, nDCG@10 for that query is close to a step function.
- In FinDER, 77.6% of queries cannot be changed by any weight, so the headroom sits in a small minority of queries.

What decides the right weight is how the unseen relevant document is worded. Features computed from the query alone barely predict it.

The learned approaches that did win were:
- trained and tested in the same domain, on thousands of queries;
- built on features of the retrieved result lists (for example, how much the keyword and vector lists overlap);
- trained end to end on the ranking metric with soft weights, not by predicting oracle labels.

Once a cross-encoder reranker is on, the first-stage fusion barely matters: about 0.003 nDCG@10 in HYRR.

**Recommendation:** ship the reranker. Then run the decomposition protocol below on Connapse's own captured scores. Build a learned gate only if the three-class oracle headroom survives that protocol on held-out datasets.

## Why oracle labels don't produce a working predictor
This section explains the gap between ~20% oracle headroom and 0–3% recovered.

1. **Selection bias.** Taking the maximum over configurations, scored on the same labels, is biased upward (Varma & Simon 2006; Cawley & Talbot 2010; cited from memory). Many queries have flat or tied curves over α, and the oracle cashes in small rank flips.
2. **Sparse labels.** 94% of MS MARCO dev queries have one judged-relevant passage, and rankers' unjudged top results are often preferred over it. Some oracle "gain" just reshuffles equally good documents (Arabzadeh et al., IRJ 2022, [arXiv 2109.00062](https://arxiv.org/abs/2109.00062)).
3. **Signal isn't in the query.** FinDER's query-only random forest gained +0.0000 Hit@10. Adaptive Re-Ranking found query complexity uncorrelated with the right route (Cramér's V 0.03). Chifu et al. found QPP predictors (query performance prediction: estimates of how well a ranker will do on a query) correlate near zero or negatively on MS MARCO, and don't generalise across collections ([arXiv 2504.01101](https://arxiv.org/abs/2504.01101), TOIS 2025).
4. **Domain shift and sample size.**
   - FinDER grouped its cross-validation by company, so test companies were never seen in training. Under that protocol, ridge regression on the query embedding gained +0.0022 Hit@10 (p = 0.056) against an oracle of +21.8% ([arXiv 2608.00183](https://arxiv.org/html/2608.00183)).
   - Detecting a 0.02 nDCG gain needs about 440 queries at a per-query spread σ = 0.15 (Sakai, IRJ 2016). Every win in the literature used 2.3k–500k in-domain queries.

## What has worked, and under which conditions
This section lists the learned fusion results that did succeed.

| Study | What won | Why it doesn't transfer to Connapse Cloud |
|---|---|---|
| LambdaMerge (Sheldon et al., WSDM 2011) | Soft gating over result lists, using list-overlap and score-statistic features, trained end to end on NDCG. Matched the selection oracle (0.555 vs 0.556) | In-domain Bing logs, 2.3k training queries |
| LTRR ([arXiv 2506.13743](https://arxiv.org/html/2506.13743v1)) | XGBoost over results-list features (cross-retriever similarity) beat the best single retriever | One corpus; target was answer correctness; not compared with tuned α |
| OpenSearch blog (vendor) | Per-query random-forest α from result-set features: +7.4% nDCG@10 on ESCI | Single product-search domain |
| Arabzadeh et al. CIKM 2021 | Sparse/dense routing classifier | Efficiency result; MS MARCO only |

The failures are the out-of-domain tests:
- FinDER under grouped cross-validation;
- Chifu et al. across collections;
- Adaptive Re-Ranking on held-out BEIR sets, between −17.5% and +4.0% nDCG@10;
- DS@GT's LambdaMART fusion on TREC Tip-of-the-Tongue, where reciprocal rank fell from 0.28 to 0.06 on the test set ([arXiv 2601.15518](https://arxiv.org/html/2601.15518v1)).

**Pattern.** Wins use results-list features and soft weights in-domain. Query-only features and regression onto oracle α have no demonstrated win.

## The reranker makes fusion weighting nearly moot
This section covers how much first-stage fusion matters once a cross-encoder reranks the candidates.

- **HYRR** ([arXiv 2212.10528](https://arxiv.org/html/2212.10528)): one reranker on BEIR scored 0.504 on BM25 candidates, 0.506 on hybrid and 0.507 on dense, a spread of about 0.003.
- **Askari et al.** (ECIR 2023): mixing the BM25 score linearly into the cross-encoder's score hurt (MRR@10 fell from 0.360 to 0.290).
- **Implication:** with the reranker on, α matters only through recall at the rerank depth. That effect is small.
- **No learned per-candidate fusion test exists.** No published study trains learning-to-rank (a model that scores each candidate) over BM25 and dense features and tests it zero-shot against tuned convex fusion. This is a gap, not a proof.

## Protocol to measure Connapse's achievable headroom
This section is a cheap, offline test on captured per-candidate (vector score, keyword score) data. It uses the enterprise run with `captureCandidates` and a fresh post-#561 dev-suite capture. #561 fixed nomic lowercasing, so the 2026-09-28 capture is unusable.

1. **Per-query curves.**
   - For each query, compute nDCG@10 at α = 0, 0.1, …, 1.
   - Regret is the per-query best minus the score at the global fixed α*; the mean regret is the raw headroom.
2. **Indifferent queries.**
   - A query is indifferent if its curve varies by no more than 0.01.
   - Its acceptable set is the α values within 0.05 of its best. A query whose acceptable set already contains α* has nothing to learn.
   - Report what share of the headroom the remaining queries carry.
3. **Noise floor.**
   - Recompute the oracle under small score jitter.
   - Recompute it over grids of 2, 3, 5 and 11 α values. Gain that keeps growing with grid size is a selection artefact; gain that saturates at three coarse values is structural.
4. **Ceilings.**
   - Per-dataset best α.
   - A three-class oracle: label each query keyword-lean, dense-lean or indifferent, and give each class its best α.
   - A shuffled-label null: train the predictor on labels shuffled within each dataset to see what no real signal appears to gain.
5. **Learnability.**
   - Train a soft gate, LambdaMerge-style, on results-list features: list overlap, each side's score gap, top-score spread.
   - Validate leave-one-dataset-out, with α* re-tuned without the held-out dataset.

**Bar to ship:**
- At least +2% relative pooled held-out nDCG@10 over the re-tuned fixed α, with a 95% paired-bootstrap interval that excludes 0.
- Beats the per-dataset ceiling and the shuffled-label null.
- No held-out dataset loses significantly in a risk-sensitive test (one that penalises per-query losses against the baseline).
- Run it twice, with and without the reranker. If the three-class headroom after step 3 is under about 3%, stop.

## Conflicts and uncertainties
- **Per-dataset tuning.** Elastic and Bruch et al. find that the best α varies by dataset and is cheap to tune per domain, given labels. Connapse Cloud has no labels and no admin tuning, so that route is closed for Cloud and open only for self-hosted opt-in.
- **Single sources.** Adaptive Re-Ranking and the OpenSearch result are each single-source. The selection-bias papers were cited from memory.
- **Untuned baselines.** FinDER's baseline was an untuned α = 0.5. Only DAT (LLM-chosen α, [arXiv 2503.23013](https://arxiv.org/abs/2503.23013)) compared against a grid-tuned α, and only in-domain.

## Gaps
- No zero-shot evaluation of learned per-candidate fusion against tuned convex fusion.
- No study of adaptive fusion on enterprise-style corpora.
- No measurement yet on Connapse's own data. The protocol above fills that.

## Sources
**Primary:**
- arXiv 2608.00183 (FinDER)
- arXiv 2504.01101 (Chifu et al., TOIS 2025)
- arXiv 2109.10739 (Arabzadeh et al., CIKM 2021)
- arXiv 2109.00062 (Arabzadeh et al., IRJ 2022)
- arXiv 2506.13743 (LTRR)
- Sheldon et al., WSDM 2011 (LambdaMerge)
- arXiv 2212.10528 (HYRR)
- arXiv 2301.09728 (Askari et al.)
- arXiv 2210.11934 (Bruch et al.)
- arXiv 2104.08663 (BEIR)
- arXiv 2601.15518 (DS@GT)
- Sakai, IRJ 2016
- Webber, Moffat & Zobel, CIKM 2008

**Preprint / single source:**
- arXiv 2606.25249 (Adaptive Re-Ranking)
- arXiv 2609.14885 (QueryRoute)
- arXiv 2305.18311 (Mothe & Ullah)
- arXiv 2503.23013 (DAT)

**Vendor:**
- OpenSearch blog (June 2025)
- Elastic Search Labs
- Vespa blog
- sbert.net model table

## Measured on Connapse (enterprise-v1, 2026-10-05)
Offline replay of convex fusion from the captured candidates of run 20261005-190045-f3605ab (470 questions, pool 30 per side). The replay reproduces the live run exactly: α = 0.75 gives nDCG@10 0.565.

| Measure (first stage, no reranker) | nDCG@10 |
|---|---|
| Fixed α 0.75 (current default) | 0.565 |
| Best fixed α (0.4) | 0.635 |
| Per-query oracle, 11-value grid | 0.703 (+10.6% over best fixed) |
| Noise-only "oracle": best of 10 score-jittered rankings at α 0.4 (sd 0.02) | 0.675 (+6.3%) |
| Three-class oracle (keyword-lean / dense-lean / indifferent) | 0.690 (+8.5%) |
| Oracle choosing only between α 0 and α 1 | 0.665 |

**Per-query headroom:**
- 75% of queries have the best fixed α within 0.05 of their own best, and 40% have flat curves.
- Random jitter alone captures about 60% of the oracle gain, so a learnable per-query signal is at most about 4%. In practice it is less, given the literature above.

**Fixed weight:**
- The larger effect is the fixed weight itself: 0.4 beats the default 0.75 by +0.07 on enterprise data.
- With the reranker on, what matters is gold recall in the 30-chunk rerank pool:

| α | 0.3 | 0.4 | 0.5 | 0.6 | 0.7 | 0.8 | 1.0 |
|---|---|---|---|---|---|---|---|
| Gold recall in the rerank pool | 0.805 | 0.804 | 0.799 | 0.795 | 0.769 | 0.749 | 0.652 |

**Next measurement:** BEIR's dev suite preferred 0.75 without the reranker (#552). The robust zero-configuration choice is the α that maximises rerank-pool recall across all suites at once. That needs a fresh dev-suite capture run, because the 2026-09-28 capture predates #561.

## Confirmed with the reranker (2026-10-06)
Live runs, all with gte-reranker-modernbert-base; `compare` against the run in the previous row:

| Config | enterprise-v1 | dev (8 BEIR) | Significance |
|---|---|---|---|
| Hybrid, α 0.75, no reranker (old default) | 0.565 | 0.608 | — |
| + reranker, top 30 | 0.660 | 0.635 | Both improve; no significant drop on any dataset |
| + α 0.65 | **0.677** | 0.634 | Enterprise +0.017, significant; dev unchanged, no drops |
| + 50 per side, rerank 50 | 0.693 | 0.637 | Dev: NFCorpus drops significantly (0.329 → 0.314); FiQA +0.04 |

Splitting the two changes on dev attributes the NFCorpus drop to rerank depth 50, not to α:
- α 0.65 alone: dev 0.634, no significant change on any dataset.
- Depth 50 alone: dev 0.640, NFCorpus significantly down.

The recall proxy predicted depth 50 would help everywhere. It doesn't: on NFCorpus the reranker promotes plausible wrong documents from the larger pool.

**Recommended zero-configuration default:** reranker on, α 0.65, rerank depth 30. This is no worse on any dev dataset, +0.112 on enterprise and +0.026 on dev overall. Depth 50 needs a reranker that holds up on NFCorpus-like text before it can be a default.

Shipped in #669 as α 0.65, with the reranker left as a user choice. The v1 test-suite re-check was deferred: the decision on #668 rests on the dev and enterprise results above.
