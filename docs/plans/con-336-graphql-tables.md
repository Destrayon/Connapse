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
   three arms; dry run on tune questions only. Done when `TableGraphQLTests` pass (moved here from Phase 3: it needs
   containers, which wait on free memory) and a tune run finishes with all arms answering.
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
- Phase 4: `eval/tools/tables/build_flex.py` (seed 20261113) writes six files for one invented retailer and 84
  gold-SQL questions (7 families × 12; 14 tune, 70 test) to `eval/datasets/tables/flex/`, with a card and
  MANIFEST entries. Rerun is byte-identical. Next: bars for both sets in `eval/pass-bars.md`, then open the PR.
- Phase 5: "Bar for GraphQL and the flexibility set" added to `eval/pass-bars.md` (three arms, one attempt each;
  G ships on no loss within 5 on tables-v1, +7 on flex with McNemar p < 0.05, cost ≤ 1.2× S; SQL kept only if it
  beats both by 7 on flex). The flex card now names that bar. Next: push and open the PR.

- CI fix (4f85aa60, pushed with ee72cbc4): integration (3) failed because table_graphql had no entry in authorization-expectations.json; integration (2) failed because TableGraphQLTests' last check matched the query alias `planted` rather than a row value (no leak: that list came back empty). Both fixed; integration build with CI flags is clean. Next: wait for the PM, then the TableGraphQLTests run and tune dry run.
- Phase 6: TableGraphQLTests 11/11 pass locally. Tune dry run (4 questions per set, all 3 arms): every arm 4/4 on both sets, 0 leaked runs; tables-v1 cost per question S $0.027, Q $0.045, G $0.019. Ollama for search runs as con336-ollama (2 CPU, 4 GB, CPU only). Subscription seven-day use was 79% at the start. Next: scored runs, flex first, then tables-v1.
- Phase 7 (paused): flex scored run finished and conforms (E:\tmp\con-336\scored-flex.json): S 70/70, Q 70/70, G 69/70, 0 leaks. tables-v1 was killed by Claude Code at 74/540 runs when free memory fell to ~4 GB (user gaming); my orphaned eval containers and con336-ollama removed. Added tables-arms --resume (0d7d2739) so the restart keeps the 74 saved runs. Next: when ClaudePM says memory is above 8 GB, start con336-ollama and rerun the tables-v1 command with --resume.

- 2026-11-12 paused again at PM request (93% memory used): eval process and its four containers (con336-ollama, pgvector, minio, ryuk) removed; 164/540 tables-v1 runs saved in E:\tmp\con-336\scored-v1.runs.jsonl. Next: on PM go, restart con336-ollama and run resume.ps1 (--resume).
- 2026-11-12 resumed run was killed by Claude Code at ~3 GB free memory; 363/540 tables-v1 runs saved. Eval process gone, its testcontainers cleaned up, con336-ollama removed. Next: on PM go, restart con336-ollama and resume.ps1 again.
- Phase 7 done (2026-11-12): tables-v1 finished, 540/540 runs, conforming. 138 runs that started without the eval's tools were dropped and re-asked with --resume. S 179, Q 179, G 166 (seen_as_of 16/30); flex S 70, Q 70, G 69; 0 leaks anywhere. con336-ollama removed.
- Phase 8 done: docs/research/con-336-graphql-tables-results-2026-11-12.md. GraphQL fails bars G2 and G3, so it doesn't ship; SQL misses the flex margin and 1.5x cost, so the bar recommends deleting table_sql. Next: PM decides on the follow-up tickets (delete table_sql, drop or park table_graphql).
