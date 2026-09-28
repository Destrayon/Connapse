# Ingestion reliability — design

Issue: #562. Status: approved in discussion 2026-09-28, pending spec review.

## Problem

An audit of the ingestion path (`IngestionPipeline`, `IngestionJobs`, `HangfireIngestionQueue`,
`SourceSyncService`, `IngestionProgressBroadcaster`, `PostgresDocumentStore`) found that no single
component owns a document's state:

- **Three status vocabularies.** `documents.status` (free text: Pending/Queued/Processing/Ready/Failed),
  `documents.ingestion_state` (enum: Pending/Indexed/SummaryIndexed/Failed), and an in-memory
  `IngestionJobStatus` table. The file list reads `ingestion_state`; container stats, source failure
  counts, the file details panel, REST and MCP read `status`.
- **They contradict.** `IngestionPipeline.IngestAsync` catches every exception, writes
  `Status = "Failed"` and returns normally. `IngestionJobs.IngestAsync` sees success and writes
  `IngestionState = SummaryIndexed`. The same happens for "no extractable content" and stale-generation
  skips.
- **Retries don't fire.** Since the pipeline swallows errors, Hangfire's `AutomaticRetry` only runs for
  failures before the pipeline (reading the file). Source sync adds a `SyncFailedAttempts` metadata
  counter on top — three overlapping retry mechanisms.
- **Dead progress code.** Nothing calls `IIngestionQueue.UpdateJobStatus`; the in-memory table stays
  "Queued" forever and grows without bound; `IngestionProgressBroadcaster` polls it. `QueueDepth` is a
  hard-coded 0, so `/api/settings` and the embedding settings tab always report ingestion idle.
- **Reindex is not atomic.** Chunks are deleted before the new version is parsed and embedded. The
  document leaves search in between, and stays gone if that fails.
- **No priority for search.** Up to 16 Hangfire workers call the embedding provider concurrently. Query
  embeddings go through the same provider with no limit or priority; on a local Ollama a large sync
  queues every search behind it.
- Per-doc summaries re-download and re-parse the file from its connector.

## Approach

Keep Hangfire; fix how Connapse uses it. Add one package, `Microsoft.Extensions.Http.Resilience`. Use
`System.Threading.RateLimiting` (built in) for the priority split. Build the state machine — the hard
part is a race-safe persisted transition, which no state-machine library provides.

Patterns used: optimistic-concurrency claims (as Postgres-backed queues do), transient vs permanent
fault classification (Azure transient-fault guidance), timeouts/backoff/circuit breaker and bulkheads
(*Release It!*), build-then-swap (shadow index), lease expiry for stuck work (SQS visibility timeout).

## 1. Document state machine

### Columns

`documents` gains, replacing `status` and `ingestion_state`:

| Column | Type | Meaning |
|---|---|---|
| `ingestion_status` | smallint enum `DocumentStatus` | Queued, Processing, Ready, FailedRetryable, FailedPermanent |
| `summary_status` | smallint enum `SummaryStatus` | NotNeeded, Pending, Done, Failed |
| `attempt_count` | int, default 0 | Ingestion attempts for the current file version |
| `status_changed_at` | timestamptz | Set on every transition |
| `job_id` | text, nullable | Hangfire job id of the current attempt |

`error_message`, `generation` and `last_indexed_at` stay. `Ready` means searchable; summary progress
never affects it.

Allowed transitions:

```
(new) ─────────────► Queued
Queued ────────────► Processing          claim by a worker
Processing ────────► Ready | FailedRetryable | FailedPermanent | Queued (retry scheduled)
Ready / Failed* ───► Queued              re-upload, reindex, sync change, manual retry
Processing ────────► Queued              stuck sweep
```

### DocumentLifecycle

A singleton in `Connapse.Storage` (interface in `Core`) is the only writer of these columns. Every
method is one conditional `UPDATE` via `ExecuteUpdateAsync`, returning whether it applied:

```sql
UPDATE documents SET ingestion_status = @to, status_changed_at = now(), ...
WHERE id = @id AND ingestion_status = ANY(@allowedFrom) AND generation = @generation
```

Zero rows means another worker or a newer upload won; the caller stops quietly. This replaces the
pipeline's two `IsCurrentGenerationAsync` checks and the status-string comparisons in source sync.
After each applied transition it publishes a `DocumentStatusChanged` event through
`IIngestionStateBroadcaster` (SignalR plus in-process notifier).

Methods: `EnqueueAsync` (→ Queued, bumps generation when content changed, stores `job_id`),
`TryClaimAsync` (Queued → Processing), `CompleteAsync` (→ Ready, resets `attempt_count`),
`RetryScheduledAsync` (→ Queued with last error), `FailAsync(kind)`, `ResetStuckAsync`,
and the summary equivalents.

### Stuck-job sweep

