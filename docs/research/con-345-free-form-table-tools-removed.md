# The free-form table query tools are removed (CON-345)

**Decision: agents read tables only through the structured table tools** (`table_list`, `table_describe`,
`table_read`, `table_aggregate`). `table_sql` (agent-written SQL) and `table_graphql` (agent-written GraphQL) are removed
from MCP, the REST API and the code before either shipped, together with the per-container `Tables.AgentSql` setting
that existed only to turn `table_sql` on.

Two scored runs against bars fixed before any scored question was asked showed neither tool earns its place:

- CON-328 ([PR #1221](https://github.com/Destrayon/Connapse/pull/1221),
  [results](con-328-table-head-to-head-2026-11-12.md)): `table_sql` lost to the structured tools on right answers, tool
  calls and cost on tables-v1.
- CON-336 ([PR #1229](https://github.com/Destrayon/Connapse/pull/1229),
  [results](con-336-graphql-tables-results-2026-11-12.md)): with its table listing fixed, `table_sql` tied the structured
  tools (179 of 180 on tables-v1, 70 of 70 on the flexibility set) at 1.75× their cost. `table_graphql` scored 166 of 180
  and 69 of 70. Neither beat the structured tools on the questions they were not designed for, which was the condition
  for keeping either.

Each extra tool also costs every agent tokens in its tool list and one more choice per question, so a tool that answers
nothing the others can't is a net loss.

## What was removed

- MCP tools `table_sql` and `table_graphql`; REST `POST /api/containers/{containerId}/tables/sql` and `/tables/graphql`.
- Their operations, services, SQL checker, GraphQL schema, statuses (`agent-sql-off`, `query-not-run`) and tests.
- The `tables` block of container settings overrides (`Tables.AgentSql`).
- The `GraphQL-Parser` and `pgsqlparser` packages, which only these tools used.
- The SQL and GraphQL arms of the `tables-arms` eval; it now runs the structured arm only.

The table reader login, row-level security and the query runner stay: the structured tools run on them.

## The structured arm after the removal

Rerun on 2026-11-13 (00:45Z to 01:28Z) from this branch: tables-v1 test split, 180 questions, arm `structured` only, one
attempt per question, the same agent (`claude-sonnet-5-5`, medium effort, at most 25 tool calls), prompt, data and
grading as CON-336. The report is conforming. Ran at low priority with Testcontainers and Ollama capped at 2 CPUs / 4 GB.

| | CON-336 structured | CON-345 structured |
|---|---|---|
| Right | 179 | 179 |
| Restricted-user right | 12/12 | 12/12 |
| Leaked runs | 0 | 0 |
| Confident wrong answers | 1 | 1 |
| Tool calls per run: mean / median / p90 | 2.77 / 2 / 4 | 2.87 / 3 / 4 |
| Wall time per run in seconds: median / p90 | 10.1 / 16.6 | 8.2 / 12.1 |
| Cost per question | $0.0247 | $0.0264 |
| API list price, all 180 runs | $4.45 | $4.76 |

Both runs miss the same question, `test-085` (sum_avg); every other family is 30/30. The removal changes nothing the
structured tools answer. The arm's tools and prompt are unchanged, so the 7% cost and 0.1-call differences come from
re-asking the model once per question, not from the removal.
