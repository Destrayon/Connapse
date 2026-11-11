# CON-307 results: does the tables container beat text search?

**tables answers 179 of the 180 `test` questions (99.4%) against rag's 51 (28.3%).** That passes bars 1–4 and the
cost part of bar 7 in `eval/pass-bars.md` ("Tables container (CON-E6)"). Bars 5, 6 and 7 pass, bar 5 on v1 and pdfqa-v1, the
scope the user approved (enterprise-v1 and links-v1 skipped; reasons under bar 5).

The deciding run is `tables-fresh`: one complete, conforming held-out run, one attempt per question. An earlier
complete run (`tables`) had 77 API refusals; it and its retry are reported below and don't count toward the bar.

## Verdict per bar

| Bar | Needs | Result | Verdict |
|---|---|---|---|
| 1. Accuracy | ≥ 25 points over rag, exact McNemar p < 0.01 | 179 vs 51: +71.1 points; 128 right only with tables, 0 only with rag; p = 5.9e-39 | **Pass** |
| 2. Absolute accuracy | ≥ 70% | 99.4% | **Pass** |
| 3. Every family | no family below rag | every family 30/30 except sum_avg 29/30 (table below) | **Pass** |
| 4. Mixed set | both ≥ rag on the 30 mixed | both 26/30, rag 1/30 | **Pass** |
| 5. No harm to text search | no Holm-significant drop, nDCG@10 −0.01 at most, on v1, enterprise-v1, pdfqa-v1, links-v1 | v1 identical on all 15 scored datasets; pdfqa-v1 −0.008, not significant; enterprise-v1 and links-v1 skipped | **Pass** (scope v1 and pdfqa-v1, approved by the user) |
| 6. Leaks | CON-31 leak suite and table leak tests, 0 failures | 64 of 64 pass: 42 link tests (CON-31 reader contract and leak canaries), 6 memory, 16 table permission trimming | **Pass** |
| 7. Cost and speed | 0 server AI tokens; `table_aggregate` p95 < 500 ms on 100k rows; agent cost ≤ 1.5x rag | no AI provider anywhere in the table code; p95 120 ms; cost 0.26x | **Pass** |

## The deciding run (`tables-fresh`)

- **Data:** `tables-v1`, the 180 `test` questions with the `tables` setup (the four table tools) and the 30 mixed
  questions with `both` (all six tools), one attempt each, 210 agent runs.
- **Agent:** `claude -p` with claude-sonnet-5-5 at effort medium, at most 25 tool calls, the CON-299 base prompt
  unchanged, on the Claude subscription. Graded by `TableAnswerGrader`; no judge model.
- **When:** 2026-11-11, tables loaded 10:37:46 UTC, run finished about 11:04 UTC, at commit 302db0fa plus the runner
  changes in this PR. The subscription's seven-day window went from 58% to 59%.
- **Outcomes:** all 210 runs answered; 0 refusals, 0 errors, no run hit the call cap.
- **Files:** `tables-fresh.json` (report), `tables-fresh.runs.jsonl` (every run with its calls, answer and grade),
  `compare-fresh.json` (`tables-compare` against `../con-299/rag.runs.jsonl`).

| Family | rag | tables | Tool calls (tables) |
|---|---|---|---|
| lookup | 25/30 | 30/30 | 2.27 |
| seen_as_of | 11/30 | 30/30 | 2.33 |
| group_topk | 7/30 | 30/30 | 2.57 |
| sum_avg | 4/30 | 29/30 | 2.30 |
| filter_count | 3/30 | 30/30 | 2.27 |
| join | 1/30 | 30/30 | 3.23 |
| **All test** | **51/180** | **179/180** | 2.49 (rag 4.19) |
| Mixed (rag vs both) | 1/30 | 26/30 | 4.30 |

