# CON-312 passage links: linked-set results

Run on 2026-11-11 with `dotnet run --project tests/Connapse.Eval -- tables-linked --workers 1`: Claude Sonnet 5.5
(medium effort) through `claude -p`, hybrid search, the 30 questions in `eval/datasets/tables/linked/questions.jsonl`
over the ten fictional reports (five DOCX, five PDF). All ten tables loaded at ingest under their document names. The
run conforms to the bar (20 table and 10 prose questions, default config and model). Raw data:
`tables-linked.json` (summary and bar) and `tables-linked.runs.jsonl` (every tool call of every run).

**Result: bars 1, 2 and 3 fail; bar 5 passes; bar 4 is shown by tests.** By the bar, passage links do not ship yet.

| Bar | Needed | both-linked | both-unlinked | Pass |
|---|---|---|---|---|
| 1. Follows the link | ≥ 16 of 20 table questions | 0 | — | no |
| 2. Helps | ≥ unlinked on 30, and +4 on table questions | 28 of 30, 20 of 20 table | 29 of 30, 20 of 20 table | no |
| 3. Doesn't distract | ≤ 2 prose table calls, prose ≥ unlinked | 1 call, 8 of 10 | 1 call, 9 of 10 | no |
| 4. No leak | planted-leak tests | `DocumentTableLinkTests`, `PassageTables` unit tests | | tests |
| 5. Cost | ≤ 1.2x unlinked | $0.0238 a question | $0.0318 a question | yes (0.75x) |

## Why

- **The agent goes to `table_list` first.** In both setups it opened with `table_list` on 14 of the 20 table
  questions, before it had seen a passage. On the other six it searched first and saw the link (the passage's
  `Tables:` line and the note in its text both name the table and its id), then still called `table_list` before
  reading. With ten tables in the container, one `table_list` shows every table, so the link adds nothing the agent
  needs.
- **The set has no headroom for bar 2.** both-unlinked also answered all 20 table questions, because it has
  `table_list` too. A gain of 4 is out of reach while the unlinked setup can list every table.
- **Bar 3 fails on grading.** The only prose miss that differs, linked-003, answered "Olga Patel, the fleet manager,
  signs off every repair above $2,500 before work starts." for the expected "Olga Patel"; the exact-match grader
  counts it wrong. linked-030 missed in both setups the same way ("The chief operating officer").

## Changed after this run

`table_list`'s description said tables are built from CSV and spreadsheet files only; it now also names tables found
in documents. It is a correction, not tuning, but it touches a table tool after the results, so by the outcome rules
it counts as a result-informed change and is not re-scored here.

A re-test uses the one sealed reserve linked set and every bar re-run; the reserve file was not opened.
