# Plan: raw read-only SQL for agents, with row-level security (CON-320)

Research: `docs/research/rls-raw-sql-agents-2026-11-10.md`. In scope for v0.5.0 (the user's decision on PR #1198,
2026-11-10). A head-to-head eval of the structured tools against `table_sql` decides whether `table_sql` ships and which
approach is the default. The eval, its pass bar and the outcomes are set out in the research note's section (4) and
repeated below so the tickets can cite them.

## Tickets (one PR each)

1. **RLS under the structured tools.** Goal: the database enforces the readable set for the existing CON-303 tools.
   Covers a NOLOGIN `tables_owner`; per-container NOLOGIN reader roles granted to the login with
   `SET TRUE, INHERIT FALSE`; `REVOKE TEMPORARY` from PUBLIC; column grants for hidden columns; the
   `tables_auth.requests` token table and its `SECURITY DEFINER` lookup with pinned `search_path`; one restrictive
   policy per table with `FORCE ROW LEVEL SECURITY`; `security_invoker` on memory's views; the runner writing and binding
   the token; a startup check for PostgreSQL 17.11, 18.6 or 16.15. Done when the existing table-tool tests and the
   planted-leak tests pass through the policy, and a test with no token sees zero rows.
2. **Parser allowlist.** Goal: a `pgsqlparser` (libpg_query) check that accepts exactly the statements the research
   note allows (one `SelectStmt`, container tables only, allowlisted functions, no catalog relations, no `reg*` casts,
   no `WITH RECURSIVE`). Pure library with unit tests over every parser row of the bypass table. Done when those tests
   pass on Windows and Linux CI and parse-plus-check p95 is under 5 ms. Independent of ticket 1.
3. **`table_sql` tool.** Goal: the tool over MCP and REST, using tickets 1 and 2, sent as one `NpgsqlBatch` command,
   with the CON-303 limits, row and byte caps, client-side cancel, the untrusted-data notice, and a per-container admin
   setting whose default the eval decides. Done when every row of the bypass table is an integration test that passes,
   the database-layer rows also pass with the parser switched off, the planted-leak tests pass through `table_sql`, and
   the 100-prompt red-team set gives zero hidden rows. Depends on 1 and 2.
4. **Eval arms in the harness.** Goal: the tables eval can run arm S (four structured tools plus search) and arm Q
   (`table_sql` plus table listing, description and search) on the same questions, 3 runs each, interleaved by
   question, and record per run: correct, tool calls (refused SQL and tool errors count), search calls, confident wrong
   answers, tokens, cost and wall time. Reports the McNemar and Wilcoxon results per the pass bar. Done when a dry run on
   a 10-question dev slice produces the full report for both arms. Can start alongside 1 and 2 with a stub `table_sql`.
5. **Head-to-head run and decision.** Goal: run both arms on tables-v1's 180 held-out questions and apply the pass bar.
   Done when the results and the ship or no-ship outcome are recorded in `docs/research/`, and the `table_sql` default
   is set to match. Depends on 3, 4 and the tables-v1 question set (CON-E6).

## Pass bar (fixed 2026-11-10, before any data exists)

Same agent, harness and DataBench grading as CON-E6; result-informed changes re-tested on the sealed reserve set.

1. **Leaks:** zero failures on both arms (bypass suite, parser-off database rows, planted-leak tests, red-team set).
2. **Correct answers:** exact McNemar on the 180 pairs. Arm Q wins at ≥ 5 points higher with p < 0.05, loses at
   ≥ 5 points lower with p < 0.05, otherwise ties. No family 10+ points worse in the arm that becomes the default.
3. **Tool calls to the right answer:** on questions both arms got right, paired Wilcoxon; lower median wins at p < 0.05.
4. **Cost:** arm Q's average cost per question at most 1.2× arm S's.
5. **Speed:** parse and check p95 under 5 ms; `table_sql` aggregate p95 under 500 ms on 100k rows with 1,000 readable
   documents.

**Outcomes:** leaks fail, no `table_sql` until fixed. Q wins and 4–5 hold, `table_sql` on by default with the
structured tools kept. Tie, the tool-call winner is the default; a double tie keeps structured as default with
`table_sql` off behind the setting. Q loses, `table_sql` doesn't ship and structured operators are filed for any family
where SQL did better.

## Progress

- 2026-11-10: research note and plan written (CON-320).
- 2026-11-10: revised after the user's review. Raw SQL is in v0.5.0; the 180-failure threshold is replaced by the
  head-to-head eval; the build is split into the five tickets above. Next action: PM files tickets 1–5.
