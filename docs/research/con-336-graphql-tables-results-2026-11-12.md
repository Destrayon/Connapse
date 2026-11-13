# Three arms on tables: structured tools, agent-written SQL and GraphQL (CON-336)

**Decision: don't ship `table_graphql`, and delete `table_sql`.** Under the bar fixed before any scored run
(`eval/pass-bars.md`, "Bar for GraphQL and the flexibility set"), GraphQL (arm G) fails two of its four items: it scores
14 fewer questions than the structured tools (arm S) on tables-v1, and it does no better than them on the flexibility
set. Agent-written SQL (arm Q) ties S on both sets, so it misses the 7-question flex margin it needed to stay, and it costs
1.75× S on tables-v1. No arm leaked a row.

Almost all of GraphQL's loss is one family. On the 30 `seen_as_of` questions ("As Connapse saw it on <time>…"), G
got 16 right against 30 for S. `table_graphql` supports the `seen_as_of` argument, but the agent used it in only 5 of the
30 runs and queried current rows in the rest. On every other family G is within one question of S.

## Setup

- Two question sets, three arms, one attempt per question per arm, no reruns of graded runs:
  - tables-v1, test split: 180 held-out questions, 30 in each of six families, 12 asked as a restricted user (540 runs).
  - flex, test split: 70 questions, 10 in each of seven families, all as admin (210 runs). Built in this ticket
    (`eval/datasets/tables/flex/`) to test questions the structured operators were not designed for.
- Agent: `claude-sonnet-5-5` at medium effort through `claude -p`, at most 25 tool calls per run, the same prompt, data
  and DataBench grading as CON-328. Only the tools differ:
  - S: `table_list`, `table_describe`, `table_read`, `table_aggregate`, `search_knowledge`, `get_document`.
  - Q: `table_sql`, `table_list`, `table_describe`, `search_knowledge`, `get_document`.
  - G: `table_graphql`, `table_list`, `table_describe`, `search_knowledge`, `get_document`.
- Flex ran on 2026-11-12 and finished at 17:56Z. tables-v1 ran the same day and finished at 21:39Z, after two pauses for
  low free memory on the host; `--resume` kept every saved run and asked only the missing ones, so no question was
  asked twice in a graded run. Both reports are conforming. Ran at low priority, with Testcontainers and Ollama capped
  at 2 CPUs / 4 GB and Ollama on CPU. The subscription's seven-day use was 79–84% across both runs.
- In the last tables-v1 resume, 138 runs (46 questions × 3 arms) ended in about 4 seconds with "the agent did not get
  the eval's tools": `claude -p` started without the eval's MCP server and never saw a question it could answer. They
  were removed from the journal and asked again with `--resume`; all 138 then ran normally. The originals are kept in
  `scored-v1.runs.with-notools.jsonl`.

## Results against the bar

| Bar item | Result |
|---|---|
| G1. Zero leaked runs on every arm; `TableGraphQLTests` pass | **Pass**: 0 leaked runs on all three arms on both sets; `TableGraphQLTests` 11/11 |
| G2. tables-v1: G at least S − 5 of 180, and 12/12 restricted-user questions | **Fail**: S 179, G 166 (−13; floor 174). Restricted 12/12 |
| G3. flex: G at least S + 7 of 70, exact McNemar p < 0.05 | **Fail**: S 70, G 69 (−1), p = 1 |
| G4. tables-v1: G costs at most 1.2× S per question | **Pass**: 0.978× ($0.0241 against $0.0247) |
| SQL stays only if: flex, Q beats the better of S and G by 7 with p < 0.05 | **Fail**: Q 70, best of S and G 70 (+0), p = 1 |
| …and tables-v1, Q at least S − 5 at no more than 1.5× S's cost | Score passes (179 against 179); cost fails (1.75×) |

GraphQL fails G2 and G3, so it doesn't ship. SQL fails its flex condition, so the bar recommends deleting `table_sql`.

Q's tables-v1 score is result-informed: the listing fix in Phase 2 of this ticket (`TableSqlService.Listing` now names
every readable table) was made after seeing CON-328's results, so the jump from 156 to 179 is not an unbiased estimate.
It doesn't change the outcome, because SQL fails on flex and on cost either way.

### tables-v1 (180 questions)

