# Plan: raw read-only SQL for agents, with row-level security (CON-320)

Research: `docs/research/rls-raw-sql-agents-2026-11-10.md`. This plan only runs if the decision gate in Phase 1 says
so. Nothing here ships in v0.5.0.

## Decision gate

The tables proof (CON-E6 ticket 10) runs first, with the four structured tools only. From its held-out failures, count
the questions that failed **because the tools couldn't express them**: window or latest-per-group, date arithmetic,
self-join, percentile, CASE bucketing.

- Fewer than 10 of 180: close this plan. File structured-operator tickets for the common gaps (latest row per key,
  date difference) instead.
- 10 or more: run the phases below.

## Phases

1. **Gate count.** Goal: classify the tables proof's held-out failures by cause. Done when a short note in
   `docs/research/` lists each failure, its cause, and the count against the threshold of 10.
2. **Leak suite first.** Goal: write the bypass tests before any implementation, one integration test per row of the
   research note's bypass table, plus the planted-leak and red-team sets. Done when the suite exists, runs against a
   stub `table_sql` that refuses everything, and passes.
3. **Database layer.** Goal: per-container NOLOGIN reader roles granted with `SET TRUE, INHERIT FALSE`; a NOLOGIN
   `tables_owner`; `REVOKE TEMPORARY` from PUBLIC; column grants for hidden columns; `tables_auth.requests` and the
   `SECURITY DEFINER` lookup with pinned `search_path`; one restrictive RLS policy per table with `FORCE ROW LEVEL
   SECURITY`; `security_invoker` on memory's views; a startup check that refuses raw SQL below PostgreSQL 17.11, 18.6
   or 16.15. Done when every database-layer leak test passes **with the parser switched off**.
4. **RLS on the structured tools.** Goal: the CON-303 runner writes and binds the request token too, so both paths
   check the same readable set in the database. Done when the existing table-tool tests and the planted-leak tests pass
   through the policy.
5. **Parser allowlist.** Goal: `pgsqlparser` (libpg_query) allowlist exactly as the research note specifies (one
   `SelectStmt`, container tables only, allowlisted functions, no `reg*` casts, no `WITH RECURSIVE`), run through one
   `NpgsqlBatch` command. Done when the full leak suite passes with the parser on, and parse-plus-check p95 is under
   5 ms.
6. **`table_sql` tool.** Goal: the fifth tool over MCP and REST, off by default behind a per-container admin setting,
   with row and byte caps, client-side cancel and the untrusted-data notice. Done when it answers through the full path
   and the leak suite stays at zero.
7. **Eval.** Goal: write `tables-sql-v1` (60 held-out questions, five families) before any run, then run the pass bar
   below. Done when results are recorded in `docs/research/` with the ship or no-ship outcome.

## Pass bar (fixed 2026-11-10, before any data exists)

Same agent, harness and DataBench grading as CON-E6. Result-informed changes are re-tested on a sealed reserve set,
never by moving a bar. All must hold:

1. **Gain:** on `tables-sql-v1`, four tools plus `table_sql` beat four tools alone by at least 25 points, exact McNemar
   p < 0.01.
2. **No loss:** on tables-v1's 180 held-out questions, at most 3 fewer correct answers; no family loses more than 2.
3. **No more confident wrong answers:** wrong answers (not refusals) rise by at most 3 points on either set.
4. **Leaks:** zero failures across the bypass suite (parser on and, for database-layer rows, parser off), the
   planted-leak tests and the 100-prompt red-team set.
5. **Speed:** parse and check p95 under 5 ms; on 100k rows with 1,000 readable documents, `table_sql` p95 at most 1.5×
   the equivalent structured request and under 500 ms for aggregates; RLS overhead reported separately.
6. **Cost:** average agent cost per question at most 1.2× without `table_sql`, on both sets.

**Outcomes:** all pass, ship behind the setting. Bar 1 fails, don't ship; add structured operators for the failing
families. Bar 4 fails, don't ship until fixed. Any other failure, one redesign re-tested on the sealed reserve.

## Progress

- 2026-11-10: research note and this plan written (CON-320). Next action: Phase 1, after the tables proof runs.