- **Restricted user:** 12/12 right with the trimmed answer, 0 leaks. Admin 167/168.
- **seen_as_of:** 16/16 on the first inventory load and 14/14 on the second (rag: 0/16 and 11/14).
- **Misses.** test-080 (sum_avg) answered 15706.16 for 17973.54. The four mixed misses (mixed-006, -008, -017,
  -020) are each a little low (43 for 44, 40 for 41, 11564.89 for 11980.4, 7078.06 for 7368.3).
- **Cost:** $0.0246 per question at API list price, 0.26x rag's $0.0931 ($6.97 for the run).

## The two fixes, from the tune split only

The first smoke run on a tune question found two problems in CON-306's table-tool code. ClaudePM approved fixing
both in this PR before any held-out run; nothing was changed after a held-out result.

1. **`table_describe` sampled history rows** (5f259285, `TableQueryCompiler.CompileSample`): on a table with history
   the sample rows mixed superseded versions with current ones. It now samples current rows only.
2. **No tool said how to read a table as it was** (302db0fa, `McpTools.Tables.cs`): `seen_as_of` worked but only
   `table_changes` mentioned history. `table_read` and `table_aggregate` now say how to pass it.

| Tune split (60 questions + 30 mixed) | Before the fixes | After |
|---|---|---|
| All tune | 54/60 | 57/60 |
| seen_as_of | 4/10 (first load 1/7) | 10/10 |
| Mixed | 28/30 | 30/30 |
| Cost per question | $0.0258 | $0.0274 |
| Restricted leaks | 0 | 0 |

The three tune misses after the fixes: tune-018 and tune-047 guessed container names instead of using the ID in the
prompt, and tune-023 gave up mid-aggregate. The prompt is CON-299's and was kept unchanged.

## The first held-out run and its refusals

The first complete run (`tables.json`, conforming, tables loaded 09:58:16 UTC, finished about 10:20 UTC) scored
**103/180**: 77 of its 210 runs ended with an API `stop_reason` of `refusal` and were scored wrong.

- **Who and what.** All 77 were admin questions; none of the 12 restricted-user questions and none of the 30 mixed
  questions refused. They were spread over every family (9–15 each) and over the whole run order, not one window.
- **Shape.** 71 of the 77 stopped at turn 2 with no tool call, at $0.0053–$0.0337 each; the rest after a call or
  two. The prompt is the same template for every question, and the refused questions read like the answered ones.
- **Not reproducible.** Three refused questions retried singly at once all answered correctly. The 77 rerun
  unchanged (`tables-refused-rerun.json`, not conforming: `--ids` set) gave 0 refusals and 69/77 right. Neither tune
  run nor CON-299's 210 rag runs had a refusal, and the fresh run 17 minutes later had none.
- **What the API returned** is not recoverable for the first run: agents run with `--no-session-persistence` and the
  runner kept only the outcome. The runner now keeps every event a refused session prints (`ClaudeCliAgent.RefusalEvents`),
  so a future refusal carries its full response in the run's `error` field; the fresh run had none to keep.

Raw, the first run passes bar 1 (+28.9 points, p = 9.4e-8) and fails bar 2 (57.2%) and bar 3 (lookup 16 vs 25)
(`compare-raw.json`). Merged with the retry it would pass every agent bar at 172/180 (`compare.json`,
`tables-merged.runs.jsonl`), but merging changes the bar's "one attempt per question" after results were seen, so
ClaudePM ruled it out and ordered the fresh complete run instead, whose result is the verdict above.

| Run | Questions | Correct | Refused | Conforming | File |
|---|---|---|---|---|---|
| First held-out | 180 + 30 mixed | 103/180, mixed 28/30 | 77 | yes | `tables.json` |
| Retry of the 77 | 77 | 69/77 | 0 | no | `tables-refused-rerun.json` |
| First + retry merged | 180 | 172/180 | — | no (ruled out) | `compare.json` |
| **Fresh held-out (deciding)** | 180 + 30 mixed | **179/180, mixed 26/30** | 0 | yes | `tables-fresh.json` |