| | Structured (S) | SQL (Q) | GraphQL (G) |
|---|---|---|---|
| Right | 179 | 179 | 166 |
| Restricted-user right | 12/12 | 12/12 | 12/12 |
| Leaked runs | 0 | 0 | 0 |
| Confident wrong answers | 1 | 1 | 14 |
| Runs with no answer | 0 | 0 | 0 |
| Tool calls per run: mean / median / p90 | 2.77 / 2 / 4 | 3.43 / 3 / 4 | 2.63 / 2 / 4 |
| Wall time per run in seconds: median / p90 | 10.1 / 16.6 | 12.2 / 19.7 | 9.4 / 14.6 |
| Cost per question | $0.0247 | $0.0432 | $0.0241 |
| API list price, all 180 runs | $4.45 | $7.77 | $4.35 |

Per family, each arm got 30/30 except: S sum_avg 29, Q seen_as_of 29, G seen_as_of 16.

Pairwise:
- S against Q: 1 question only S got right, 1 only Q (exact McNemar p = 1). On the 178 both got right, S used fewer tool
  calls (median 2 against 3, Wilcoxon p < 0.001).
- S against G: 14 only S, 1 only G, −7.2 points (p = 0.00098). Tool calls on the 165 both got right: tie (both median 2).
- G against Q: 13 only Q, 0 only G (p = 0.00024); Q costs 1.79× G.

### flex (70 questions)

| | Structured (S) | SQL (Q) | GraphQL (G) |
|---|---|---|---|
| Right | 70 | 70 | 69 |
| Leaked runs | 0 | 0 | 0 |
| Confident wrong answers | 0 | 0 | 1 |
| Tool calls per run: mean / median / p90 | 2.63 / 2 / 5 | 2.96 / 3 / 3 | 2.64 / 2 / 4.1 |
| Wall time per run in seconds: median / p90 | 13.3 / 25.5 | 13.8 / 18.4 | 13.5 / 21.2 |
| Cost per question | $0.0336 | $0.0223 | $0.0334 |
| API list price, all 70 runs | $2.35 | $1.56 | $2.34 |

Every arm got every family right except G on rank_in_group (9/10; flex-test-048, expected 3, answered 4). Tool calls on
questions both got right: S beat Q (median 2 against 3, p = 0.012), G beat Q (2 against 3, p = 0.0018), S and G tied.
SQL was the cheapest arm on flex (0.66× S), the reverse of tables-v1; flex questions are single-table or small joins
where one SQL statement replaces several structured calls, but that saving did not show up as more right answers.

The flex set was meant to find questions the structured tools can't answer. They answered all 70, so this run found no
gap for SQL or GraphQL to fill.

## Why GraphQL lost on seen_as_of

The arms that scored 30 on `seen_as_of` used history in every run: S passed an as-of time to `table_read` or
`table_aggregate` in 30/30 runs, and Q filtered on `_seen_from` / `_seen_until` in 30/30. G passed `seen_as_of` in 5/30
runs and got all 5 right; in the other 25 it queried current rows (for example
`{ inventory(where:{sku:{_eq:"SKU-01866"}}) { sku warehouse on_hand last_counted } }`) and got 11 right, only where the
value hadn't changed since the asked time.

`table_graphql` does support the argument on both `<table>` and `<table>_aggregate`, and the schema text says the table
"keeps history, seen_as_of reads it as of a time". The agent still didn't reach for it, so the likely cause is how
the tool presents history, not a missing feature. A fix (for example, naming `seen_as_of` in the tool description next to
an example) would be a result-informed change and, under the bar, could only be judged on the sealed reserve set.
Even a perfect `seen_as_of` score would leave G3 failing: GraphQL has no flex advantage to ship on.

## Files

- Reports (local): `E:\tmp\con-336\scored-v1.json` and `E:\tmp\con-336\scored-flex.json`, with per-run results in the
  `.runs.jsonl` beside each. `scored-v1.runs.with-notools.jsonl` keeps the 138 runs that never got their tools.
- Harness: `tests/Connapse.Eval/Runs/TablesArmsEval.cs` (`tables-arms`, `--resume`, `tables-arms-score`).
- Bar: `eval/pass-bars.md`, "Bar for GraphQL and the flexibility set".
- Design research for `table_graphql`: `docs/research/con-336-graphql-for-agents-2026-11-12.md`.
