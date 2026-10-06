# Why Connapse retrieves poorly on enterprise data, and what would fix it in general
**Date:** 2026-10-05
**Status:** Reviewed
**Built on:** retrieval-eval-github-sources-2026-09-24.md, embedding-instruction-prefixes-2026-09-28.md, vector-recall-tradeoff-2026-09-29.md (Connapse knowledge base not available in this session)

## Executive summary
Connapse's hybrid defaults score nDCG@10 0.565 on `enterprise-v1` (erb-50k, 50,000 EnterpriseRAG-Bench documents, 470 questions). The low scores come from four general weaknesses, not from the benchmark:

1. Chunks carry no document context.
2. The default Semantic chunker stores averaged sentence-window vectors instead of embedding the chunk text.
3. There is no reranker by default.
4. Fusion leans toward dense search, which is the weaker side on enterprise jargon.

The two weakest slices fail for specific reasons:

- **Paraphrased "semantic" questions (0.244):** the question shares no distinctive word with the gold document, and dozens of templated sibling documents match the topic equally well.
- **HubSpot (0.329):** near-identical deal records whose title is only a company name, while questions describe the customer instead of naming it.

Each fix below needs one run on erb-50k to confirm. None has been measured here yet.

## Research brief
**Question:** what general retrieval weaknesses explain Connapse's low EnterpriseRAG-Bench scores, and what changes would fix them without tuning to the benchmark?

**Sub-questions:**
1. Why do the weak question categories fail?
2. Why do the weak sources fail?
3. Which design choices in the default pipeline cause it?
4. What does published work say helps?

**Out of scope:** answer generation, permissions, the full 512k corpus.

**Success criteria:** a ranked list of changes with evidence and an experiment for each.

## Finding 1 — Paraphrased questions fail on missing anchor words and templated siblings
This finding covers the 125 "semantic" questions on erb-50k: Connapse finds no gold document in the top 10 for 62% of them (nDCG@10 0.244).

**Missing anchor words.** An anchor is a rare word (in 500 or fewer documents) present in both the question and the gold document.
- 72% of semantic misses have no anchor, against 47% of semantic hits.
- Distinctive gold-title words appear in 1% of semantic misses, against 28% of basic hits.
- Examples: "Frankfurt secondary region with preemptible GPU workers" for a document that says "eu-central-2 … GPU spot"; "our inference tuning product" for "Optimize 1.3".

**Templated siblings.** In 76% of misses, a wrong top-10 document overlaps the question more than the gold document does. Many documents share the gold's topic, and nothing in the question tells them apart.

**Context.** The benchmark authors' own systems also score low here: semantic recall@10 is 43.2% for BM25 and 24.8% for dense search with text-embedding-3-large ([arXiv 2605.05253](https://arxiv.org/html/2605.05253)). Connapse's 0.38 recall sits between the two. This category is hard by design, so the realistic goal is to move toward and past BM25's 43%, not to match the basic category.

Sources: run analysis of `eval/runs/20261005-111902-7935554-enterprise-v1-connapse-hybrid` (primary); arXiv 2605.05253 (primary).

## Finding 2 — HubSpot, Gmail and Fireflies fail for different shape reasons
This finding covers source-level failures on erb-50k. The three weak sources fail differently, and none mainly because answers are split across chunks.

| Source | nDCG@10 | Question shares a gold-title word | Same-source share of top 10 | Main failure |
|---|---|---|---|---|
| HubSpot | 0.329 | 36% | 73% | templated near-duplicate records; title is only a company name |
| Gmail | 0.506 | 79% | 79% | sibling threads on the same customer; answer late in the thread |
| Fireflies | 0.495 | 68% | 18% | answer is one remark in a 3,000-token transcript; documents from other sources on the same feature win |
| Jira | 0.697 | 94% | 62% | (best) 13-word descriptive titles, few near-duplicates |

Three points need separating:

- **Question type confounds source.** 41% of HubSpot questions are semantic, and HubSpot scores 0.10 on those. But the source effect holds within basic questions too: HubSpot 0.49, Jira 0.84.
- **Long documents lose:** the gold-retrieval rate falls from about 0.70 for documents of 2.5–8k characters to 0.43 above 12k. That loss hits Confluence pages and transcripts.
- **Benchmark artifact:** 92% of Gmail documents are a stringified Python list with literal `\n` escapes. A real Gmail connector would not produce that, so it is not a Connapse weakness. Its measured effect looks small.

Source: run analysis (primary).

## Finding 3 — Four pipeline choices lose recall on heterogeneous content
This finding covers Connapse's default ingestion and search path as of `f3605abb` (code-verified).

