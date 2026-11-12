# CON-335 nested files as tables: results

Run on 2026-11-12: Claude Sonnet 5.5 (medium effort) through `claude -p`, the four table tools, the tables base prompt,
at most 25 tool calls, one attempt per question. The bar is "Bar for nested files" in `eval/pass-bars.md`, committed
before the nested set or any run existed. Every run conforms (default config and model, all questions answered).

**Result: all four bars pass.**

| Bar | Needed | Result | Pass |
|---|---|---|---|
| 1. Accuracy on the 60 nested `test` questions | at least 51 of 60 | 57 of 60 (95%) | yes |
| 2. Every family | at least 8 of 10 each | lookup 10, filter_count 10, child_aggregate 10, grandchild 9, scalar_list 10, json 8 | yes |
| 3. No drop on tables-v1's 180 held-out questions | at least 176 of 180 | 180 of 180 on the branch rebased onto CON-310 (180 before the rebase too) | yes |
| 4. Cost | 0 server AI tokens; tool calls and agent cost reported | 0 by construction; 2.95 tool calls and $0.0285 a nested question | yes |

The 12 `tune` questions, used only for dry runs, scored 12 of 12.

## The three nested misses

All three were agent misreads with the right rows already returned; none was a loading problem.

- `nested-test-036` (grandchild): counted 18 timing-belt replacements where the rows it read held 19.
- `nested-test-052` (json): read the wrong cell of the calibration matrix, 0.558 for 0.565.
- `nested-test-055` (json): the same, 1.022 for 1.399. Each matrix row is a `readings.calibration` row whose jsonb
  `value` holds the inner array, and the right cell was in the result.

## tables-v1 recheck after CON-310

CON-310 changed how `table_list` ranks tables, and this branch was first measured on a base without it. The rerun on
the rebased branch scored 180 of 180, every family 30 of 30, with 0 restricted-login leaks; the 30 mixed questions
scored 30 of 30 (29 before the rebase). Cost was $0.0218 a question and 2.53 tool calls.

## Files

Each `.json` is the run's summary and bar; each `.runs.jsonl` has one line per question with its family, expected and
given answer, `correct`, and every tool call.

- `nested-test.json`, `nested-test.runs.jsonl`: the 60 scored nested questions.
- `nested-tune.json`, `nested-tune.runs.jsonl`: the 12 tune questions.
- `tables-v1.json`, `tables-v1.runs.jsonl`: bar 3 on the rebased branch (the result that counts).
- `tables-v1-before-rebase.json`, `tables-v1-before-rebase.runs.jsonl`: bar 3 before the rebase onto CON-310.
