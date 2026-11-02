---
title: Tune search
type: how-to
audience: admin
---

# Tune search

Connapse's defaults suit most content, and the change that helps most is turning on a reranker, which is off by default. This guide covers that first, then the settings worth changing when results still miss. Why each one works is in [About search](../concepts/search.md).

## Prerequisites

- You are the Owner or an Admin.
- The settings live under **Admin** → **Settings** → **Search**. Each change applies to the next search; nothing needs a restart or a re-index.

## Steps

### 1. Turn on a reranker

A reranker reads each of the top candidates next to the query and reorders them. It was the largest quality gain in Connapse's evaluation.

1. Run one. The Compose file's `reranker` service is a TEI (Hugging Face Text Embeddings Inference) server; see [Choose the reranker build](../self-host/install.md#choose-the-reranker-build) for CPU and GPU. Cohere Rerank, Jina Reranker, Azure AI Foundry and Voyage AI work too, with an API key.
2. Set **Reranker** to **Cross-Encoder**, pick the **Provider**, and fill in **Model**, **Base URL** or **API Key** as the provider needs. With the Compose reranker, the base URL is already `http://reranker:80`.
3. Choose **Test Reranker**, then **Save**.

**Candidates** (default 30) is how many fused results the reranker reads. More can find a document ranked lower, at a cost that grows with each one: about 45 ms for 30 on a GPU. **Timeout (seconds)** (default 5) bounds a slow reranker; a search that hits it returns results in their original order.

### 2. Pick the search mode

**Search Mode** is **Hybrid (Vector + Keyword)** by default, and it is the right choice almost everywhere: vector search finds paraphrases, keyword search finds exact names and codes, and hybrid finds both. Choose **Keyword (Full-Text)** only for content that is mostly identifiers, and **Vector (Semantic)** only to compare.

### 3. Adjust fusion, if keyword or meaning matters more

| Setting | Default | Change it when |
|---------|---------|----------------|
| **Fusion Alpha (Semantic Weight)** | 0.65 | Lower it (toward 0.5) when exact terms, ticket numbers or product codes should win; raise it when people search in their own words. |
| **Fusion Method** | Convex Combination (min-max) | Choose **DBSF (distribution-based, outlier-robust)** when one very high score crowds out the rest. |
| **Hybrid Candidate Pool** | 30 | Raise it when a document you expect is found by only one side and ranked too low. |
| **Keyword Ranking** | BM25 | Leave it. **PostgreSQL ts_rank** is the fallback for older chunks. |

### 4. Trim the result list

**Results per Query (TopK)** (default 10) is how many results a search returns. **Minimum Score** (default 0) drops results below a score, such as 0.2 to cut noise. **Enable AutoCut** keeps the top group of closely scored results and drops everything after the largest gap in scores.

### 5. Set one container apart

A container can override the global search, chunking, embedding and upload settings. An override replaces the whole category for that container, and any field you leave out takes its built-in default rather than the global value. So start from the global category and change it:

```bash
curl -s -H "X-Api-Key: $TOKEN" http://localhost:5001/api/settings/search \
  | jq '{ search: (. + { fusionAlpha: 0.5 }) }' > overrides.json
curl -X PUT http://localhost:5001/api/containers/CONTAINER_ID/settings \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" --data @overrides.json
```

A category you leave out of the override follows the global settings. `DELETE` on the same address removes every override.

## Do it with the API

Read the category, change it and send all of it back with the read's `ETag` in `If-Match`. A token needs `admin:settings`:

```bash
curl -s -D headers.txt -H "X-Api-Key: $TOKEN" http://localhost:5001/api/settings/search > search.json
ETAG=$(grep -i '^etag:' headers.txt | cut -d' ' -f2 | tr -d '\r')
jq '.reranker = "CrossEncoder" | .crossEncoderProvider = "TEI"' search.json > search-new.json
curl -X PUT http://localhost:5001/api/settings/search \
  -H "X-Api-Key: $TOKEN" -H "If-Match: $ETAG" \
  -H "Content-Type: application/json" --data @search-new.json
```

Every search setting, with its environment variable, is in the [configuration reference](../reference/configuration.md).

## Related

- [About search](../concepts/search.md)
- [Change the embedding model](change-embedding-model.md)
- [Configuration reference](../reference/configuration.md)
