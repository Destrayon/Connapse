# Head-to-head: the structured table tools against agent-written SQL (CON-328)

**Decision: don't ship `table_sql` in 0.5.0.** Under the pass bar fixed on 2026-11-10 (`docs/plans/con-320-raw-sql-rls.md`),
agent-written SQL (arm Q, `table_sql`) loses to the structured table tools (arm S) on right answers, tool calls and cost.
Neither arm leaked. The `Tables.AgentSql` setting stays off by default, which is already its default.

Most of the gap comes from a `table_sql` defect, not from SQL as an approach: its list of queryable tables is cut at
4,000 characters, and the six tables past the cut (tickets, supermarket, invoices, orders) can't be queried because no tool
tells the agent their SQL names. All 24 questions SQL missed use those tables. On the 98 questions that only use the listed
tables, SQL got every question right but still cost 1.65× as much as the structured tools, which is over the 1.2× cost bar.
So fixing the listing alone would not pass the bar.

## Setup

- tables-v1, test split: 180 held-out questions, 30 in each of six families; 12 asked as a restricted user.
- Both arms ran three full passes, interleaved by question (1,080 runs), one attempt per question, no reruns. A question is
  right for an arm when at least 2 of its 3 runs are right.
- Agent: `claude-sonnet-5-5` at medium effort through `claude -p`, at most 25 tool calls per run, the same prompt, data and
  DataBench grading as CON-E6. Only the tools differ: arm S has `table_list`, `table_describe`, `table_read`,
  `table_aggregate`, `search_knowledge`, `get_document`; arm Q has `table_sql`, `table_list`, `table_describe`,
  `search_knowledge`, `get_document`.
- Ran 2026-11-11 23:52Z to 2026-11-12 03:54Z at low priority, Testcontainers and ryuk capped at 2 CPUs / 4 GB, Ollama on
  CPU. The report is conforming (no diagnostic-only reasons).
- The run's own report step crashed after the last run (`MathNet.Numerics` couldn't load: the eval binary's worktree was
  replaced while the run was in progress). The report was then built from the run's saved `runs.jsonl` with the new
  `tables-arms-score` command, which reads back each run's recorded grade, calls, tokens, cost and time and applies the
  same scoring code. Nothing was asked again.

## Results against the pass bar

| Bar item | Structured (S) | SQL (Q) | Result |
|---|---|---|---|
| 1. Leaks: zero failures on both arms | 0 leaked runs; leak integration tests pass | 0 leaked runs; leak integration tests pass | **Pass** |
| 2. Right answers: Q wins or loses at a 5-point gap with McNemar p < 0.05 | 180/180 (100%) | 156/180 (86.7%) | **Q loses**: −13.3 points, 24 questions only S got right, 0 only Q got right, exact McNemar p = 1.2 × 10⁻⁷ |
| 3. Tool calls on questions both got right: Wilcoxon p < 0.05 | median 2 | median 3 | **S wins**: 156 questions, p < 0.001 |
| 4. Cost: Q at most 1.2× S per question | $0.0168 | $0.0518 | **Fail**: 3.09× |
| 5. Speed: parse-and-check p95 < 5 ms; `table_sql` aggregate p95 < 500 ms on 100k rows | — | parse gate passes; `table_sql` 100k-row gate not built | Not needed for the outcome |

The outcome rule for "Q loses" applies: `table_sql` doesn't ship, and structured operators are filed for any family where
SQL did better. SQL did better in no family, so none are filed.

Family gaps (Q minus S, in points): filter_count −16.7, group_topk −16.7, join −16.7, lookup −10, seen_as_of 0,
sum_avg −20.

### Other measurements

| | Structured (S) | SQL (Q) |
|---|---|---|
| Runs right | 540/540 | 469/540 (86.9%) |
| Restricted-user questions right | 12/12 | 0/12 |
| Runs that answered wrong with confidence | 0 | 53 |
| Runs with no answer | 0 | 18 |
| Tool calls per run: mean / median / p90 | 2.67 / 2 / 4 | 4.81 / 3 / 12 |
| Search calls per run: mean | 0.03 | 0.59 |
| Wall time per run in seconds: median / p90 | 9.1 / 13.6 | 11.7 / 41.3 |
| API list price for all 540 runs | $9.07 | $27.99 |

About 40 of the 53 confident wrong answers say the agent couldn't determine the answer, written into the answer instead of
declining. The other 13 are real wrong values, mostly supermarket totals estimated from the parts of the CSV that search
returned. None matched the admin answer on a restricted question.

## Why SQL lost

`TableSqlService.Listing` puts the tables an agent may query into every refusal, with their SQL names (`t_<32 hex>`),
and stops at 4,000 characters with "(more tables not shown)". In the main container the listing showed bank, customers,
employees, forbes and inventory; invoices, orders, supermarket and the three ticket tables never appeared. The SQL name is
not the table's id, and neither `table_list` nor `table_describe` returns it, so the agent had no way to name those tables.
It tried quoted display names, `t_<table id>`, and `information_schema` (refused), then fell back to searching the CSV text.

- All 24 questions SQL got wrong use those tables; all 12 restricted-user questions are among them.
- On the 98 questions that use only listed tables, both arms got 98/98. SQL still took more calls (median 3 against 2) and
  cost $0.0254 per question against $0.0154 (1.65×).

Because item 4 fails even on those 98 questions, a fixed listing would still not pass the bar on this evidence. Any
re-test after a fix has to run on the sealed reserve set, as the pass bar requires for result-informed changes.

## Files

- Scored report: `E:\tmp\con-328\tables-arms-test.json` (local); the per-run results are `tables-arms-test.runs.jsonl`
  beside it.
- Scoring from a saved run: `Connapse.Eval tables-arms-score --runs-file <runs.jsonl> --out <file>` with the options the run
  used (`tests/Connapse.Eval/Runs/TablesArmsEval.cs`, `ReadRun` and `ScoreAsync`).
