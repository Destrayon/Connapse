---
title: Change the embedding model
type: how-to
audience: admin
---

# Change the embedding model

The embedding model turns each chunk of text into a vector, which vector search compares with the query's. Vectors from different models can't be compared, so after you change the model every document has to be embedded again. Connapse keeps the old vectors while that runs, so nothing goes missing from search in the meantime.

## Prerequisites

- You are the Owner or an Admin.
- The new model is reachable: pulled into Ollama, or an OpenAI or Azure OpenAI key and deployment.
- Time for the re-embed. It reads every document again, so on a local model a large library can take hours; ingestion and searches share the model while it runs.

## Steps

### 1. Switch the model

1. Open **Admin** → **Settings** → **Embedding**.
2. Pick the **Provider**: **Ollama**, **OpenAI** or **Azure OpenAI**. Then fill in its fields: **Model** and **Base URL** for Ollama, **API Key**, **Model** and **Base URL** for OpenAI, or **API Key**, **Endpoint URL** and **Deployment Name** for Azure OpenAI.
3. Set **Dimensions** to the length of the model's vectors, such as 768 for `nomic-embed-text` or 1536 for `text-embedding-3-small`. It must match what the model outputs.
4. Leave **Use the model's published prefixes** ticked unless the model's own documentation says otherwise.
5. Choose **Test Connection**, then **Save Changes**.

New uploads and syncs use the new model from now on.

### 2. Re-embed what's already there

After the save, the tab shows **Legacy vectors detected.** with the number of documents still on a previous model. Choose:

- **Re-Embed Now** to re-embed those documents. Connapse turns on cross-model search for the duration (see below).
- **Skip - Use Cross-Model Search** to leave them as they are. They stay findable through keyword matching, but vector search never finds them.

**Re-Embed All Documents**, under **Re-Embedding**, re-embeds every document whether or not it changed. Use it after changing the prefixes or when you suspect vectors are bad.

A banner shows **Re-embedding in progress.** and the queue depth. When the queue is empty, every document is on the new model.

### 3. Turn cross-model search off again

Cross-model search makes a vector search add keyword results, so documents still on an older model show up. Once the re-embed has finished it only adds work, so turn off **Enable Cross-Model Search** under **Admin** → **Settings** → **Search**. **Embedding Models in Store** on the same tab lists which models still have vectors, and the old one disappears once nothing uses it.

## Do it with the API

Change the `embedding` category with an ETag save, as in [Automate Connapse with the API](automate-with-the-api.md#3-change-a-setting-safely). A token needs `admin:settings`. Then start the re-embed:

```bash
curl -X POST http://localhost:5001/api/settings/reindex \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" -d '{}'

curl -H "X-Api-Key: $TOKEN" http://localhost:5001/api/settings/reindex/status
```

Without `force`, the reindex embeds again only the documents whose embedding or chunking settings changed; `"force": true` does them all, and `"containerId"` limits it to one container. The status answer has `queueDepth`, `isActive`, `status` and, after a failure, `lastError`.

For one container, `POST /api/containers/{id}/reindex` takes more options. `"dryRun": true` reports what would be re-embedded and why, without doing it:

```bash
curl -X POST http://localhost:5001/api/containers/CONTAINER_ID/reindex \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{ "dryRun": true }'
```

`maxDocuments` caps one run, and the answer's `continueAfter` goes into the next request to carry on from there. `GET /api/containers/{id}/files/{fileId}/reindex-check` says whether one document needs it.

## Related

- [Tune search](tune-search.md)
- [About ingestion](../concepts/ingestion.md)
- [Configuration reference](../reference/configuration.md)
