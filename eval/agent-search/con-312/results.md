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

## Why agents didn't follow the link, and a wording trial

Asked for by the PM after the run above. This is diagnostic: it reuses the same questions, so it can't count toward
the bar. Only a run on the sealed reserve set can.

**Cause.** Nothing the agent reads told it that a search result's table id can be used directly:

- The system prompt doesn't mention search or tables.
- `search_knowledge`'s description says nothing about tables.
- `table_list` describes itself as where ids come from, and `table_describe`'s id parameter said "from table_list".

So agents treated `table_list` as the way into tables. Most opened with it before they had seen a passage. A few
called it alongside the first search, before the results came back. Even agents that read the note and its id still
listed tables first.

Established projects don't leave this step to the agent. LlamaIndex's RecursiveRetriever and LangChain's
multi-vector retriever follow the pointer themselves, and AWS Bedrock swaps the table in at retrieval (sources in
`docs/research/con-312-table-extraction-and-passage-markers-2026-11-11.md`, section 3).

**Change** (no bar touched):

- `search_knowledge` description: passages from documents with tables name each table and its id on a `Tables:`
  line and in a note. Pass that id as "table" to `table_read` or `table_aggregate` directly, without
  `table_list`. Search first when the question names a report.
- The `Tables:` line ends "query directly: pass the id as "table" to table_read or table_aggregate; no table_list
  needed".
- The passage note says "Its rows aren't in this passage: pass "table": "<id>" to table_read or table_aggregate to
  query them directly."
- `table_list` adds "If a search result already named the table and its id, query it directly instead."
- `table_describe`'s id parameter says "from table_list or a search result's Tables line".

**Result.** The 20 table questions ran one attempt each, in both setups, at `--workers 1`. Claude Code stopped the
run for low system memory after 29 of the 40 runs, so these figures cover the first 15 questions (linked-001 to
linked-022):

| | Before (same 15) | After (15) |
|---|---|---|
| both-linked follows the link | 0 of 15 | 11 of 15 (73%) |
| both-linked first call is a search | 4 of 15 | 11 of 15 |
| both-linked cost a question | $0.0211 | $0.0287 |
| both-unlinked cost a question | $0.0238 | $0.0346 |
| cost ratio | 0.89x | 0.83x |

The four runs that didn't follow the link were the two marketing-campaign and two IT-asset-register questions. In
both setups the agent opened with `table_list` on those, likely because "campaign results" and "asset register"
already sound like tables. Searching first costs more than listing, because search output is long, so both setups
cost more after the change. The ratio stayed under the 1.2x bar.

## Re-test on the sealed reserve set, with table previews

Run on 2026-11-11 with `tables-linked --reserve --workers 1`. The 30 sealed reserve questions (20 table, 10 prose) were
asked once each in both setups, with no retries. The set was read only by the eval after its SHA-256 matched the
manifest pin, and it is now used. Everything else matched the first run: the same model, config, reports and bar.
Raw data: `tables-linked-reserve.json` and `tables-linked-reserve.runs.jsonl`.

This run has the wording from b9f4645c and the table preview. The first search hit naming a table now shows its first
8 columns and 3 sample rows, and says to query the full table with `table_read` or `table_aggregate` using its id. The
spec was written in PR #1222 before the run.

**Result: bars 1, 3 and 5 pass; bar 2 fails; bar 4 is shown by tests.**

| Bar | Needed | both-linked | both-unlinked | Pass |
|---|---|---|---|---|
| 1. Follows the link | ≥ 16 of 20 table questions | 16 (80%) | — | yes, exactly at the bar |
| 2. Helps | ≥ unlinked on 30, and +4 on table questions | 30 of 30, 20 of 20 table | 30 of 30, 20 of 20 table | no (gain 0) |
| 3. Doesn't distract | ≤ 2 prose table calls, prose ≥ unlinked | 0 calls, 10 of 10 | 0 calls, 10 of 10 | yes |
| 4. No leak | planted-leak tests | `DocumentTableLinkTests`, `PassageTables` unit tests | | tests |
| 5. Cost | ≤ 1.2x unlinked | $0.0323 a question | $0.0352 a question | yes (0.92x) |

- **Bar 1.** both-linked queried a table on all 20 table questions. On 16 of them, it used the id from a search
  result. On the other 4 (reserve-010, 013, 014 and 020), it opened with `table_list`. These were again the
  marketing-campaign, IT-asset-register and supplier-scorecard questions.
- **Bar 2 fails because the unlinked setup already answers every table question.** both-unlinked answered all 20.
  On 15 of them it read the answer straight from the passage text, which still holds the table, and made no table
  call. Both setups got 30 of 30, so a gain of 4 can't happen on this set.
- **Preview size.** A preview averaged 501 characters. A linked search named about 7.4 tables, so previews added
  about 3,700 characters, 28% of its output. That is more than the estimate in the PR, which assumed 3.7 tables per
  search. Even so, a linked search was shorter overall than an unlinked one (13,238 against 13,992 characters),
  because the note replaces the table text.

By the outcome rules in `eval/pass-bars.md`, bar 2 still fails after the re-test, so the build tickets after the
proof are dropped unless the bar's owners decide otherwise. This was the one sealed reserve linked set, so no
further re-test is possible on a sealed set.