1. **Averaged chunk vectors (new finding, verified).**
   - The default Semantic chunker embeds each sentence with one neighbour on each side, then stores the mean of those vectors as the chunk vector (`SemanticChunker.cs:61-81, 212-220`).
   - The pipeline keeps the means whenever every chunk has one (`IngestionPipeline.cs:559-564`). Medium-length documents therefore never get the chunk text embedded.
   - **Mechanism:** averaged vectors are smoother and score close to many generic queries, which is the paraphrase failure from Finding 1. The title reaches only the first two windows' vectors. Scores are not comparable across documents, because long documents fall back to real embeddings.
   - This has never been measured.
2. **No document context in chunks.**
   - Title, path and source type are not added to chunk text for embedding or BM25. Only Markdown gets a heading breadcrumb.
   - Later chunks of a long Confluence page or transcript lose what they are about. A HubSpot record's distinguishing name sits once in chunk 0.
   - The 95th-percentile breakpoint makes groups of about 20 sentences, and sentences are re-joined with spaces, which flattens "field: value" records.
3. **No reranker by default.**
   - `Reranker = "None"` (`SettingsModels.cs:202`). The reranker service from #542 exists, but #520 (on by default) is unshipped.
   - Connapse's own earlier eval measured +14–20 MRR from it on held-out GitHub questions.
4. **Small chunk-level pool, best chunk per document.**
   - Each side contributes 30 chunks (`HybridCandidatePool`), and documents are ranked by their single best chunk.
   - Long documents take several slots, so far fewer than 60 distinct documents compete.
   - Questions needing 6.5 gold documents on average (completeness, 0.423) need breadth this pool can't supply.

**Not a cause:** the vector index (HNSW recall@30 about 0.99), embedding prefixes and lowercasing are all correct.

## Finding 4 — Published evidence ranks reranking first, then context in chunks, then the embedding model
This finding summarises the external evidence relevant to Connapse's weaknesses.

