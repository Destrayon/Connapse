# Plan: run both table approaches side by side in the eval harness (CON-327)

Plan ticket 4 of `docs/plans/con-320-raw-sql-rls.md`. The `tables-arms` command runs arm S (structured table tools plus
search) and arm Q (`table_sql` plus table listing, description and search) on the same tables-v1 questions, three runs
each, interleaved by question, and reports right answers, tool calls, search calls, confident wrong answers, tokens,
cost and wall time, with the pass bar's McNemar and Wilcoxon tests.

1. **Table tools in the toolbox.** Goal: `McpToolbox` can offer and call tools whose parameters are a `Guid` or a JSON
   object. Done when unit tests describe and call such a tool.
2. **Paired tests.** Goal: exact McNemar and Wilcoxon signed-rank, ported from statsmodels and SciPy. Done when they
   match SciPy-generated fixtures.
3. **The arms run.** Goal: `tables-arms` with `--arms structured,sql` and `--runs 3`, per-run records and the report.
   Done when unit tests cover arm tool sets, interleaving, per-run metrics, majority grading and the report.
4. **Ship.** Goal: CI-flag build, format, tests, README, PR into `epic/con-e6-tables`. Done when the PR is open.

## Progress
- Phase 1 done: `McpToolbox` describes `Guid` as a string and `JsonElement` as an object, and binds them through
  `TryConvert` (a bad UUID is a tool error, not a crash). Tests in `MemorySearchTests`; all 73 pass.
- Phase 2 done: `Statistics/PairedTests.cs` ports statsmodels' exact McNemar and SciPy's `wilcoxon` (exact, permutation
  and asymptotic branches). Fixtures added to `generate_expected.py`/`expected.json` (old keys unchanged); 31 reference
  tests pass. THIRD_PARTY_NOTICES updated. Next: `TablesArmsEval`.
- Phase 3 done: `Runs/TablesArmsEval.cs` (`tables-arms` in `Program.cs`) reuses the rag setup's data, prompt and grading;
  arm S is the four structured tools (read and aggregate already take a seen-as-of time, so `table_changes` isn't
  needed), arm Q is `table_sql`, list and describe; both get search. It turns on `Tables.AgentSql` through the stored
  overrides' JSON, and fails up front naming any tool the build lacks. `TablesRagEval` exposes `Select`, `IndexAsync`,
  `NonconformingTest` and `RunLine` for reuse. 31 tests in `TablesArmsEvalTests`; they and the rag tests pass. The live
  10-question dry run waits on CON-326. Next: ship.
- Phase 4 done: Release build with `-warnaserror --no-incremental` is clean, `dotnet format --verify-no-changes` passes,
  and all unit tests pass (3,701 in Connapse.Eval.Tests). PR #1215 is open into `epic/con-e6-tables`. Next: the
  10-question dry run (`tables-arms --split tune --limit-tasks 10`) once CON-326 merges.
- Dry run done (2026-11-11), after merging the epic (CON-326's `table_sql`): 60/60 runs, both arms 10/10 right. Structured
  2 calls, 0 searches, $0.0105 a run; SQL 3.07 calls, 0.37 searches, $0.0282 a run (2.69x, over the 1.2x bar). It found a
  scope bug (fixed) and led to streaming `runs.jsonl`. All 10 tune questions are lookups. Report in `E:\tmp\con-327`.