A recurring Hangfire job (every 10 minutes) finds documents in Processing whose `status_changed_at` is
older than Hangfire's invisibility timeout (30 min). For each, it reads the state of `job_id` from
Hangfire storage; if that job is not Processing, the document goes back to Queued and is re-enqueued.
This replaces `SourceSyncService.MaxCursorHold`'s six-hour escape hatch.

### Derived status

`GetContainerStatsAsync` and the source store's failed counts group over `ingestion_status`. REST and
MCP keep returning a `status` string derived from the enum — Queued → "Pending", Processing →
"Processing", Ready → "Ready", both failures → "Failed" — plus a new `failureKind` field, so the
separate connapse-cli keeps working.

### Removed

`IngestionJobStatus`, `IngestionJobState`, `IIngestionQueue.UpdateJobStatus` / `GetAllStatuses` /
`GetStatusAsync` / `RegisterJobCancellation` / `UnregisterJobCancellation` / `DequeueAsync`,
`HangfireIngestionQueue`'s dictionaries, `IngestionProgressBroadcaster`'s polling loop,
`IngestionState`, the `SyncFailedAttempts` metadata key. `BatchesEndpoints` reads document rows
instead of the in-memory table.

### Migration

One migration adds the columns and backfills, then drops `status` and `ingestion_state`:

- status Ready → Ready; Pending/Queued/Processing → Queued; Failed → FailedRetryable
- `attempt_count` ← `metadata->>'SyncFailedAttempts'` (default 0); the key is removed
- `summary_status` ← Done when `summary IS NOT NULL`; Pending when ingestion_state = Indexed; else NotNeeded
- `status_changed_at` ← `coalesce(last_indexed_at, created_at)`

Documents mapped to Queued from a Processing row are picked up by the stuck sweep if their Hangfire
job is gone.

## 2. Job execution

### Failures propagate, classified

`IngestionPipeline` stops catching exceptions. New `PermanentIngestionException` (Core) is thrown for:
unsupported extension, parser failure, no extractable content, and source file not found. Everything
else is transient. `DocumentOwnershipChangedException` stays separate: the job logs it and returns
without retrying or touching the document's status, because the refusal is not a failure of that
document.

`IngestionJobs.IngestAsync`:

1. `TryClaimAsync` — if false, return (stale or already taken).
2. Run the pipeline.
3. Success → `CompleteAsync`; enqueue per-doc summary when the container's settings need one.
4. `PermanentIngestionException` → `FailAsync(Permanent)`, return without rethrow (no Hangfire retry).
5. Other exception → rethrow.

A Hangfire `IApplyStateFilter` on the ingestion job maps Hangfire state to document status:
entering `ScheduledState` from a retry → `RetryScheduledAsync` (Queued, last error kept); entering
`FailedState` after retries are exhausted → `FailAsync(Retryable)`; `DeletedState` (cancel) →
`FailAsync(Retryable, "Cancelled")`. The document no longer flickers to Failed between attempts.

### One retry budget

`attempt_count` increments on each claim. Source sync requeues a FailedRetryable document only while
`attempt_count < 3`, and `EnqueueAsync` resets it to 0 when the remote signature changed. Hangfire's
`AutomaticRetry` drops to `Attempts = 2` since its retries now count toward the same budget. Manual
retry (UI/API) resets it.

### Build-then-swap reindex

The pipeline parses, chunks and embeds entirely in memory, then in one transaction: deletes the
document's chunks (vectors cascade), inserts new chunks and vectors, and applies `CompleteAsync`'s
update with the generation guard. If the guard fails the transaction rolls back and the newer job wins.
The old version stays searchable until commit; a failure leaves it untouched. `ReindexService`
enqueues through `DocumentLifecycle` instead of writing `Status = "Pending"`.

### Summaries from stored chunks

`PerDocSummaryAsync` rebuilds the document text from `chunks` ordered by `chunk_index`, using
`start_offset`/`end_offset` to drop overlap, instead of reading the file through its connector and
re-parsing. Its failures set `summary_status = Failed` only.

### Provider resilience

`AddStandardResilienceHandler` on the Ollama embedding and LLM clients, the cross-encoder client, and
the OpenAI/Azure OpenAI embedding clients (moved onto `IHttpClientFactory` where they are not):
per-attempt timeout, retry with jittered backoff on 429/5xx/timeouts, circuit breaker. An open circuit
surfaces as a transient failure, so jobs back off instead of each waiting out a timeout.

Out of scope: streaming large files to disk instead of buffering in a `MemoryStream`.

## 3. Search priority

### EmbeddingThrottle

A decorator over the resolved `IEmbeddingProvider`, applied in the embedding factory delegate, with
two singleton `ConcurrencyLimiter`s chosen by the call's `EmbeddingInputType`:

- `Document` → ingestion lane, default 2 concurrent requests.
- `Query` / `Unspecified` → query lane, default 8.

Ingestion can never take more than its lane, so the provider keeps headroom for searches. Requests
beyond a lane's cap wait in FIFO order on that lane only; a waiting query is cancelled with its HTTP
request. Both caps live in `EmbeddingSettings`
(`MaxConcurrentIngestionRequests`, `MaxConcurrentQueryRequests`) and are editable in the embedding
settings tab; a limit change rebuilds the limiters.

