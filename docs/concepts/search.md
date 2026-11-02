---
title: About search
type: explanation
audience: admin
---

# About search

Connapse's default search is hybrid: it runs a vector search and a keyword search on the same query and fuses the two rankings into one. Vector search finds passages that mean the same thing in other words; keyword search finds exact names, codes and phrases that a vector blurs. Each covers the other's blind spot, which is why hybrid is the default. To change the settings, see [Tune search](../guides/tune-search.md).

## The steps of a search

1. **Candidates.** Vector search and keyword search each pick their best passages, 30 apiece by default (**Hybrid Candidate Pool**). Each candidate is then scored by both sides, so a passage only one side found still gets a fair score from the other.
2. **Fusion.** The two scores become one. By default each list is scaled to 0–1 (min-max) and combined as a weighted sum, with 0.65 of the weight on the vector score (**Fusion Alpha**). DBSF scales by each list's mean and spread instead, so one outlying score can't flatten the rest.
3. **Reranking**, if it's on. A cross-encoder reads the query and each of the top 30 fused passages together and scores them again. Reading the pair together makes it far more accurate than comparing two vectors, and far slower, which is why it sees only the top of the list. If the reranker is slow or down, the search returns the fused order instead, and skips the reranker for the next minute.
4. **Trimming.** Passages below **Minimum Score** are dropped. **AutoCut**, if it's on, then cuts the list at the largest gap between neighbouring scores, keeping the closely scored group at the top. Finally the list is cut to **TopK**, 10 by default.

A search of a source also drops every passage from a document the person searching can't see there. See [About permissions](permissions.md).

## Keyword ranking

Keyword scores use BM25, the standard ranking that rewards rare terms and discounts long passages. Searches that span several containers, queries made only of stop words, and passages not yet indexed for BM25 fall back to PostgreSQL's `ts_rank`.

## Vector index

A container's vectors are searched exactly until it holds 20,000 of them for one model. Past that, Connapse builds an approximate index (HNSW) for that container and model in the background, which keeps vector search fast as the container grows. **Index Search Breadth** trades speed for recall on that index.

## Several embedding models at once

Vectors from different embedding models can't be compared, so each vector is stored with the model that made it, and a query is compared only with vectors from the current model. After you change the model, documents still embedded with the old one are invisible to vector search until they're embedded again. **Cross-model search** covers the gap: while it's on, every vector search also runs a keyword search, so those documents still turn up. [Change the embedding model](../guides/change-embedding-model.md) walks through it.

## Related

- [Tune search](../guides/tune-search.md)
- [How Connapse works](how-connapse-works.md)
- [Configuration reference](../reference/configuration.md)
