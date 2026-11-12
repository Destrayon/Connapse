# CON-311 admin-saved queries: results (round 2)

**Verdict: fail.** Saved queries are safe and as accurate as today's tools, but they did not make agents better or
cheaper on the questions they cover, so the feature does not meet its bar (`eval/pass-bars.md`, Admin-saved queries).

Run on 2026-11-11: `tables-saved`, round 2, 30 held-out questions none of which round 1 asked, 3 runs per arm,
claude-sonnet-5-5 at medium effort, hybrid config. The run is conforming. Raw data: `tables-saved.json`,
`tables-saved.runs.jsonl`, `tables-saved.probe.jsonl`.

| Bar | Needed | Result | |
|---|---|---|---|
| 1. Correctness | saved ≥ baseline | 30 vs 30 right (McNemar p = 1) | pass |
| 2. Gain | +3 right, or cost ≤ 0.85× | +0 right; cost $0.0202 vs $0.0175 per question = 1.16× | **fail** |
| 3. Login | 0 cross-user rows, own accounts right in most runs | 0 cross-user rows in 15 probe runs; own accounts 3 of 3 | pass |
| 4. No regression | table, search, leak, authorization tests pass unchanged | CI on the PR | see PR checks |

**Why the gain fails.** Today's agent already answers every covered question, in a median of 2 tool calls
(`table_list`, then one `table_aggregate` or `table_read`). Saved queries were used in 48 of 90 saved-arm runs, and
each of those takes a median of 3 calls (`table_list`, `table_query_list`, `table_query_run`), so the saved arm made
more calls (mean 2.56 vs 2.23) and cost more, though it ran faster (7.8 s vs 9.3 s mean). The saved arm got one run
wrong (89 of 90); no question changed its majority answer.

**Login probe.** Every one of the 15 runs ran the saved query. The four attempts to read another user's accounts
(asking outright, passing the owner as an argument, claiming to be the other user, asking for their total) returned
no other owner's rows, because the owner comes from the caller's login.

**Round 1** (stopped, kept as `tables-saved-round1-stopped.runs.jsonl`): the saved arm never called a saved query, so
`table_list` now points agents at them when a container has any (recorded in `pass-bars.md` before round 2). In round
2 the baseline arm's `table_list` showed the same pointer to a tool it lacks; it made no visible difference (it called
no missing tool).

**What this means.** The trust and per-user guarantees hold, but on these questions agents don't need vetted queries
to get the answer right, and the extra lookup makes them dearer. The value would have to come from questions agents
get wrong today, which this dataset doesn't have among the covered templates.
