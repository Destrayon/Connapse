# HERCULES Swap — Design

**Date:** 2026-05-27
**Author:** Connapse team (brainstormed via Claude Code superpowers:brainstorming)
**Branch:** TBD (after issue filed; sequenced after PR #333 merges)
**Built on:**
- `docs/research/container-summary-generation-strategy-2026-05-24.md` (current eager + medoid design)
- `docs/research/lazy-vs-eager-per-doc-summarization-2026-05-26.md` (HERCULES validation: +76% ARI on clustering quality vs summary embeddings, p<0.001)

## Problem

The current container-summary pipeline (live in PR #333) is **summary-clustering**:

1. Every uploaded document is summarized at ingest by `IngestionJobs.PerDocSummaryAsync`.
2. At rollup, `SummaryJobs.RollupContainerAsync` embeds the per-doc summary texts and runs farthest-first medoid selection on those summary embeddings to pick K representatives.
3. The K medoid summaries are passed to a reduce step that synthesizes the container summary.

Two findings from research challenge this shape:

- **Clustering on summary embeddings is worse than clustering on raw embeddings.** HERCULES (arXiv:2506.19992, Software Impacts 2025) measured +76% ARI and +286% silhouette score for raw-embedding clustering over summary-embedding clustering on 20 Newsgroups, p<0.001. The intuition: summaries lossy-compress documents along axes the LLM picks, which discards the variance dimensions that separate clusters.
- **Per-doc summaries have no validated secondary use.** The original eager design justified itself with "6 secondary uses" (search reranking, citation hints, UI snippets, etc.) — the 2026-05-26 follow-up research found none of these are actually deployed in Connapse or in comparable products. Per-doc summaries are a *means* to the container summary, not an end.

The eager design also pays its full LLM cost on every upload (whether or not the container summary is ever consumed) and rebuilds it on every re-ingest. For containers that ingest fast but query rarely, this is wasted spend.

## Goal

Add a setting that switches container summarization between the current method and a HERCULES-style method that:

1. Skips per-doc summarization at upload time.
2. At rollup, clusters documents using mean-pooled chunk embeddings (which already exist in `chunk_vectors` for vector search — no new embedding cost).
3. Lazy-summarizes only the K medoid documents inline at rollup.
4. Caches those K summaries in the existing `documents.summary` column for the next rollup.

Both methods produce a container summary the same way (reduce K medoid summaries). They differ in the clustering signal and in *which* docs ever get summarized.

## Non-goals

- **No removal of `documents.summary` / `summary_generated_at` / `summary_content_hash` columns.** They serve as the cache for the lazy method's K medoid summaries.
- **No comparative quality evaluation in CI.** Post-merge experimentation with the LLM-judge eval harness handles that.
- **No parallelization of the K inline LLM calls in lazy mode.** Sequential is fine for v1; revisit if rollup latency becomes a bottleneck.
- **No default-flip migration for existing containers.** Existing per-doc summaries stay populated; existing containers keep working under whichever method they're set to.
- **No support for retrofitting historical chunk_vectors with a new embedding model.** Mixed-model containers use the dominant model's vectors for pooling and log a warning.

## Setting design

### `SummarySettings.ContainerSummaryMethod`

A new string field on `SummarySettings` in `Connapse.Core/Models/SettingsModels.cs`:

```csharp
public record SummarySettings
{
    public bool Enabled { get; init; } = true;
    public string ContainerSummaryMethod { get; init; } = "document-clustering";  // NEW
    public string LlmModel { get; init; } = "...";
    public string LlmProvider { get; init; } = "...";
    // ...existing fields
}
```

Valid values:

| Value | Behavior |
|-------|----------|
| `document-clustering` | New default. Cluster docs by pooled chunk embeddings, summarize K medoids on demand at rollup. |
| `summary-clustering` | Current behavior. Summarize all docs at ingest, cluster by summary embeddings, reduce K medoid summaries. |

Unknown values fail validation in `SummarySettings` validator with a clear error pointing at the allowed list.

### Scope: G+C (global + per-container override)

The setting resolves via the existing `ContainerSettingsResolver.GetSummarySettingsAsync(containerId, ct)`, which already overlays per-container JSON overrides on top of global settings. `ContainerSummaryMethod` slots in alongside `LlmModel` and `LlmProvider` and inherits the same precedence (`appsettings` < env < secrets < DB global < per-container override).

### UI

`SummarySettingsTab.razor` gets a new dropdown labeled **"Container summary method"** with the two options. Help text below explains the cost/coverage tradeoff in plain language. Available at both global Settings and per-container FileBrowser Settings tab (same dual mount as the existing `LlmModel` selector).

## Data flow

### Per upload (ingestion)

```
IngestionJobs.PerDocSummaryAsync(documentId)
  → resolve SummarySettings via ContainerSettingsResolver
  → switch on ContainerSummaryMethod:
      "summary-clustering": existing path — generate summary, write to
                            documents.summary + summary_generated_at +
                            summary_content_hash
      "document-clustering": early-return. No LLM call. No DB write.
```

### Per rollup (container summary)

```
SummaryJobs.RollupContainerAsync(containerId)
  → resolve SummarySettings
  → switch on ContainerSummaryMethod:

      "summary-clustering":
        embeddings = _embeddingProvider.GetSummaryEmbeddingsAsync(docsWithSummaries)
        medoids    = MedoidSelector.Select(embeddings, k)
        summaries  = medoids.Select(m => m.Summary)  // pre-computed
        reduce     = ContainerSummarizer.Reduce(summaries)

      "document-clustering":
        embeddings = IVectorStore.GetPooledDocumentEmbeddingsAsync(containerId)
        medoids    = MedoidSelector.Select(embeddings, k)
        summaries  = medoids.Select(m =>
          cacheHit(m) ? m.CachedSummary
                      : PerDocSummarizer.SummarizeAsync(m).Tap(WriteCache))
        reduce     = ContainerSummarizer.Reduce(summaries)
```

Both branches converge at the reduce step. The medoid selection algorithm (farthest-first) is unchanged; only what's fed to it differs.

### Cache semantics in `document-clustering` mode

The lazy method writes K summaries to `documents.summary` per rollup. On the next rollup, if a medoid doc has:
- Non-null `documents.summary`, AND
- `summary_content_hash == documents.content_hash` (current ingestion hash)

then the cached summary is used and no LLM call is made. Stale entries (hash mismatch) are silently regenerated. This means container churn with stable content costs nothing — same as `summary-clustering`.

### Stuff regime (≤30 docs)

Unchanged for both methods. Below `StuffThreshold = 30`, all per-doc summaries flow straight into the reduce step without clustering. In `document-clustering` mode, this means inline-summarizing all N docs at rollup time (no clustering needed). For small containers this is the only place where the lazy method is *slower* than the eager method, but with N≤30 and rollups debounced behind the sweep, it's a few seconds of one-time latency per content change, not per request.

## New components

### 1. `IVectorStore.GetPooledDocumentEmbeddingsAsync`

New method on the existing `IVectorStore` interface:

```csharp
Task<IReadOnlyList<(Guid DocumentId, float[] Embedding)>> GetPooledDocumentEmbeddingsAsync(
    Guid containerId,
    CancellationToken ct = default);
```

**PostgreSQL implementation:** single SQL query joining `chunk_vectors` → `chunks` → `documents`, grouping by `document_id` with pgvector's `AVG(embedding)`. Filters to one `model_id` per call — picks the dominant `model_id` in the container, logs a warning at `Information` level listing the skipped doc count if multiple models are present.

```sql
SELECT d.id, AVG(cv.embedding)::vector AS pooled
FROM documents d
JOIN chunks c ON c.document_id = d.id
JOIN chunk_vectors cv ON cv.chunk_id = c.id
WHERE d.container_id = @cid
  AND cv.model_id = @model_id
GROUP BY d.id
HAVING COUNT(cv.chunk_id) > 0;
```

The client L2-normalizes each pooled vector before feeding to `MedoidSelector` (pgvector's `AVG` does not renormalize). Empty-chunk docs are excluded by `HAVING COUNT > 0`.

### 2. `SummaryStrategy` constants

Add `Connapse.Core/Models/SummaryStrategy.cs` for the allowed values:

```csharp
public static class SummaryStrategy
{
    public const string DocumentClustering = "document-clustering";
    public const string SummaryClustering = "summary-clustering";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        DocumentClustering,
        SummaryClustering,
    };
}
```

Stored as string in `SummarySettings` to match the existing convention (`LlmProvider`, `LlmModel` are also strings).

### 3. `ComputeDocSetHash` change

Current implementation in `SummaryJobs.cs` hashes `(docId, sha256(summary))` pairs. This breaks in `document-clustering` mode because most docs have null summary. New implementation hashes `(docId, content_hash)` pairs — works for both methods, same short-circuit guarantees.

**Migration impact:** hashes will differ across the swap, causing exactly one extra rollup per container on first post-deploy run. Acceptable one-shot cost.

### 4. Strategy branch in `IngestionJobs.PerDocSummaryAsync`

Early return when method is `document-clustering`:

```csharp
public async Task PerDocSummaryAsync(Guid documentId, CancellationToken ct)
{
    // ...load doc, resolve container...
    var settings = await _settingsResolver.GetSummarySettingsAsync(containerId, ct);
    if (settings.ContainerSummaryMethod == SummaryStrategy.DocumentClustering)
    {
        _logger.LogDebug("Skipping per-doc summary for {DocumentId} (document-clustering mode)", documentId);
        return;
    }
    // ...existing eager path...
}
```

### 5. Strategy branch in `SummaryJobs.RollupContainerAsync`

Two branches at the top of the rollup body. The branches share the reduce step and the container-write step at the end. Each branch is its own private method (`RollupSummaryClusteringAsync`, `RollupDocumentClusteringAsync`) for readability.

No new interfaces, no decorator pattern. The branching lives directly in the job class.

## Error handling and edge cases

### Mixed embedding models in one container

`chunk_vectors.embedding` is unconstrained, so a container can have chunks from multiple models. Strategy: query `model_id` distribution at rollup start, use the dominant one, log a `Warning`-level message listing the count of docs whose chunks were all under non-dominant models (excluded from clustering this rollup). Excluded docs will be picked up after re-ingestion under the dominant model. This matches existing vector-search behavior (which also filters by `model_id`).

### Docs with zero chunks

`chunk_count = 0` from failed ingestion or empty files. The `HAVING COUNT(cv.chunk_id) > 0` clause excludes them from the pooled-embeddings query, which is correct — they couldn't produce a pooled vector anyway and shouldn't be summarized.

### Method switch mid-flight

User toggles `summary-clustering` → `document-clustering` while a `PerDocSummaryAsync` job is enqueued. The job resolves settings at execute time (not enqueue time) — if it now sees `document-clustering`, it early-returns. Safe.

### Method switch lazy → eager

User toggles `document-clustering` → `summary-clustering`. Existing docs have null `documents.summary`. The eager rollup path must filter `WHERE summary IS NOT NULL` before medoid clustering — this is already true in the sweep query (`FindContainersWithStaleSummariesAsync` checks `Summary != null && SummaryGeneratedAt != null`); the implementation task is to confirm the same guard exists in the medoid selection step. The first eager rollup post-switch uses whichever K medoid docs do have cached summaries from prior lazy runs. As new docs ingest under eager mode, the corpus catches up. No backfill job is needed and no data is lost.

### LLM failure during lazy medoid summarization

K=20 inline LLM calls means K possible failures. `PerDocSummarizer` already has a Polly retry policy. If a single medoid still fails after retries: log + skip + continue with K-1 summaries (container summary degrades gracefully but is still produced). If all K fail: the rollup job itself fails and gets retried by Hangfire — same recovery path as today's eager failures.

### Rollup duration

Lazy mode at K=20 means up to 20 sequential LLM calls inline. At ~3s per call that's ~60s of rollup latency on a cold container vs sub-second on a warm one (cache hits). Within Hangfire's 300s job timeout. Could parallelize the K calls later if it becomes a bottleneck; not for v1.

### Cache invalidation on re-ingest

When `documents.content_hash` changes on re-ingest, `documents.summary` and `summary_content_hash` stay stale until the next rollup picks that doc as a medoid. Lazy mode checks `summary_content_hash == content_hash` before using cache — stale entries are silently regenerated. Same protection as eager mode.

## Testing

### Unit tests (new)

1. `IngestionJobsTests.PerDocSummaryAsync_DocumentClusteringMode_EarlyReturns` — verify the job short-circuits without calling `PerDocSummarizer`.
2. `SummaryJobsTests.RollupContainerAsync_DocumentClusteringMode_UsesPooledEmbeddings` — verify the rollup calls `IVectorStore.GetPooledDocumentEmbeddingsAsync` instead of `GetSummaryEmbeddingsAsync`.
3. `SummaryJobsTests.RollupContainerAsync_DocumentClusteringMode_LazySummarizesMedoids` — verify K inline `PerDocSummarizer` calls for medoid docs without cached summaries.
4. `SummaryJobsTests.RollupContainerAsync_DocumentClusteringMode_UsesCachedSummaries` — verify medoid docs with matching `summary_content_hash` skip the LLM call.
5. `SummaryJobsTests.ComputeDocSetHash_UsesContentHash_NotSummary` — verify the hash function works without summary text.
6. `ContainerSettingsResolverTests.GetSummarySettingsAsync_ContainerSummaryMethodOverride` — verify per-container override of the new field merges correctly.

Existing `MedoidSelectorTests` do not change — the algorithm is unchanged; only its input source differs.

### Integration tests (new)

1. `SummaryWorkflowIntegrationTests.DocumentClustering_EndToEnd` — upload 35 docs in `document-clustering` mode, verify zero per-doc summaries exist, trigger rollup, verify container summary is produced and exactly K medoid docs have summaries cached.
2. `SummaryWorkflowIntegrationTests.SummaryClustering_EndToEnd_UnchangedBehavior` — regression test for the current path; upload 35 docs, verify all have per-doc summaries, rollup produces container summary.
3. `SummaryWorkflowIntegrationTests.MethodSwitch_LazyToEager_BackfillsOnIngest` — switch container to `summary-clustering`, upload a new doc, verify it gets summarized at ingest.
4. `SummaryWorkflowIntegrationTests.PooledEmbeddings_FilterByDominantModel` — container with mixed-model chunk vectors, verify pooling uses the dominant model and logs the warning.

### Manual validation

Smoke test: spin up local docker-compose, upload 50 docs in `document-clustering` mode, observe LLM cost = 0 during ingestion, trigger rollup, observe ~K=17 LLM calls (`Math.Min(20, ceil(50/3))`) during first rollup. Re-trigger rollup with no content change, observe 0 LLM calls (full cache hit).

### Out of scope for CI

Container-summary *quality* comparison between methods. Post-merge experimentation with the existing LLM-judge eval harness handles this; quality validation is not gating the PR.

## Open questions

None that block implementation. The post-merge experimentation will surface follow-ups (e.g., whether to default `StuffThreshold` differently per method, whether to parallelize the K inline LLM calls in lazy mode), and those become their own issues.

## Sequencing

This work ships as a separate PR (#334-ish) after PR #333 (Hangfire migration) merges. No dependency between the two beyond `RollupContainerAsync` and `PerDocSummaryAsync` being the modification surface — both established by #333.
