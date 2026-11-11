# How should the `table_sql` runner pin names, lex strings, report errors and show the schema?
**Date:** 2026-11-10
**Status:** Reviewed
**Built on:** rls-raw-sql-agents-2026-11-10.md (the four layers), the CON-325 class comment on `TableSqlCheck`.

## Executive summary
The `table_sql` runner (CON-326) sends an agent's checked query through the same `TableQueryRunner` as the structured
tools, with three additions. It sets `search_path` to `pg_catalog, <container schema>, pg_temp` (catalog first, so no
catalog name can ever be shadowed), sets `standard_conforming_strings` on inside the transaction, and keeps sending the
text as the only command of an `NpgsqlBatch`, which skips Npgsql's own SQL rewriting. Database errors are returned as
SQLSTATE class plus message text, never the server-internal fields. The agent sees the generated names (`t_<hex>`,
`c_1…`) in a compact schema listing that carries each column's original header and type, and gets that listing back
with every refusal. The main open risk is version skew between libpg_query (PostgreSQL 18 grammar) and the server.

## Research brief
**Question:** what must the CON-326 runner do beyond `TableQueryRunner` so that the text `TableSqlCheck` allowed is
the text the server runs, with the meaning the check assumed, and so that its errors and schema help agents without
leaking rows?

**Sub-questions:** (1) `search_path` order; (2) string lexing differences between libpg_query, Npgsql and the server;
(3) which PostgreSQL error fields are safe to return under row-level security; (4) how agents should see generated
table and column names.

**Out of scope:** the allowlist itself (CON-325), the RLS policy (CON-324), column grants (CON-304).

**Success criteria:** a concrete setting for each runner choice, with sources.

## Findings by sub-question
### 1. `search_path`: put `pg_catalog` first
PostgreSQL searches `pg_catalog` where it appears in `search_path`, and before everything only when it is left out
(PostgreSQL docs, runtime-config-client). So `<schema>, pg_catalog` lets an object in the container schema shadow a
catalog relation, type, function or operator of the same name. Overload resolution ignores path order between
different argument types (docs, typeconv-func 10.3), so a better-matching user function wins even with the catalog
first; that only matters if someone other than Connapse can create functions in the schema (the CVE-2018-1058 class),
and the reader roles have no `CREATE`. `pg_temp` is never searched for functions or operators. Container tables are
named `t_<hex>` and their row types `t_<hex>`/`_t_<hex>`, which cannot collide with catalog names, so putting
`pg_catalog` first costs nothing and removes shadowing as a class. `TableSqlCheck` already refuses `pg_`-prefixed
table names for either order. Decision: `pg_catalog, <schema>, pg_temp`; the `TableSqlCheck` contract comment is
updated to match. Confidence: high (primary docs).

### 2. String lexing: one lexer to bypass, one setting to pin
libpg_query compiles the server's own `scan.l` with `standard_conforming_strings = true` (postgres REL_18_STABLE
source), so it and the server agree whenever the server's setting is on. The setting has defaulted to on since 9.1 and
is user-settable before PostgreSQL 19 (reported to force it on: Neon's PG19 notes and a pgsql-hackers thread, both
secondary). The server reads it each time it parses, so `set_config('standard_conforming_strings', 'on', true)` in the
setup batch applies to the query that follows. Npgsql sends `client_encoding = UTF8`, which is not a client-only
encoding, so a backslash byte can't hide inside a multibyte character (closes the CVE-2006-2313 class).

The third lexer is Npgsql's: `NpgsqlCommand` with no parameters still runs `ParseRawQuery` for legacy semicolon
batching while SQL rewriting is on (npgsql `NpgsqlCommand.cs`). An `NpgsqlBatch` command is sent as written, which
the existing test `Runner_QueryWithoutColumns_TakesTheDatabaseNamesAndIsNeverSplit` covers. Decision: keep the batch,
pin the setting, and keep `TableSqlCheck`'s NUL refusal (C strings stop at NUL in both the parser and the protocol).
Confidence: high for the source-read points, medium for the PG19 change.