## Bar 5: text search is unchanged

Each suite ran with `eval/tools/run_light.sh` on two builds: the merge base `e2173c34` (the epic before any tables
code, exported with `git archive`) and this branch at 302db0f. `compare` pairs the two runs query by query; the files
are in `search-suites/` (each run's `report.json` and the `compare` output).

| Suite | Datasets compared | Largest nDCG@10 drop | Holm-significant drops | Verdict |
|---|---|---|---|---|
| v1 | 15 (nanobeir-scidocs not scored on either build) | 0.000: all 15 identical | 0 | Pass |
| pdfqa-v1 | 1 (92 questions) | −0.008 (Holm p = 1) | 0 | Pass |
| enterprise-v1 | — | not run | — | Skipped (user-approved scope) |
| links-v1 | — | not run | — | Skipped (user-approved scope) |

- **v1** scored exactly the same on both builds, every metric on every dataset. nanobeir-scidocs is unscored on both
  because 344 of its 2,210 documents fail to ingest, the same count the 2026-10-24 baseline recorded.
- **pdfqa-v1** ran twice on the branch by accident (two copies started six seconds apart). The first scored 0.008
  below the base and the second exactly equal to it, so the 0.008 is run-to-run noise in the PDF suite, not the
  tables code; either run passes. The first is the one compared above (`pdfqa-v1-compare.json`); the second is
  `pdfqa-v1-compare-duplicate.json`.
- **enterprise-v1 and links-v1** were skipped. The user narrowed bar 5 to v1 and pdfqa-v1 for three reasons: the
  tables work changed only how CSVs are loaded, not search; those suites hold almost no CSVs; and a full run needs a
  several-GB EnterpriseRAG-Bench download and about a week (measured at about 15.5 hours per suite per build without
  the GPU).
- **Why no change is expected.** Table containers are kept out of search, and a CSV in a documents container is
  indexed as text exactly as before, except a large CSV (`CsvTextDigest.IsLarge`), whose text becomes a digest
  (CON-319). The suites load only `.txt`, `.md` and `.pdf` files, so neither change reaches them.

## Bar 6: no leaks

`dotnet test tests/Connapse.Integration.Tests -c Release` on this branch, 2026-11-11, with Ollama on localhost:11434:
**64 passed, 0 failed, 0 skipped.**

| Tests | Count | What they check |
|---|---|---|
| `Links.*` (CON-31) | 42 | the link reader contract on Postgres and the reference reader, the planted-leak canaries, and link ingestion and search for GitHub, Atlassian and code links |
| `Memory.MemoryLeakTests` | 6 | one user's memories never reach another |
| `TablePermissionTrimmingTests` (CON-304) | 16 | a table's rows, columns and aggregates are trimmed to the documents the caller can read |

The agent runs add to this: the 12 restricted-user questions in the fresh run and in both tune runs got the trimmed
answer, with no restricted value in any answer.

## Bar 7: cost and speed

- **0 server AI tokens.** Nothing in `src/Connapse.Storage/Tables` or `src/Connapse.Application/Tables` references
  `IEmbeddingProvider` or `ILlmProvider`: loading, describing, reading and aggregating a table compile to SQL and never
  call a model. The only model work on a CSV is the text path documents already had (its text is embedded for search).
- **`table_aggregate` p95 120 ms on 100,000 rows** (limit 500 ms). `TableAggregateGateTests` uploads a 100k-row CSV
  through the files endpoint, waits for ingestion, then times 100 `table_aggregate` calls over MCP (20 rounds of five
  shapes: a count, a grouped sum with top-k, a filtered average, distinct counts with extremes, a grouped count with
  `having`). p50 23 ms, p95 120 ms, max 129 ms, Release build. It runs in CI's `Category=Gate` step from now on.
- **Agent cost:** $0.0246 per question against rag's $0.0931, a ratio of 0.26 (limit 1.5). Tables answers in 2.49 tool
  calls on average where rag took 4.19.
