# Manual QA: Container auto-summary steering

Verify that the auto-generated container summary biases an MCP-connected agent toward calling `search_knowledge` with appropriate queries instead of enumerating files.

## Setup

1. Run a local instance with an `ILlmProvider` configured (any of: Anthropic, OpenAI, Ollama, Azure).
2. Create a test container and upload ~10 documents on a coherent topic (e.g., Apple earnings reports, or any set of related markdown).
3. Wait for per-doc summaries to generate (check logs for `PerDocSummaryCompleted` events).
4. Wait for or trigger a container rollup (or lower `Summary.DebounceCount` in container settings for the test).
5. Verify `Container.Summary` is non-null via the `container_describe` MCP tool.

## Test scenarios

1. **Routing test.** Connect Claude Code (or an equivalent MCP client). Ask: "What did Apple report for Q3 2025 iPhone sales?" Expected: agent calls `container_list` → `search_knowledge(container=<test>, query="iPhone Q3 2025 sales")`. Should NOT use `list_files` + `get_document`.

2. **Out-of-scope test.** Ask the agent something the container should NOT route to. Expected: agent does not route based on the summary's "Does not cover…" section.

3. **Query-term lift.** Compare agent query terms with/without summary. Agent should use phrases from the "Query hints" section more often when summary is present.

## v2 — Configurable settings scenarios

### Scenario: Toggle enable/disable

1. Go to `/settings` → Summary tab
2. Confirm "Enable summary generation" checkbox is unchecked (opt-in default)
3. Upload a document to any container — verify no per-doc summary generated (DB: `documents.summary IS NULL` for the new row)
4. Check the box, click "Save Summary Settings"
5. Upload a document — verify per-doc summary IS generated within ingestion latency

### Scenario: Per-doc prompt override (fixes Qwen 3 script-bleed)

1. With Ollama + qwen3:14b configured and enabled, upload an engineering-flavored document and verify the summary may contain a Chinese character (`流程` etc.)
2. Go to `/settings` → Summary tab → "Per-document system prompt" textarea
3. Append "Respond entirely in English. /no_think" to the end of the default text
4. Save
5. Upload a fresh engineering document — verify the summary is entirely in English with no thinking preamble

### Scenario: Per-container override via FileBrowser

1. Open a specific container at `/{slug}/files`
2. Click the Settings tab
3. In the Summary Generation section: enable, set LLM Model to a non-default
4. Click Save
5. Upload a document to THIS container — verify the per-doc summary uses the override model (check the `documents.summary` or LLM provider logs)
6. Upload a document to a DIFFERENT container — verify it uses the global model (not the per-container override)

### Scenario: "Restore default" link behavior

1. Edit the per-doc prompt textarea
2. Click "Restore default" — verify the textarea repopulates with the in-code default text
3. Save — verify no override is stored (DB JSON for that container has `summary.perDocSystemPrompt: null` or the field absent)

## v3 — Hangfire migration scenarios

### Scenario: Upload latency

1. Upload a 1MB markdown file to a summary-enabled container
2. UI should acknowledge upload within ~1 second (Pending pill briefly visible, flips to nothing within seconds)
3. Per-doc summary lands asynchronously within ~10-30s (depending on LLM)
4. Container rollup pill appears ~60s after the last per-doc summary, runs ~30s, disappears

### Scenario: Failed-state UI + retry

1. Configure an invalid LLM provider config (broken endpoint URL)
2. Upload a doc; per-doc summary will fail after 3 retries
3. Verify red "Failed" pill appears on the document row with a retry button (↻)
4. Fix the provider config
5. Click the retry button; verify the doc re-ingests and lands at SummaryIndexed

### Scenario: Hangfire dashboard access

1. Open `/hangfire` while logged in as an admin → see the dashboard
2. Log out, log in as a non-admin → `/hangfire` returns 403
3. Verify the dashboard shows: scheduled jobs (rollups in flight), failed jobs (if any), succeeded count, recurring job `summary-sweep-stale-containers`

### Scenario: Regenerate-now button

1. Open a container, switch to the Summary tab in settings
2. Click "Regenerate summary now"
3. Container rollup pill appears immediately (without waiting for 60s debounce settle)
4. Pill disappears when rollup completes; container summary updated

### Scenario: Hourly safety-net sweep

1. In the Hangfire dashboard → Recurring Jobs tab, verify `summary-sweep-stale-containers` is registered with cron `0 * * * *`
2. Manually trigger it via the dashboard
3. Verify no errors and no rollups fire if all containers are up-to-date
4. Make a container's summary stale (e.g. update a doc summary timestamp manually) and trigger the sweep again; a `RollupContainerAsync` for that container should appear in the queue

## HERCULES — document-clustering container summary method (#335)

The default container summary method for new installs is `document-clustering`. The legacy method is `summary-clustering`. Verify both end-to-end.

### Scenario: Document-clustering default for new container

1. Create a fresh container "qa-hercules-default" and open its Summary settings tab.
2. **Expected:** "Container summary method" dropdown shows "Document clustering (recommended)" by default.
3. Enable summaries (master toggle on) and save.
4. Upload 5 documents (any text files).
5. Wait for ingestion to complete (per-doc spinners stop, no Indexed pill).
6. **Expected:** Each doc's row shows no "Summary" indicator. The container row shows no summary yet (rollup hasn't happened).
7. Click "Regenerate summary now".
8. **Expected:** Container rollup pill appears, runs for ~10–30s, container summary text populates. Open each doc detail — all 5 docs (N ≤ stuff threshold of 30) now have per-doc summaries cached.

### Scenario: Document-clustering clustering regime (>30 docs)

