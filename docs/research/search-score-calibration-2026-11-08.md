# How should a hybrid search engine report scores and say "nothing matched"?
**Date:** 2026-11-08
**Status:** Reviewed
**Built on:** no prior corpus material (CON-278 findings in ClaudePM `docs/connapse-findings.md` set the question)

## Executive summary
No search vendor treats a fused hybrid score as an absolute relevance value: min-max fusion, which Connapse uses,
puts the top hit at 1 by construction, and every vendor that offers a "no good match" cut-off applies it to an
unnormalized signal (raw vector similarity, a reranker score) instead. The literature agrees that a raw top score
is a weak abstention signal on its own and that the shape of the score list (the gap from the top hit to the rest)
adds real information. Raw cosine ranges differ widely between embedding models, so any fixed threshold must be
measured per model or made relative to a background. Calibrated probabilities need labelled data, which a
self-hosted product does not have. The sporadic 3–5 s searches match Ollama unloading an idle model after five
minutes, which the `/api/embed` `keep_alive` field prevents.

## Research brief
**Question:** what should Connapse report as a search score, how should it signal that nothing matched, and why
are some searches slow?
**Sub-questions:** (1) what vendors do with hybrid scores and thresholds; (2) what the literature says about
query performance prediction and abstention; (3) how embedding-similarity ranges behave across models; (4) why a
small-container search can take seconds with Ollama.
**Out of scope:** changing the ranking itself; training a calibration model.
**Success criteria:** a design that can be measured with a pass bar fixed in advance.

## Findings by sub-question
### Vendors: fused hybrid scores are relative, thresholds go on raw signals
- **Azure AI Search** says hybrid scores are "not conducive to thresholds" and puts filtering on the vector leg;
  its semantic reranker score runs 0–4 with documented meaning per band (primary).
- **Elastic** recommends `min_score` on the semantic leg before fusion, and notes min-max makes the top hit 1
  (primary).
- **OpenSearch** normalized scores depend on how many results came back; it added `lower_bounds` so a weak result
  set is not stretched to fill 0–1 (primary).
- **Weaviate** `relativeScoreFusion` is min-max; its distance threshold applies only to vector distance, and it
  offers `autocut` on score jumps (primary).
- **Qdrant** DBSF normalizes per query; `score_threshold` applies to raw similarity (primary).
- **Cohere** rerank scores are query dependent; its docs give a recipe for picking a threshold from labelled
  examples (primary).

### Literature: shape of the score list beats its top value
- "Magnitude Mirage" (EMNLP 2026) finds thresholding the raw top score fails for abstention, and adding the top-1
  minus top-k gap and score variance raises abstention AUROC by up to 0.16 (primary, single study).
- Query performance predictors such as NQC (Shtok et al. 2012) and WIG (Zhou & Croft 2007) use the same
  score-distribution idea; they work less well on dense retrievers (Faggioli et al. 2023; 2025 follow-up) (primary).
- Score-distribution models for relevant and non-relevant documents go back to Manmatha et al. (SIGIR 2001)
  (primary).
- Platt scaling and isotonic regression (Niculescu-Mizil & Caruana 2005) and temperature scaling (Guo et al. 2017)
  need labelled pairs; ECE and Brier only make sense when a score is claimed to be a probability (primary).
- Kochergin & Konovalov (2026) show that "no gold document" labels understate how many questions are answerable,
  so a foreign-query test set has label noise (primary).

### Embedding similarity ranges differ by model
- Contextual embeddings are anisotropic: unrelated texts already have high cosine (Ethayarajh 2019, primary).
- Unrelated text scores about 0.75–0.88 with ada-002 but about 0.2–0.4 with text-embedding-3-small (Fox 2025,
  secondary).
- Length bias moves cosine for the same relevance (Jina, secondary), and InfoNCE-trained scores are not
  comparable across queries (2025 preprint, primary).
- A background-relative score (z-score against unrelated text) removes most model dependence (GNAT, single model,
  primary).

### Ollama unloads idle models
- Ollama keeps a model loaded for 5 minutes by default (`OLLAMA_KEEP_ALIVE`); the next request reloads it
  (primary).
- `/api/embed` accepts `keep_alive` per request and returns `load_duration` (primary; added in ollama PR #2146).

## Conflicts and uncertainties
- The gap-feature gain rests on one recent study; Connapse must measure it on its own suites.
- Cross-model robustness of any cosine-based rule is unproven here; the GNAT result covers one model.

## Gaps — what we did not find
- No public benchmark of abstention for hybrid (BM25 + dense) fusion specifically.
- No vendor guidance for a zero-label, any-embedding-model, self-hosted setting.

## Source quality assessment
Mostly primary: vendor documentation and peer-reviewed papers. The cosine-range figures come from one
well-documented blog post.

## Sources
**Primary**
- https://learn.microsoft.com/en-us/azure/search/vector-search-how-to-query
- https://learn.microsoft.com/en-us/azure/search/semantic-search-overview
- https://www.elastic.co/search-labs/blog/semantic-precision-minimum-score
- https://docs.opensearch.org/latest/search-plugins/search-pipelines/normalization-processor/
- https://docs.weaviate.io/weaviate/search/hybrid
- https://docs.weaviate.io/weaviate/api/graphql/additional-operators
- https://qdrant.tech/documentation/concepts/hybrid-queries/
- https://docs.cohere.com/docs/reranking-best-practices
- https://arxiv.org/abs/2609.15578 (Magnitude Mirage)
- https://arxiv.org/abs/2302.09947 and https://arxiv.org/html/2504.01101v1 (QPP for dense retrievers)
- Manmatha, Rath & Feng, "Modeling score distributions for combining the outputs of search engines", SIGIR 2001
- Niculescu-Mizil & Caruana, "Predicting good probabilities with supervised learning", ICML 2005
- https://arxiv.org/abs/1706.04599 (Guo et al., calibration)
- https://arxiv.org/abs/2610.40966 (Kochergin & Konovalov)
- https://arxiv.org/abs/1909.00512 (Ethayarajh, anisotropy)
- https://arxiv.org/pdf/2510.00137 (InfoNCE score comparability)
- https://arxiv.org/pdf/2311.03627 (GNAT)
- https://docs.ollama.com/faq, https://docs.ollama.com/api/embed, https://github.com/ollama/ollama/pull/2146

**Secondary**
- https://blog.pamelafox.org/2025/05/a-visual-exploration-of-vector.html
- https://jina.ai/news/on-the-size-bias-of-text-embeddings-and-its-impact-in-search/
