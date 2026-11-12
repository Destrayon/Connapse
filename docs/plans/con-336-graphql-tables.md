# Plan: GraphQL as a third way for agents to query structured data (CON-336)

Base `epic/con-e6-tables` at e8adda0b (CON-328 merged). Scratch and eval output in `E:\tmp\con-336`.

1. **Research.** Goal: a note on exposing tables and JSON/NoSQL-style data to agents through GraphQL (generated
   schemas, .NET libraries, depth and cost limits, per-tenant scoping), with sources and what we reuse. Done when
   `docs/research/con-336-graphql-for-agents-2026-11-12.md` is committed.
2. **Fix `table_sql`'s listing.** Goal: every table an agent may query is nameable in SQL, not only the first 4,000
   characters' worth. Done when a test with more tables than fit shows each one reachable, and Storage tests pass.
3. **GraphQL view.** Goal: a `table_graphql` tool that answers GraphQL over the caller's readable tables, child tables
   and jsonb columns of one container, through the same access trimming as the structured tools. Done when unit,
   random-input and leak tests (other containers, hidden columns, unreadable documents) pass.
4. **Flexibility set.** Goal: a second question set on varied data (nested JSON, mixed-kind columns, math across
   columns, medians, ranks within groups, totals by month, three-table joins), split tune/test. Done when the data,
   questions and checksums are committed.
5. **Pass bars.** Goal: bars for both sets written in `eval/pass-bars.md` and the PR opened before any scored run.
   Done when the PR is open.
6. **Harness and dry run.** Goal: arm G (GraphQL) in the tables-arms harness and the flexibility set runnable for all
   three arms; dry run on tune questions only. Done when a tune run finishes with all arms answering.
7. **Scored runs.** Goal: one attempt per question per arm on the 180 tables-v1 test questions and the flexibility test
   questions. Done when both reports are conforming.
8. **Decision.** Goal: results and outcome in `docs/research/`, CI-flag build and tests, push, PM told. Done when
   pushed.

## Progress

- Phase 1: four research agents (generated schemas, .NET libraries, safety and scoping, GraphQL for agents) merged
  into `docs/research/con-336-graphql-for-agents-2026-11-12.md`. Decision: GraphQL-Parser plus our own validator over
  a per-caller schema, every read through `ITableQueryService`. Next: build `table_graphql`.
- Phase 2: `TableSqlService.Listing` names every readable table; columns are listed up to 16,000 characters, and
  later tables by name and id, up to 500 tables. Two new tests replace the old cap test; all 31 TableSqlServiceTests
  pass. Commit cdbb8b9d.
- Phase 3: `TableGraphQLService` (GraphQL-Parser 9.5.1, own validator, per-caller schema), `tables.graphql`
  operation, REST route, MCP `table_graphql`, OpenAPI, capabilities and authz entries. 25 unit tests, the random-input
  test, and 34 TableOperationTests pass; `TableGraphQLTests` (leak tests) compiles with CI flags but has not run yet,
  because free memory was 6.1 GB. Next: run `TableGraphQLTests` when memory allows; meanwhile Phase 4.