1. Create container "qa-hercules-cluster".
2. Enable summaries in document-clustering mode.
3. Upload 35 documents.
4. Wait for ingestion to complete (no per-doc summary spinners — they all SummaryIndexed without LLM calls).
5. Click "Regenerate summary now".
6. **Expected:** Container summary appears. Open the FileBrowser doc list — exactly K = `min(20, ceil(35/3))` = 12 docs have summaries; the remaining 23 do not. Watch the Hangfire dashboard during the rollup — should see exactly 12 `PerDocSummary*` jobs run (sequential, inside the rollup) plus 1 `RollupContainerAsync`.

### Scenario: Switch to summary-clustering

1. Open container "qa-hercules-default" settings tab.
2. Change "Container summary method" to "Summary clustering". Save.
3. Upload one new document "doc-after-switch.txt".
4. Wait for ingestion to complete.
5. **Expected:** "doc-after-switch.txt" gets a per-doc summary at ingest (open detail panel to confirm). The 5 docs uploaded under document-clustering may or may not have summaries depending on whether they were medoids in a prior rollup — lazy-mode cache state is preserved.

### Scenario: Cache reuse on re-rollup

1. In container "qa-hercules-cluster" (from Scenario 2), click "Regenerate summary now" a second time without uploading anything.
2. **Expected:** Rollup completes quickly. Hangfire dashboard / logs show **zero** `PerDocSummaryAsync` invocations this time (all K medoid docs have cached summaries whose `summary_content_hash` matches the current `content_hash`).

## Ollama concurrency gate — stability under concurrent rollups (#335)

A single local Ollama instance serializes generation internally. When many container rollups are enqueued at once (e.g. the stale-container sweep settling a backlog), unbounded `/api/chat` requests pile into Ollama's internal queue; each queued request's `HttpClient` timeout clock (`LlmSettings.TimeoutSeconds`, 300s default) is already running, so the slowest tail requests exceed it and surface as `TaskCanceledException`. `LlmSettings.MaxConcurrentRequests` (default 1) gates how many completions the app issues to Ollama at once — callers wait in-process before the HTTP call starts, so each request that reaches Ollama runs alone and finishes well within the timeout. Only the Ollama provider is gated; cloud providers (OpenAI/Azure/Anthropic) handle concurrency server-side.

### Scenario: Concurrent rollups don't time out on Ollama

1. With Ollama configured (single instance) and summaries enabled in document-clustering mode, create several small containers (N ≤ 30 docs each, so each rollup uses the "stuff" regime).
2. Make them all stale at once and let the `summary-sweep-stale-containers` job fire (or trigger it from the Hangfire dashboard), so multiple `RollupContainerAsync` jobs run concurrently.
3. Tail the logs: `/api/chat` calls should start strictly one at a time — each new call begins only after the previous one returns (`MaxConcurrentRequests = 1`) — and every rollup should complete with **no** `TaskCanceledException`. The first call may be slow (cold model load); subsequent calls are fast.
4. (Optional) Raise `MaxConcurrentRequests` above 1 only if the Ollama host has GPU/CPU headroom to run that many generations in parallel without any single one exceeding `TimeoutSeconds`. The setting is read once at startup, so changing it requires a restart.

## Postgres connection budget — stability under concurrent rollups (#335)

Concurrent rollups also pressure the database, not just the LLM. Two Npgsql connection pools share one Postgres server: the app's `NpgsqlDataSource` (queries, vector search, EF) and the background job runner's storage pool. Each pool, left unspecified, defaults to a maximum of 100 connections — so two uncapped pools can together demand 200 against a server that typically allows ~100. On top of that, `DisableConcurrentExecution` on a rollup holds its distributed-lock connection open for the job's entire lifetime, including while a worker is parked waiting on the Ollama concurrency gate. With an oversized worker pool (Hangfire's default is `ProcessorCount * 2`, which is large on a many-core box), a backlog of swept rollups could hold enough connections at once to exhaust the server, surfacing as `PostgresException: 53300: sorry, too many clients already`.

Three knobs bound the demand, each overridable per deployment:

- **`Database:MaxPoolSize`** (default 40) — caps the app's `NpgsqlDataSource` pool.
- **`Hangfire:MaxPoolSize`** (default 30) — caps the background runner's storage pool.
- **`Hangfire:WorkerCount`** (default `min(ProcessorCount * 2, 16)`) — caps concurrent jobs, so fewer lock-holding connections are alive at once.

The defaults sum to a per-process budget (40 + 30 = 70 connections) that leaves headroom for admin tooling under a ~100 ceiling. A deployment whose Postgres allows more connections, or that runs multiple app instances against one server, should raise or lower these to fit its own `max_connections`.

Rollups also do **not** use Hangfire's automatic retry (`AutomaticRetry(Attempts = 0)` on `RollupContainerAsync`). The recurring `summary-sweep-stale-containers` job re-enqueues any container that is still stale on its next tick, so a failed rollup is retried by the sweep rather than by Hangfire stacking Scheduled retry jobs on top of the sweep's enqueues — the duplicate pile-up that retries would create is exactly what pressured the connection pool.

### Scenario: Backlog of rollups doesn't exhaust connections

1. With summaries enabled and a working LLM provider, make several containers stale at once (e.g. update their doc summary timestamps) and let `summary-sweep-stale-containers` fire, or trigger it from the Hangfire dashboard.
2. While the backlog drains, query the database: `SELECT count(*) FROM pg_stat_activity;` — the count should stay well under the server's `max_connections` (no climb toward the ceiling).
3. **Expected:** every rollup completes; no `53300: sorry, too many clients already` in the app logs. A rollup that does fail (e.g. transient LLM error) lands in **Failed** in the dashboard — not Scheduled — and is picked up again on the next sweep tick rather than by a Hangfire retry.
