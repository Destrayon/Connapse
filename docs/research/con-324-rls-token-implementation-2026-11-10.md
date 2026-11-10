# How should Connapse's table reader enforce row-level security with a per-request token?
**Date:** 2026-11-10
**Status:** Reviewed
**Built on:** rls-raw-sql-agents-2026-11-10.md, con-303-safe-query-layer-2026-11-09.md (no Connapse corpus available in this session)

## Executive summary
The CON-320 design holds on PostgreSQL 16–18 with three corrections, all confirmed on a throwaway PostgreSQL 17.11
container. First, a RESTRICTIVE policy alone shows zero rows, so the document check has to live in a PERMISSIVE
policy (or a permissive `USING (true)` must sit beside it). Second, `COPY FROM` is refused on any table where RLS
applies to the current role, the owner included once `FORCE ROW LEVEL SECURITY` is on, so rows must be copied before
RLS is enabled, in the same transaction. Third, a non-superuser `CREATEROLE` login that creates a role gets only
`ADMIN` on it, not `SET` or `INHERIT`, so a separate owner role is only usable after the login grants it to itself
with `INHERIT TRUE`, at which point it separates nothing. The main uncertainty is Azure Flexible Server: whether the
admin login owns the database (needed to revoke `TEMPORARY` from PUBLIC) is deployment-specific.

## Research brief
**Question:** What exact SQL and Npgsql flow makes PostgreSQL refuse table rows the caller may not see, keyed by a
per-request token the agent can't write, under Connapse's existing CON-303 reader login?
**Sub-questions:** (1) role grants and ownership on PG16+ for a non-superuser admin; (2) RLS policy semantics,
function volatility and plan caching; (3) which server versions are safe and how to keep the token out of logs.
**Out of scope:** `table_sql` (CON-326), hidden columns, memory views.
**Success criteria:** SQL that a prototype proves: zero rows without the right token, all rows with it.

## Role grants and ownership on PostgreSQL 16+
- `GRANT role TO login WITH SET TRUE, INHERIT FALSE` (option order is free) lets the login `SET ROLE` to a
  container's reader role without inheriting its rights; `pg_auth_members` records `set_option`/`inherit_option`
  per grant (PostgreSQL 16 docs, GRANT and pg_auth_members; primary).
- A non-superuser `CREATEROLE` creator is auto-granted the new role `WITH ADMIN TRUE, SET FALSE, INHERIT FALSE`. It
  can grant it to others and drop it, but cannot use it (PostgreSQL 16 role-attributes docs; primary).
- `ALTER TABLE … OWNER TO r` needs ownership, the ability to `SET ROLE r`, and `CREATE` on the schema for `r`.
  `ALTER DEFAULT PRIVILEGES FOR ROLE r` needs INHERIT membership (aclchk.c; primary).
- Role names are cluster-wide and truncated silently at 63 bytes, so per-container names need a database-specific
  prefix and a fixed length (PostgreSQL docs; primary). Connapse uses `connapse_tr_<8 hex of sha256(database)>_<container>`.
- Azure Flexible Server's admin is not a superuser but has `CREATEROLE` and `azure_pg_admin` (Microsoft Learn;
  primary for the role, a training module for the attribute list).

## RLS policy semantics, volatility and caching
- "If only restrictive policies exist, then no records will be accessible" (CREATE POLICY docs; primary). Confirmed
  in the prototype.
- `COPY FROM` errors with "COPY FROM not supported with row-level security" whenever RLS is enabled for the current
  role; FORCE makes that true for the owner (copy.c, rls.c; primary). Confirmed: COPY before `ENABLE`/`FORCE` works.
- A `SECURITY DEFINER` function with a `SET search_path` clause is never inlined (clauses.c; primary). Wrapped as a
  scalar sub-select referencing no outer columns, the whole check becomes an InitPlan run once per query — confirmed
  with `EXPLAIN`: `Filter: (InitPlan 1).col1`. The form `x = ANY ((SELECT f()))` is a parse trap: the double
  parentheses make it a sub-query comparison, `uuid = uuid[]`, which fails. Use `(SELECT x = ANY (f()))`.
- `current_setting(name, true)` returns NULL when never set and `''` after the setting transaction ended, so the
  lookup uses `NULLIF(…, '')` (guc.c; primary for NULL, widely observed for `''`, confirmed in the prototype).
- Plans cached under one role are re-planned under another (plancache.c; primary). CVE-2026-14666 (stale cached
  policies after membership changes) is fixed in 18.6, 17.11, 16.15 (postgresql.org security page; primary).
- With RLS active, `pg_stats` hides the table from the viewer and the planner gives statistics only to leakproof
  operators, so estimates can be worse (system_views.sql, selfuncs.c; primary). Confirmed `pg_stats` returns nothing.
- `DISCARD ALL` resets the role and custom settings, and Npgsql sends it before a pooled connection's next use
  (sql-discard docs, NpgsqlConnector.cs; primary). Connapse still uses only `SET LOCAL` and `set_config(…, true)`.

## Versions and keeping the token secret
- Safe floors: 16.15, 17.11, 18.6 (CVE-2026-14666 and CVE-2026-14668). 17.11 shipped 2026-08-13. Npgsql's
  `PostgreSqlVersion` parses "17.11 (Debian…)" without a round trip (Npgsql source; primary).
- A bound parameter shows as `$n` in `pg_stat_activity`, but `log_statement` and `log_min_duration_statement` log bind
  values unless `log_parameter_max_length = 0` (PostgreSQL logging docs; primary). Operators who log statements
  log tokens; a token lives only a few seconds and is deleted after its query.
- The token is 128 random bits (`RandomNumberGenerator.GetHexString(32, true)`).

## Conflicts and uncertainties
- The two research workers disagreed on FORCE: one advised not forcing RLS so the owner can COPY; the other advised
  copying before enabling RLS. The prototype shows the second works, and keeps FORCE, so Connapse's own login also
  reads no rows.
- Whether Azure's admin owns the application database is unverified; revoking `TEMPORARY` runs under a savepoint and
  logs a warning when refused.

## Gaps — what we did not find
- No Microsoft statement on `createrole_self_grant` or on an admin granting itself a role it created.
- `AtEOXact_GUC` was not read to confirm the placeholder setting outlives its transaction; the prototype shows it does.

## Source quality assessment
Almost every claim rests on PostgreSQL's own docs and source (REL_16_STABLE and master), and each load-bearing one was
also run on PostgreSQL 17.11. The Azure attribute list is from a training module (secondary).

## Sources
- Primary: postgresql.org docs for GRANT, CREATE POLICY, ALTER TABLE, COPY, DISCARD, role-attributes, xfunc-volatility,
  runtime-config-logging; postgres source gram.y, copy.c, rls.c, clauses.c, plancache.c, aclchk.c, guc.c,
  system_views.sql, selfuncs.c; CVE-2026-14666 and CVE-2026-14668 pages; Npgsql NpgsqlConnector.cs; Microsoft Learn
  "Security access control" for Azure Database for PostgreSQL.
- Secondary: Microsoft Learn training module "Describe built-in server roles".