### Worker pools

Two `BackgroundJobServer`s instead of one: ingestion queue (default 4 workers,
`Hangfire:IngestionWorkerCount`) and summarization + default queues (default 1,
`Hangfire:SummaryWorkerCount`, matching `LlmSettings.MaxConcurrentRequests`). `Hangfire:WorkerCount`
is removed. Worker count above the ingestion embedding cap buys overlap of download/parse with
embedding, not more provider load.

## 4. Visibility

- **Queue depth:** `IIngestionQueue.QueueDepth` becomes `GetQueueDepthAsync()`, reading enqueued +
  processing counts for the ingestion queue from Hangfire's monitoring API.
- **Source and container badge:** one derived label — Syncing (sync running), "Processing N"
  (Queued + Processing > 0), "N failed" (any failed), Ready. A source's last sync outcome combines with
  its documents' status; a successful sync that left failures shows the failures.
- **Failed documents list** on the source and container pages: file, error message, retryable or
  permanent, with Retry and Retry all failed actions (`POST /api/documents/{id}/retry`,
  `POST /api/sources/{id}/retry-failed`, `POST /api/containers/{id}/retry-failed`).
- REST/MCP stats keep their existing ready/processing/failed fields, now from `ingestion_status`.

## Testing

- `DocumentLifecycle`: integration tests per transition, including a lost race (two claims, one wins)
  and a stale generation.
- Pipeline: unit tests that a parse failure throws `PermanentIngestionException` and an embedding
  failure propagates; integration test that a failed reindex leaves the old chunks searchable.
- State filter: integration test that a transient failure ends Queued between retries and
  FailedRetryable after the last one.
- Sync: the retry budget stops at 3 and resets on a signature change.
- Throttle: unit test that with the ingestion lane saturated a query-lane call completes without
  waiting.
- Migration: backfill test over rows in every old status combination.

## Delivery

Stacked PRs under the 300-line guideline (migrations excluded from the count):

1. Lifecycle columns, migration, `DocumentLifecycle`, readers switched to `ingestion_status`.
2. Job execution: exceptions, classification, state filter, retry budget, source sync on the lifecycle.
3. Atomic reindex and summaries from chunks.
4. Dead code removal: in-memory job table, polling broadcaster, legacy queue members.
5. Stuck sweep.
6. `EmbeddingThrottle` and worker pools.
7. Provider resilience handlers.
8. Visibility: queue depth, badges, failed list, retry endpoints.

## Changes made during implementation

- **Retries are scheduled by the job, not a Hangfire state filter.** Filters register in
  process-global static state, which breaks the multi-host integration tests. The job reads
  `attempt_count` (max 4) and either marks the document Queued and schedules the next attempt
  (30 s, 2 min, 10 min) or marks it FailedRetryable. `AutomaticRetry` is off for ingestion.
- **Sync retries FailedRetryable after a 24-hour cooldown**, with a fresh budget, instead of
  "while `attempt_count < 3`". The job's own retries run out within the hour; the cooldown covers
  longer outages without re-enqueueing a broken source every cycle. FailedPermanent waits for the
  file to change.
- **The job id is recorded at enqueue** (`RecordJobAsync`), so the sweep also catches a document
  left Queued by an enqueue that failed after the status was written. `ResetStuckAsync` was
  dropped: the sweep re-enqueues through the normal path. `MaxCursorHold` stays as a safety net.
- **Parsing and chunking run before the first database read**, so unreadable input is classified
  without touching a row. PostgreSQL data errors (SQLSTATE class 22, e.g. NUL characters from a
  BOM-less UTF-16 file) during the swap are permanent.
- **The embedding throttle uses `SemaphoreSlim`**, matching `LlmConcurrencyGate`, not
  `ConcurrencyLimiter`. The default queue runs on the ingestion pool, so the stuck sweep is never
  stuck behind a long summary.
- **Resilience covers the Ollama embedding and cross-encoder clients only**, with one quick retry
  and a circuit breaker. The OpenAI and Azure OpenAI SDKs already retry; Ollama LLM generation is
  expensive and the summary job retries it. `Microsoft.Extensions.Http.Resilience` is pinned to
  10.0.0 to match the repository's 10.0.x `Microsoft.Extensions` packages.
- **Queue depth counts Queued and Processing documents**, not Hangfire's queue, since that is the
  backlog a user means. `/api/batches/{id}/status` resolves the job's document through Hangfire's
  job details and reports the document's status.
- **Visibility is counts plus retry actions**, not a per-file failure list on the Sources page:
  a source is never browsable, by design. Failed files in a container are already listed in the
  file browser with their error and a per-file retry.
- **The SignalR hub sends `DocumentStatusChanged` to clients subscribed to that document**
  (`SubscribeToDocument`) instead of broadcasting every document id to every client.