- **Cross-encoder rerankers** are the most consistent zero-shot gain. A cross-encoder scores each query–document pair jointly instead of comparing precomputed vectors.
  - BEIR (a public retrieval benchmark) names them the strongest approach ([arXiv 2104.08663](https://arxiv.org/abs/2104.08663), primary).
  - mxbai-rerank-large-v2 is about +10–14 nDCG over BM25 on BEIR (vendor).
  - Qwen3-Reranker-0.6B scores 65.8 against 61.8 for its first stage (vendor).
  - Caveat: bge-reranker-v2-m3 scored *below* that same first stage, so pick the model by measurement.
- **Context in every chunk:**
  - Onyx, the benchmark's author, prefixes every chunk with the document title and a natural-language metadata sentence ([chunker.py](https://raw.githubusercontent.com/onyx-dot-app/onyx/main/backend/onyx/indexing/chunker.py), primary for behaviour, no measured gain).
  - Anthropic's Contextual Retrieval cut top-20 failures by 35% with context on embeddings alone, 49% with BM25 as well, and 67% with a reranker added ([anthropic.com](https://www.anthropic.com/news/contextual-retrieval), vendor, non-enterprise data).
  - Cheap title-only headers are what Onyx ships by default.
- **Embedding model:**
  - Similar-size models beat nomic-embed-text-v1.5 (about 53 on MTEB retrieval) by 2–3 points: arctic-embed-l-v2 55.6, gte-modernbert BEIR 55.3.
  - Qwen3-Embedding 0.6B/4B claim much more, but on a different MTEB version, so the numbers aren't directly comparable (vendor). Switching requires re-embedding.
- **Fusion:**
  - Convex score combination beats RRF ([Bruch et al., arXiv 2210.11934](https://arxiv.org/abs/2210.11934), primary). Connapse already does this.
  - On this benchmark, BM25 beat dense search by 22 recall points (68.4 against 46.0), because public embedding models miss internal codenames.
- **Query rewriting and HyDE** help weak retrievers and hurt strong ones ([Weller et al., arXiv 2309.08541](https://arxiv.org/abs/2309.08541), primary). Opt-in only.
- **Completeness questions:** iterative search was the only large measured gain on the benchmark (59.0 against 46.5 recall), at about 39× the tokens. Diversification evidence is modest.

## Recommendations — ranked general changes, each with an experiment
Each experiment is one `run --suite enterprise-v1` against the baseline above, compared with `compare`. Each should also be checked on the `v1` and `dev` suites, so a gain on enterprise data isn't a loss elsewhere.

1. **Turn the reranker on (#520).** Strongest external and internal evidence; no re-ingest needed. Try gte-reranker-modernbert (current service) and Qwen3-Reranker-0.6B; rerank the top 50 documents.
2. **Embed the chunk text, not averaged sentence windows.** Keep semantic boundaries, but embed each final chunk (one extra embedding batch per document). This is a correctness fix as much as a tuning change.
3. **Contextual chunk headers.** Prepend the document title, plus source and type metadata that connectors already know (channel, sender, ticket key, record type), to every chunk's embedded and keyword-indexed text. This targets long-document loss, HubSpot's entity name, and Fireflies/Gmail thread identity.
4. **Check `FusionAlpha` only after the reranker, and don't tune it to this benchmark.**
   - It was tuned on BEIR, where dense search is strong, while enterprise jargon favours BM25.
   - A reranker makes the final order much less sensitive to the fusion weight, so decide after item 1.
   - If it still matters, compare 0.75 against one alternative (for example 0.5) across every suite. Change the global default only if the alternative is no worse anywhere and clearly better on enterprise data.
   - Data that needs a different weight already has the per-container setting. Searching for the best alpha on enterprise questions alone would overfit, as #552 did when a single-domain dev set picked 0.1 and it lost on the full suite.
5. **Document-level candidate pool.** Gather candidates until N distinct documents exist (for example 100 chunks or 50 documents) before fusion and reranking. This targets completeness.
6. **Stronger embedding model (#522).** Qwen3-Embedding-0.6B is cheap on the 16 GB card. It requires re-embedding and a re-tuned alpha, so do it after items 1–4.
7. **Source/type filters from the query** ("Fireflies transcripts", "postmortems"). Connectors know the type, so expose it as a filter. Evidence is three questions; low priority.
8. **Query rewriting or HyDE:** opt-in only, measured last.

**Harness improvement:** save rankings to depth 100, not 10. Today the run can't show where gold sits when it misses the top 10, and that is exactly what reranking and pool-size decisions need.

## Conflicts and uncertainties
- **Fusion direction conflict.** Connapse's alpha 0.75 favours dense search (BEIR dev suite, #552). The benchmark paper finds BM25 far stronger on this data. Both can hold, because the data differ; the experiment above decides it.
- **Averaged vectors' effect size is unmeasured.** The mechanism is verified in code; the harm is inferred.
- **Rerankers can hurt.** Vendor numbers show bge-reranker-v2-m3 below its first stage.
- **Gold labels may be incomplete** for project_related: one "wrong" result was a transcript about the exact ticket asked about. Not measured.
- **Small samples:** Fireflies (21–28 questions), HubSpot (34), completeness (20).
- **Comparability:** erb-50k nDCG is not comparable to the paper's recall on 512k documents; recall falls as the corpus grows.

## Gaps — what we did not find
- No published evaluation of any technique on EnterpriseRAG-Bench beyond BM25, dense search and an agent.
- No measured study of CRM-record serialisation for retrieval.
- No effect size for collapsing results to one per document.
- Gold rank beyond 10 is not available from the current run.

## Source quality assessment
- **Diagnosis (Findings 1–3):** rests on primary evidence: this run's rankings, the dataset, and code read at a pinned commit.
- **Remedies (Finding 4):** mix primary papers (BEIR, Bruch, Weller, the benchmark paper) with vendor model cards and blogs (rerankers, embeddings, Anthropic).
- Every recommendation is therefore paired with a local experiment.

## Sources
**Primary:**
- arXiv 2605.05253 (EnterpriseRAG-Bench)
- arXiv 2104.08663 (BEIR)
- arXiv 2210.11934 (fusion)
- arXiv 2309.08541 (LLM query expansion)
- arXiv 2404.13207 (STaRK)
- arXiv 2409.04701 (late chunking)
- arXiv 2410.10813 (LongMemEval)
- Onyx chunker.py and chat_configs.py
- Connapse source at f3605abb
- Run 20261005-111902-7935554

**Vendor/secondary:**
- Qwen3-Embedding and Qwen3-Reranker model cards
- mixedbread mxbai-rerank-v2 blog
- arXiv 2412.04506 (Snowflake arctic-embed)
- Anthropic Contextual Retrieval
- dsRAG
- onyx.app leaderboard

## Update — reranker measured (2026-10-05)
Run 20261005-190045-f3605ab (hybrid with the `gte-reranker-modernbert-base` cross-encoder over the top 30, no other change) against the baseline: nDCG@10 0.565 → **0.660** (+0.095). The `compare` tool found no significant drop on any dataset.

| Slice | Before | Reranked |
|---|---|---|
| Semantic | 0.244 | 0.395 |
| Basic | 0.660 | 0.764 |
| Completeness | 0.423 | 0.470 |
| HubSpot | 0.329 | 0.582 |
| Gmail | 0.506 | 0.599 |
| Fireflies | 0.495 | 0.545 |
| Jira | 0.697 | 0.753 |

Project_related moved 0.570 → 0.538, the only drop (n = 40, not significant). Eval-harness latency p50 rose from 0.91 s to 1.06 s, and p95 from 1.11 s to 2.20 s. Both runs include the harness's own overhead, so these are not product latencies. Offline replay also shows α 0.4 beats 0.75 on this data; see learned-query-fusion-2026-10-05.md.
