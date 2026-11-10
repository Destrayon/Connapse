# CON-324 — The database enforces who sees which table rows

Ticket 1 of [con-320-raw-sql-rls.md](con-320-raw-sql-rls.md). Research: [con-324-rls-token-implementation-2026-11-10.md](../research/con-324-rls-token-implementation-2026-11-10.md).

1. **Research and prototype** — confirm the role, policy and token SQL on a throwaway PG 17.11. Done when the
   prototype shows zero rows with no token, a wrong token and after `RESET ROLE`, and all rows with the right token.
2. **Database objects** — `TableReaderLogin` creates the `tables_auth` token table and lookup, per-container NOLOGIN
   reader roles granted to the login `WITH SET TRUE, INHERIT FALSE`, revokes `TEMPORARY` from PUBLIC, checks the
   server version; table builds enable and force RLS with the readable-document policy; container delete drops the
   role. Done when it builds.
3. **Runner** — every query writes a token for the caller's readable set, runs under `SET LOCAL ROLE` with the token
   bound by `set_config`, and deletes the token afterwards. Done when the existing table-query tests pass through RLS.
4. **Leak tests** — no token, a forged token, another caller's token, an expired token, a planted app-side leak, the
   login alone after `RESET ROLE`. Done when they pass.
5. **Ship** — Release build with CI's flags, unit and table integration tests, PR into `epic/con-e6-tables`.

## Decisions that differ from the CON-320 plan

- No separate `tables_owner`. The main login would need INHERIT membership in it to drop retired tables, which gives
  it every right of the owner anyway. The main login owns the tables, and `FORCE ROW LEVEL SECURITY` means it reads
  no rows either.
- Rows are copied before RLS is switched on, in the same transaction: `COPY FROM` is refused on a table with RLS.
- One PERMISSIVE policy carries the document check. A restrictive policy alone shows no rows, and only the table owner
  can add a policy that could widen it.
- `security_invoker` views and hidden-column grants are skipped: no memory views or hidden columns exist yet.

## Progress

- Phase 1 done: prototype on postgres:17-alpine (17.11) confirmed every row in the done-condition, plus no temp tables,
  no `SET ROLE` to other roles, and the lookup running once per query as an InitPlan. Scripts in E:\tmp\CON-324.
- Phase 2 done: `TableDdl` (role names, `ProtectSql`, role drop on container delete), `PostgresTableStore` (RLS after
  COPY), `TableReaderLogin` (version check, token store, TEMPORARY revoke, per-container roles, issue/revoke token).
  Storage builds clean. Next: the runner binds the token.
- Phase 3 done: `TableQueryRunner` issues a token, runs under `SET LOCAL ROLE` with `tables_auth.token` bound, and
  revokes it in `finally`; `TableQueryService` passes container and access. Policy SQL needed its own parentheses
  (`USING ((SELECT …))`, `USING (false)`). All 56 `TableQueryTests` pass.
- Phase 4 done: leak tests (no, forged, other caller's and expired token, planted app-side leak, cross-container
  query, login after `RESET ROLE`, forging from the container role, token deleted, owner forced, role dropped) pass.
  CSV upload tests time out locally without host Ollama; the load gate passes alone. Next: Release build and PR.
