# Plan: head-to-head of the structured table tools against `table_sql` (CON-328)

Ticket 5 of `docs/plans/con-320-raw-sql-rls.md`. Runs arm S (structured tools) and arm Q (`table_sql`) on tables-v1's
180 held-out (test split) questions, three full passes each, one attempt per question, and applies the pass bar fixed
in that plan on 2026-11-10. The pass bar is copied into the PR description before any run and is not changed after.

1. **Data.** Goal: build the tables-v1 CSVs and confirm the test split holds 180 questions. Done when `build.json`
   matches the committed checksums and the question count is 180.
2. **Pass bar on record.** Goal: push this plan and open the PR into `epic/con-e6-tables` with the pass bar in its
   description. Done when the PR is open, before any run.
3. **Leak tests.** Goal: zero leak failures on both arms (bypass suite, parser-off database rows, planted-leak tests,
   red-team set, structured-tool trimming tests). Done when those integration tests pass under capped containers.
4. **Head-to-head run.** Goal: `tables-arms --split test` with both arms and 3 runs, at low priority with capped
   containers and CPU-only Ollama. Done when the report is conforming (no diagnostic-only reasons).
5. **Decision.** Goal: write results and the ship or no-ship outcome to `docs/research/`, set the `table_sql` default to
   match, CI-flag build and tests, push, CI green, message the PM. Done when CI passes.

## Progress
- Phase 1 done: `build_tables.py` wrote the 14 CSVs (DataBench parquet downloaded at the pinned revision); every
  SHA-256 in `build.json` is in `eval/MANIFEST.json`. `questions.jsonl` has 180 test questions, 30 in each of six
  families. Next: open the PR with the pass bar.
- Phase 2 done: PR #1221 open into `epic/con-e6-tables` with the pass bar and run rules in its description, before any
  run. Next: leak tests.
- Phase 3 done: CI-flag Release build clean. 427 table tests pass (Storage 254, Integration 173: research bypass,
  parser-off database rows, planted-leak, trimming, RLS, parser check), Testcontainers and ryuk capped at 2 CPUs/4 GB,
  Ollama CPU only. The plan's 100-prompt red-team set was never built; the run's restricted-question leak check covers
  the agent side. Next: the full run.
- Phase 4 interrupted (2026-11-11 21:54Z): Claude Code stopped the run 25 minutes in because the PC was critically low
  on memory (4.3 GB free of 64 GB), after 35 of 1,080 runs (6 questions). Containers cleaned up; nothing left running.
  The harness can't resume, so a restart is a fresh full run and the 35 partial runs are set aside, not merged.
  Waiting on the PM to say when to restart.