### 3. Errors: SQLSTATE and message text only
Under row-level security the planner runs non-leakproof expressions (casts, most functions) only after the policy
qual: `order_qual_clauses` sorts by security level and `restriction_is_securely_promotable` lets only leakproof quals
move earlier (postgres createplan.c, restrictinfo.c). So a value echoed by a class 22 error (`invalid input syntax
for type integer: "abc"`) comes from a row the caller's token can already read. Class 42 hints come from
`searchRangeTableForCol`, which looks only at tables in the query, so they never name columns of other tables. The
fields that do leak are `InternalQuery`, `InternalPosition` and `Where` (bodies of server-side functions, including
the policy helper), plus `Routine`/`File`/`Line`. Decision: return class 22 and 42 message text and hint (message cut
to 200 characters, since a cast error can echo a whole cell), map 53/54/57014/55P03 to the existing plain messages, and
everything else to a generic message. Confidence: high (source read by the research agent; leakproof markings in the
catalog are trusted).

### 4. Schema presentation: show generated names with their headers
Text-to-SQL studies find the schema format matters about 1–1.5 points (Chang and Fosler-Lussier 2023; DAIL-SQL, Gao
et al. 2024), a few distinct values per column help more than whole rows, and descriptions recover much of the loss
from opaque names (Wretblad et al. 2024, columns renamed A1, A2…). SNAILS (Luoma and Kumar, SIGMOD 2025) finds less
natural names lower accuracy, most for weaker models, and suggests aliasing views, with the gain unmeasured. A
read-only transaction can't create views, and rewriting queries over aliases means agents debug SQL they didn't
write. Decision: the tool takes physical names; refusals, unknown-table and unknown-column errors return a compact
listing — `t_<hex> "Sales" (columns: c_1 "Region" text, c_2 "Amount" numeric, …)` — and the existing `table_list` /
`table_describe` tools already give the same mapping. Revisit with aliases if Connapse's eval shows weaker models
failing on `c_n`.

## Conflicts and uncertainties
- The CON-325 class comment asked for `<schema>, pg_catalog, pg_temp`; this note chooses catalog first. Both are safe
  with today's grants; catalog first stays safe if the schema ever gains `CREATE` by mistake.
- libpg_query uses the PostgreSQL 18 grammar; a server of another major version may lex keywords differently. Connapse
  ships PostgreSQL 17 in Docker today, so the gap is one version; it is refused-by-default for new syntax but not for
  removed syntax.
- CVE-2024-10976 (RLS and cached plans across role changes) is fixed in 16.5/17.1; deployments should be at least that.
- SNAILS and BIRD numbers came from summaries and charts, not the full papers.

## Gaps — what we did not find
- No measured gain for aliasing generated names; Connapse's own eval (v0.5.0 plan) will decide.
- Npgsql's connection reset (`DISCARD ALL`) clearing temp schemas was not checked in source; the reader login has no
  `TEMPORARY` anyway.

## Source quality assessment
Sub-questions 1–3 rest on primary sources: PostgreSQL docs, PostgreSQL and Npgsql source. Sub-question 4 rests on
peer-reviewed text-to-SQL papers, some read through secondary summaries, and vendor docs.

## Sources
**Primary:** postgresql.org/docs/current/runtime-config-client.html; /typeconv-func.html; /runtime-config-compatible.html;
postgres REL_18_STABLE `src/backend/parser/scan.l`, `optimizer/plan/createplan.c`, `optimizer/util/restrictinfo.c`,
`parser/parse_relation.c`; npgsql `src/Npgsql/NpgsqlCommand.cs`; arXiv 2204.00498 (Rajkumar), 2305.11853 (Chang and
Fosler-Lussier), 2308.15363 (DAIL-SQL), 2408.04691 (Wretblad), SNAILS (adalabucsd.github.io/research-blog/snails.html).
**Secondary:** neon.com/postgresql/postgresql-19/breaking-changes; pgsql-hackers message 1131492.1781717349; BIRD
(arXiv 2305.03111) via summaries; MCP Toolbox, DBHub, Snowflake Cortex Analyst, Databricks Genie docs.
