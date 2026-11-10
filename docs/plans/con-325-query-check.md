# Plan: check every agent-written query before it runs (CON-325)

Ticket b of `docs/plans/con-320-raw-sql-rls.md`. A standalone check in `Connapse.Storage/Tables`; no tool, no runner,
no RLS (CON-303, CON-324 and CON-326 own those).

1. **Parser library.** Goal: confirm the parser choice from the packages themselves. Done when
   `docs/research/sql-parser-library-2026-11-10.md` names the pick and its sources.
2. **The check.** Goal: `TableSqlCheck` parses with `pgsqlparser` and allows only one plain `SELECT` of the
   container's tables, allowlisted functions and types, refusing every other node kind by default. Done when it builds
   in Release with CI's flags.
3. **Tests.** Goal: one refused test per parser row of the research's bypass table, branch tests for every rule, a
   CsCheck random-input test and a p95 timing test. Done when `Connapse.Storage.Tests` passes.
4. **PR.** Goal: PR into `epic/con-e6-tables`, ready for review. Done when it is open and the PM is told.

## Progress

- Phase 1: picked pgsqlparser 2.1.0 (libpg_query 18.1.0); evidence in docs/research/sql-parser-library-2026-11-10.md.
- Phase 2: `src/Connapse.Storage/Tables/TableSqlCheck.cs`, default-deny walk of the protobuf tree with scoped CTE names;
  Release build with CI's flags is clean.
- Phase 3: `tests/Connapse.Storage.Tests/Tables/TableSqlCheckTests.cs`, 171 tests (every parser row of the bypass table,
  each rule's branches, CsCheck random input, p95 under 5 ms); Storage unit tests 544/544 pass.
- Next: commit, push, open the PR into `epic/con-e6-tables`.
