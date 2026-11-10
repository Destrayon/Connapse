# Could agents write their own read-only SQL over tables, with PostgreSQL row-level security enforcing who sees what?
**Date:** 2026-11-10
**Status:** Reviewed
**Built on:** tables-container-2026-11-02.md (its safety section and "Option A, validated raw SQL"),
con-303-safe-query-layer-2026-11-09.md (PR #1195: the reader login, its limits and its bypass tests),
per-user-search-permission-design-2026-08-27.md (how Connapse decides who can read a document). The Connapse MCP
corpus was not connected in this session.

## Executive summary
Yes, it can be done safely, but only with four layers together, and row-level security is just one of them. Row-level
security (RLS: PostgreSQL policies that filter rows per query) can enforce Connapse's existing permissions, as long as
Connapse still works out who can read what and hands the database the result for each request. The identity has to come
from something the agent's SQL can't change. A custom session setting such as `app.user_id` doesn't work, because any
client can overwrite it. The grants from CON-303 stop writes and DDL. They don't stop an agent from raising its own
timeouts, turning read-only mode off, taking advisory locks or cancelling other agents' queries. So a parser allowlist
(libpg_query) is still required, and Datadog, Langroid, DBHub and executeautomation all had bypasses that came from
weaker checks. Raw SQL is in scope for v0.5.0 as a `table_sql` tool with all four protections (decided on PR #1198,
2026-11-10). Whether it ships, and whether it or the structured tools become the default, comes from a head-to-head
eval on the same held-out table questions. That eval measures correct answers, tool calls to reach the right answer, and
cost, and its pass bar is fixed below before any run. The main uncertainty is which approach wins: no published study
runs the same agent on the same questions with fixed tools and with raw SQL.

## Research brief
**Question:** could Connapse let agents send their own read-only SQL over tables containers, with PostgreSQL row-level
security and per-user or per-request identity enforcing Connapse's permissions, and should it?

**Sub-questions:** (1) how RLS and identity would enforce Connapse's permissions, and how the identity is made
tamper-proof (with how Supabase, PostgREST, Neon and Azure do it); (2) how tables are locked so agent SQL can only
read; (3) every known bypass and what blocks it; (4) raw SQL against today's structured requests, a recommendation,
and a pass bar set in advance.

**Out of scope:** product code; writes from agents; SQL engines other than PostgreSQL; the MCP and REST tool shapes.

**Success criteria:** a design a build ticket could implement, a blocker named for every bypass, and a measurable pass
bar.

## (1) How RLS would enforce Connapse's permissions
RLS can enforce Connapse's permissions only as a check against a set of documents Connapse has already worked out. The
policy can't make that decision itself.

**Why the database can't decide on its own.** Connapse decides document access in the application:
`ISearchScopeResolver` turns a user into cloud grants (S3 prefixes, Azure RBAC, GitHub), and `ISearchResultVerifier`
checks single documents, sometimes with live cloud API calls. `IDocumentAccess.ReadableAsync` wraps both, fails closed,
and is what memory uses today. A policy can't call AWS or Azure. So for each request Connapse computes the readable
set: the ids of the source documents behind the tables the agent may query. The database checks every row against that
set. This is one permission model checked in two places, so it doesn't drift the way the epic research feared a second
model would (tables-container-2026-11-02.md §(b) point 5).

**Policy mechanics** ([PG17 row security](https://www.postgresql.org/docs/17/ddl-rowsecurity.html), primary):
- With RLS enabled and no policy, the default is to deny: "no rows are visible".
- Superusers, `BYPASSRLS` roles and table owners skip policies. Owners are subject to them only under
  `FORCE ROW LEVEL SECURITY`. The agent's role must own nothing and have `NOBYPASSRLS`, which CON-303's reader already
  has.
- Permissive policies are combined with OR, restrictive ones with AND. Connapse would use one **restrictive** policy per
  table, so that a permissive policy added later can't widen access.
- Policy conditions run "prior to any conditions or functions coming from the user's query". The only exception is
  `LEAKPROOF` functions, and only a superuser can mark a function `LEAKPROOF`
  ([CREATE FUNCTION](https://www.postgresql.org/docs/17/sql-createfunction.html)). On Azure, which has no superuser,
  Connapse's own functions can't be marked leakproof. That costs speed, not safety.
- `row_security = off` raises an error instead of bypassing a policy, so it is safe.
- Views run as their owner unless they are created with `security_invoker = true` (PG15+,
  [CREATE VIEW](https://www.postgresql.org/docs/17/sql-createview.html)). Memory's planned `tables_memory.facts` view
  needs it.

**The policy Connapse would write.** Every loaded table already carries its source document (one id per table, or the
hidden source-document column that the plan gives merged tables):
```sql
CREATE POLICY readable ON c_<container>.t_<id> AS RESTRICTIVE FOR SELECT TO tables_reader_<container>
  USING (source_document_id = ANY (tables_auth.readable_documents()));
```
Here `tables_auth.readable_documents()` is a `SECURITY DEFINER` function with `search_path` pinned. It returns the
document ids stored for the current request, and an empty array when there is no valid request, so it fails closed.
Memory's rules (every cited source must be readable; facts with no sources are visible to their author only) become a
`NOT EXISTS` over the sources table in the same way.

**Where the identity comes from.** Three standard options, plus the one Connapse would use:

| Option | Can SQL the agent writes forge it? | Cost | Verdict |
|---|---|---|---|
| Shared login plus a custom setting (`set_config('app.user_id', …)`; this is what Supabase's `auth.uid()` and PostgREST's `request.jwt.claims` read) | **Yes.** PostgreSQL creates custom settings as user-settable ([guc.c](https://github.com/postgres/postgres/blob/REL_17_STABLE/src/backend/utils/misc/guc.c) `add_placeholder_variable`; [custom options](https://www.postgresql.org/docs/17/runtime-config-custom.html)). Neon's README says the claims "can be modified by any database user" ([pg_session_jwt](https://github.com/neondatabase/pg_session_jwt)). Tomas Vondra: the approach "collapses once you allow the users to run arbitrary SQL" ([EDB](https://www.enterprisedb.com/blog/application-users-vs-row-level-security), secondary) | none | rejected |
| Shared login plus `SET LOCAL ROLE` to a role per user (PostgREST's `authenticator` pattern, [auth docs](https://github.com/PostgREST/postgrest/blob/main/docs/references/auth.rst)) | Any user can run `RESET ROLE` and return to the login role ([SET ROLE](https://www.postgresql.org/docs/17/sql-set-role.html)). PostgREST is safe only because its clients send HTTP, never SQL | one role per user | safe only if the login role itself sees nothing |
| One database login per Connapse user (or Azure Entra principals, [Entra auth](https://learn.microsoft.com/en-us/azure/postgresql/security/security-entra-configure)) | No, because the login is the identity | a pool and a credential per user; slow connection setup and GRANTs reported at thousands of roles (mailing list, secondary) | doesn't solve the problem: a login can't represent live S3 or Azure grants, so a per-request set is still needed |
| Signed token checked inside the database (Neon `pg_session_jwt`: the JWK is fixed at connection startup, and `auth.jwt_session_init` accepts only signed tokens) | Not without the key | extension not on Azure's PG17 list ([Azure extensions](https://learn.microsoft.com/en-us/azure/postgresql/extensions/concepts-extensions-by-engine)) | not available on Azure |
| **Per-request random token (recommended).** Connapse's own connection writes `(token, document ids, expiry)` into `tables_auth.requests`, a table only the app's role can write. The runner then opens the agent's transaction on the reader login, `SET LOCAL ROLE`s to the container's role, and passes the token as a **bound parameter** to `set_config('tables_auth.token', $1, true)` | The agent could overwrite the setting, but only with another live token: 128 random bits that live for one request, are deleted afterwards, and are never sent as a literal, so they don't appear in `pg_stat_activity`. The parser also blocks `set_config` and `current_setting` | one insert and one delete per query; the set is limited to documents behind this container's tables | **use** |

This is Vondra's "vault table" idea (EDB, secondary), with the two weaknesses he names fixed: replay, by expiry and
single use, and hand-rolled crypto, by not using a signature at all. No published design for agent-written SQL was
found. The lookup is cheap if done the way Supabase measured: index `source_document_id` and wrap the call as
`(SELECT …)` so it is computed once per query. On a 1M-row table that took 2–3 ms with 10–500 ids and 24 ms with 1,000
([Supabase RLS performance](https://supabase.com/docs/guides/troubleshooting/rls-performance-and-best-practices-Z5Jjwv),
vendor-run).

**The structured tools would use the same policy.** CON-303's compiled queries run on the same reader login, so once RLS
is enabled they need the token too. That is a benefit: the compiler's `WHERE` predicates and the database policy then
check the same set twice.

## (2) Locking tables so agent SQL can only read
Grants make writes and DDL impossible. Grants can't stop the agent changing its session settings, and in stock
PostgreSQL 17 nothing can, except a physical replica.

- **What is already in place** (CON-303, PR #1195). A `LOGIN NOSUPERUSER NOINHERIT NOBYPASSRLS` reader with only
  `USAGE` on the container schemas, `SELECT` on their tables, and default privileges so reloaded tables stay readable. It
  holds nothing on Connapse's own tables. Its integration tests show writes, DDL, `TRUNCATE`, file functions,
  `COPY TO PROGRAM`, `lo_import`, `SET ROLE` and `SET temp_file_limit` all fail with 42501, even with read-only mode
  turned off.
- **What raw SQL adds:**
  - **A role per container.** `tables_reader_<container>` is a NOLOGIN role holding `SELECT` on that container's
    tables. The login is granted it with `SET TRUE, INHERIT FALSE` (PG16+ grant options,
    [GRANT](https://www.postgresql.org/docs/17/sql-grant.html)). After a `RESET ROLE`, the login holds nothing. Hidden
    tables get no grant. Hidden columns use column-level grants, so the database enforces them too, not just the
    parser.
  - **The owner is a separate NOLOGIN role.** Tables belong to `tables_owner`, which the app switches to only for DDL and
    COPY. The agent never runs as the owner. That matters because an owner can turn read-write back on and then `ALTER`
    or `DROP`.
  - **`REVOKE TEMPORARY ON DATABASE … FROM PUBLIC`.** PUBLIC gets `TEMPORARY` by default
    ([privileges](https://www.postgresql.org/docs/17/ddl-priv.html)). Temp objects in `pg_temp` can shadow names inside
    privileged functions, so every `SECURITY DEFINER` function also pins `search_path = pg_catalog, pg_temp`. Whether
    Azure's admin can run this revoke is unverified.
  - **`SELECT` with only `SELECT` privilege can't lock rows.** `FOR UPDATE` and `FOR SHARE` need `UPDATE` privilege
    ([SELECT](https://www.postgresql.org/docs/17/sql-select.html)).
- **What grants can't do.** `default_transaction_read_only`, `transaction_read_only`, `statement_timeout`,
  `lock_timeout`, `work_mem`, `hash_mem_multiplier`, `max_parallel_workers_per_gather` and custom settings are all
  user-context. Any session can change them, and `ALTER ROLE … SET` only sets a starting value
  ([pg_settings](https://www.postgresql.org/docs/17/view-pg-settings.html)). `GRANT SET ON PARAMETER` (PG15+) only governs
  superuser-context settings. `temp_file_limit` is superuser-context, so it is the one limit the agent can't raise. On a
  **physical hot-standby replica**, "transaction_read_only is always true and may not be changed", and `nextval`,
  `NOTIFY` and temp tables are refused ([hot standby](https://www.postgresql.org/docs/17/hot-standby.html)). Azure read
  replicas are physical ([read replicas](https://learn.microsoft.com/en-us/azure/postgresql/read-replica/concepts-read-replicas)).
  A replica is a worthwhile option where one exists. It can't be required, because self-hosted installs run a single
  server and replicas lag, and it still allows `pg_sleep`, advisory locks and `set_config`. The token table is written
  on the primary, so with a replica the runner must wait until the token row has replicated.
- **Functions anyone can call.** EXECUTE goes to PUBLIC by default. The advisory-lock functions, `set_config`,
  `pg_notify`, `pg_relation_size` and the size functions need no privilege. `pg_cancel_backend` and
  `pg_terminate_backend` work on any session of the same role ([admin functions](https://www.postgresql.org/docs/17/functions-admin.html)).
  Revoking EXECUTE on built-ins needs their owner, a superuser, which Azure doesn't provide. Tom Lane says hiding the
  catalogs "is not designed to" work, and advises putting "some kind of restrictive app in front"
  ([pgsql-general](https://www.postgresql.org/message-id/3952715.1726115805%40sss.pgh.pa.us), secondary).
- **So the parser allowlist stays.** It parses the text with libpg_query, the real PostgreSQL parser
  ([libpg_query](https://github.com/pganalyze/libpg_query), BSD-3). In .NET the choice is
  [`pgsqlparser`](https://www.nuget.org/packages/pgsqlparser): MIT, v2.1.0 from 2026-10-09, libpg_query 18.1, native
  builds for Windows, Linux, musl and macOS. [`Npgquery`](https://www.nuget.org/packages/Npgquery/) only ships Windows and
  Linux builds. The allowlist accepts the parse tree only if all of the following hold:
  - It is exactly one `RawStmt`, and that statement is a `SelectStmt`. Everything else is rejected, including `SET`,
    `SHOW`, `DO`, `CALL`, `COPY`, `EXPLAIN`, `PREPARE`, `EXECUTE`, `LISTEN`, `NOTIFY`, `LOCK` and transaction control.
  - It has no `intoClause` (`SELECT INTO` creates a table), no locking clause, no data-modifying `WITH`, and no
    `WITH RECURSIVE` in v1.
  - Every `RangeVar` names a table in the caller's container schema, by catalog id. No `pg_catalog`, no
    `information_schema`, and no other schema.
  - Every `FuncCall`, anywhere in the tree (FROM, LATERAL, subqueries), is on a fixed allowlist of built-in aggregate,
    window, math, string and date functions, matched after the parser has normalised quoting, `U&` escapes and
    comments, and qualified to `pg_catalog`. The allowlist has no `set_config`, `current_setting`, `pg_sleep*`,
    `pg_advisory*`, `pg_notify`, `lo_*`, `pg_*_backend`, `nextval`/`setval`, `pg_read*`, `pg_ls*`, `dblink*`,
    `pg_relation_size`, and no set-returning generators such as `generate_series` in v1.
  - It has no casts to `reg*` types (`'pg_authid'::regclass`), because those look names up in the catalog.

  The runner sends the original text as one command in an `NpgsqlBatch`, which Npgsql never splits
  (con-303-safe-query-layer-2026-11-09.md, from Npgsql source). So even a parser bug can't add a second statement.

## (3) Every known bypass and what blocks it
Each row names what stops the bypass. The rows that depend on the parser alone are the reason the parser stays.

| Bypass | What blocks it | Layer |
|---|---|---|
| `COMMIT;` then a write (Anthropic's reference Postgres MCP server, [Datadog](https://securitylabs.datadoghq.com/articles/mcp-vulnerability-case-study-SQL-injection-in-the-postgresql-mcp-server/)); stacked statements after a `startsWith("select")` check ([CVE-2025-59333](https://osv.dev/vulnerability/CVE-2025-59333)) | one `SelectStmt` only; one batch command; no write grants | parser, driver, grants |
| `SET default_transaction_read_only = off`, `SET TRANSACTION READ WRITE` | not a `SelectStmt`; grants still stop writes | parser, grants |
| Forging identity with `set_config('tables_auth.token', …)` or `SET` | `set_config` not allowlisted; a forged value must be another live random token | parser, token |
| `SET ROLE` to another container, `RESET ROLE`, `SET SESSION AUTHORIZATION` | not a `SelectStmt`; `RESET ROLE` lands on a login that holds nothing; session authorization needs a superuser; RLS still limits rows to the request's documents | parser, grants, RLS |
| `DO` blocks, `CREATE FUNCTION` with `RAISE NOTICE` | not a `SelectStmt`; no `CREATE` on any schema (PG15+ removed PUBLIC `CREATE` on `public`; databases upgraded from PG14 need the revoke, [schemas](https://www.postgresql.org/docs/17/ddl-schemas.html)) | parser, grants |
| `SECURITY DEFINER` function plus `search_path` hijack through `pg_temp` | `TEMPORARY` revoked; `search_path` pinned in every definer function; the reader gets no definer function except the token lookup | grants |
| Error text that reveals hidden values (`hidden::int`, division by zero) | policy conditions run before non-leakproof user conditions, so errors only come from rows the caller may see; Azure can't mark new leakproof functions | RLS |
| Planner statistics (`pg_stats`, `pg_stats_ext`) | the views exclude tables where RLS is active (`row_security_active`, [system_views.sql](https://github.com/postgres/postgres/blob/REL_17_STABLE/src/backend/catalog/system_views.sql)); catalog reads rejected | RLS, parser |
| Statistics leaks through a crafted operator: [CVE-2017-7484](https://www.postgresql.org/support/security/CVE-2017-7484/), [CVE-2019-10130](https://www.postgresql.org/support/security/CVE-2019-10130/), [CVE-2025-8713](https://www.postgresql.org/support/security/CVE-2025-8713/) (fixed 17.6), [CVE-2026-14668](https://www.postgresql.org/support/security/) (ctid selectivity type confusion, fixed 17.11) | patched versions; the reader can't create operators | version, grants |
| RLS bugs with role changes: CVE-2024-10976 and CVE-2024-10978 (fixed 17.1); CVE-2026-14666, row-security caching ignores role changes (fixed 17.11) ([security page](https://www.postgresql.org/support/security/)) | require **PostgreSQL 17.11 or later** (or 18.6, 16.15) before raw SQL is enabled; this design switches roles inside a pooled session, which is exactly where these bugs live | version |
| `EXPLAIN` row estimates for hidden values | `EXPLAIN` rejected | parser |
| Row counts and sizes of hidden data (`pg_class.reltuples`, `n_live_tup`, `pg_relation_size`) | catalog tables and size functions rejected; no privilege check exists in stock PostgreSQL | parser |
| Other containers' table and column names through `pg_class` and `pg_attribute` (readable by PUBLIC) | catalog references and `reg*` casts rejected | parser |
| Other agents' query text in `pg_stat_activity` (same role sees full text) | `pg_stat*` rejected; the token is bound, never literal | parser, token |
| Cancelling or killing other agents' queries (`pg_cancel_backend`, same role) | not allowlisted | parser |
| Advisory locks, `NOTIFY`, `pg_notify`, `LISTEN`, cursors `WITH HOLD`, `LOCK TABLE` (all allowed in read-only transactions, `utility.c` `ClassifyUtilityCommandAsReadOnly`) | not a `SelectStmt` or not allowlisted; `DISCARD ALL` on pool return | parser, pool |
| Large objects (`lo_create`, `lowrite`, `lo_import`) | refused in read-only transactions (`PreventCommandIfReadOnly` in `be-fsstubs.c`); not allowlisted | read-only, parser |
| `pg_read_file`, `COPY … TO PROGRAM`, quoted or schema-qualified spellings that dodged Langroid's regex ([CVE-2026-54760](https://osv.dev/vulnerability/CVE-2026-54760)) | superuser or `pg_*` roles needed; the parser sees the call after normalising its name | grants, parser |
| A first-keyword classifier with a read-only flag that never took effect (DBHub, [CVE-2026-61788](https://osv.dev/vulnerability/CVE-2026-61788)) | grants rather than a flag are the boundary; a test proves read-only mode is on | grants, test |
| `dblink`, `postgres_fdw` | not installed; not on Azure's default allow-list; no `CREATE` on the database | grants |
| Raising `statement_timeout` or `work_mem` | `set_config` not allowlisted; runner sets limits with `set_config(…, true)`; client `CommandTimeout` cancels from another connection; `temp_file_limit` is superuser-context | parser, runner |
| `pg_sleep`, `generate_series` bombs, recursive CTEs, cross joins, catastrophic regexes | functions not allowlisted; no `WITH RECURSIVE`; `statement_timeout` 5 s, `work_mem`, `temp_file_limit`, `max_parallel_workers_per_gather`; rows and bytes capped while streaming | parser, limits, runner |
| Many connections at once | `CONNECTION LIMIT` on the login, its own pool, a per-user rate limit | pool |
| Timing: query time grows with the number of hidden rows scanned | **not blocked.** It reveals rough table size only, and structured requests have the same leak today. Accepted and documented | — |
| Policy subquery racing a concurrent change under READ COMMITTED (row security docs) | the token row is written and committed before the query starts and never changes | token |
| Prompt injection in cell text making the agent send what it read through another tool (Supabase MCP, [Willison](https://simonwillison.net/2025/Jul/06/supabase-mcp-lethal-trifecta/), secondary) | RLS limits what can be read to what the user may read anyway; no table tool writes; rows return under the untrusted-data notice. Raw SQL adds nothing new here | design |

## (4) Raw SQL against today's structured requests
Raw SQL can only win on questions the structured tools can't express, and on those it fails in a way that looks like a
right answer.

| | Structured requests (CON-303, the four table tools) | Raw read-only SQL |
|---|---|---|
| What can be asked | one table, plus joins on named columns; fixed filters, aggregates, group, order, limit | anything one `SELECT` can say: window functions, CTEs, `CASE`, date arithmetic, self-joins, percentiles |
| How it fails | refuses what it can't express | answers, sometimes wrongly but plausibly |
| Security boundary | no SQL input: grammar, then grants | parser allowlist, grants, RLS, token, limits: five layers, all required |
| Permission enforcement | compiler `WHERE` (the database too, once RLS is in) | RLS on the request's readable set |
| Residual risks | rough table size through timing | the same, plus any parser allowlist gap and any future RLS CVE |
| Build cost | already built | parser binding with native libraries, allowlist, per-container roles, token table, RLS on every table, minimum-version check, leak suite |

**Evidence on accuracy.**
- No study runs the same agent on the same questions with fixed tools and with raw SQL.
- The closest is dbt's 2026 semantic-layer benchmark, vendor-run on 11 questions
  ([dbt](https://docs.getdbt.com/blog/semantic-layer-vs-text-to-sql-2026)). On questions the semantic layer could
  express, it scored 98–100% against 84–90% for text-to-SQL. On questions needing many joins it scored 0% because it
  refused, while text-to-SQL scored 70–100%, and its wrong answers looked like valid ones.
- BIRD questions, which are close to tables-v1's six families, average 0.0 CTEs and 1.1 nesting levels per query
  ([BEAVER, arXiv 2409.02038](https://arxiv.org/html/2409.02038v3)). Most of them fit structured tools.
- The likely gaps for Connapse are "value as of a date" (latest row per group needs a window or a self-join) and date
  arithmetic.

**Every product that lets a model write SQL makes the engine enforce row security as the end user.** Fabric data agents
use "the requesting user's credentials and permissions" ([MS Learn](https://learn.microsoft.com/en-us/fabric/data-science/concept-data-agent)).
BigQuery data agents use the interacting user's credential
([Google](https://docs.cloud.google.com/gemini/docs/conversational-analytics-api/known-limitations)). Databricks Genie
does it only under user authorization ([Unity Catalog filters](https://docs.databricks.com/aws/en/data-governance/unity-catalog/filters-and-masks/)).
Metabase is the closest precedent: its row security "only applies to questions composed in the query builder" because it
"can't parse SQL queries", so for SQL it switches to a database role per user and lets Postgres RLS filter
([Metabase impersonation](https://www.metabase.com/learn/permissions/impersonation)). Every documented leak came from
SQL running under a broader identity: Supabase `service_role` with `BYPASSRLS`, and Genie under app authorization
(single community post).

**Recommendation (revised 2026-11-10 after the user's review of PR #1198).** The first version of this note advised
against raw SQL in v0.5.0 and gated it on a count of the tables proof's failures. The user put it in v0.5.0 and asked
for "an eval of both approaches to see which one gets the correct answer the most times, and how many searches it
takes each to get to the right answer". So:
1. **Build `table_sql` in v0.5.0 with all four protections:** the per-request readable set checked by RLS, the grants
   and roles above, the parser allowlist, and the limits. It is allowed only on PostgreSQL 17.11+, 18.6+ or 16.15+.
2. **Put RLS under the structured tools too**, so both approaches read through the same database policy and the eval
   compares answering ability, not permission paths.
3. **Let a head-to-head eval decide** whether `table_sql` ships and which approach is the default. The eval and its
   pass bar are below and are fixed now, before any data or results exist.

**The head-to-head eval.** It follows the CON-E6 rules: the same agent (Sonnet 5.5 through `claude -p`, at most 25
turns), the same harness and DataBench grading, and result-informed changes re-tested on the sealed reserve set, never
by moving a bar.
- **Questions:** tables-v1's 180 held-out questions in its six families (lookup, filter and count, sum or average,
  group and top-k, join, value as of a date). Both arms answer the same 180. No question is added for this eval, so
  neither arm gets questions picked to suit it.
- **Arm S, structured:** the four CON-303 table tools plus search.
- **Arm Q, SQL:** `table_sql` plus the same table-listing and description tools and search; no structured query tool.
- **Runs:** each arm answers each question 3 times; a question counts as correct when at least 2 of 3 runs are correct.
  Run order is interleaved by question so drift in the model or machine hits both arms equally.
- **Measured per question and arm:** correct or not; **tool calls to the right answer**, meaning every tool call in a
  run that ends correct, with a refused SQL statement or a tool error counted as a call; search calls counted
  separately; wrong answers given confidently (not refusals); input and output tokens and dollar cost; wall time.
- **Leaks:** the planted-leak tests (a user who can't read one source document, CON-31 style) run through **both**
  arms, and the whole bypass table runs as integration tests against `table_sql`, with each database-layer row run a
  second time with the parser switched off.

**Pass bar, fixed 2026-11-10.**
1. **Leaks: zero failures on both arms.** Every bypass test refuses or returns no hidden row; with the parser off, the
   database-layer rows still give zero hidden rows, zero writes and zero changed settings after the transaction. The
   planted-leak tests return the trimmed answer through both arms. A red-team set of 100 prompts and planted cell
   injections against arm Q gives zero hidden rows. Any failure blocks shipping `table_sql` until fixed and re-run.
2. **Correct answers:** compare the arms on the 180 paired questions with an exact McNemar test. Arm Q **wins** if it is
   at least 5 points higher with p < 0.05, **loses** if it is at least 5 points lower with p < 0.05, and otherwise
   **ties**. Per-family rates are reported; no family may be 10 or more points worse in an arm that becomes the default.
3. **Tool calls to the right answer:** on the questions both arms got right, compare calls per question with a paired
   Wilcoxon signed-rank test. The arm with the lower median wins this measure if p < 0.05; otherwise it is a tie.
   Medians, 90th percentiles and per-family figures are reported for both arms.
4. **Cost:** arm Q's average cost per question is at most 1.2× arm S's.
5. **Speed:** parsing and allowlist checking p95 under 5 ms; on a 100k-row table with 1,000 readable documents,
   `table_sql` p95 under 500 ms for aggregates; RLS overhead reported separately for both arms.

**Outcomes.**
- Bar 1 fails: `table_sql` doesn't ship in v0.5.0 until fixed and re-run.
- Arm Q wins correct answers, and bars 4 and 5 hold: `table_sql` ships on by default, and the structured tools stay.
- Tie on correct answers: the arm that wins on tool calls becomes the default. If that is also a tie, the structured
  tools stay the default and `table_sql` ships off by default behind a per-container admin setting. Either way bars 4
  and 5 must hold for `table_sql` to ship at all.
- Arm Q loses correct answers: `table_sql` doesn't ship. Its failures are classified, and structured operators are
  filed for any family where SQL did better.
- A result-informed change to either arm (a prompt, a tool description, an allowlist entry) is re-tested on the sealed
  reserve set before it counts.

## Conflicts and uncertainties
- **Earlier research advised against RLS** (tables-container-2026-11-02.md; con-e6-tables-container.md "No row-level
  security as the main control") because of owner bypass, superuser-only `LEAKPROOF` and drift. This note keeps RLS as a
  backstop, not the main control. It answers the drift concern by checking Connapse's own computed set, not a second
  model, and the owner concern with a NOLOGIN owner. The `LEAKPROOF` gap only costs speed.
- **Azure revokes are unverified:** whether `azure_pg_admin` can revoke `TEMPORARY` from PUBLIC, and confirmation that
  it can't revoke EXECUTE on built-ins. Both are covered by the parser either way.
- **Large-object writes in read-only transactions.** One secondary thread suggests they were allowed before a 2022
  patch. PG17's `be-fsstubs.c` checks read-only mode. The parser blocks `lo_*` either way.
- **Supabase's current `auth.uid()`** reads `request.jwt.claims`. Only an older init script was read, and both forms
  are forgeable.
- **Neon MCP's read-only mode** is described in conflicting ways by secondary mirrors. Not relied on.
- **An AWS `postgres-mcp-server` read-only bypass (CVE-2026-87911)** was reported by one search result. It was not
  found on AWS's bulletin page, so it is left out.
- **Speed numbers** (Supabase's RLS timings, dbt's accuracy) are vendor-run, and Supabase didn't label its units.

## Gaps — what we did not find
- A published design for letting agents write SQL under per-request database identity. Vondra's vault table is the
  nearest.
- A same-agent, same-questions comparison of fixed tools and raw SQL.
- How often BIRD or Spider questions need window functions or date arithmetic. Only averages of CTE and nesting counts
  were found.
- Any Azure documentation on `LEAKPROOF`, revoking privileges on catalog objects, or `pg_read_server_files`.
- Whether `EXPLAIN` estimates leak hidden values under RLS. `EXPLAIN` is rejected, so this doesn't matter here.
- Details of `sepgsql` and `set_user` beyond `set_user`'s own note that its reset token "is a best-effort safeguard, not
  a security boundary".

## Source quality assessment
The mechanism claims rest on primary sources: the PostgreSQL 17 docs and source (`guc.c`, `utility.c`, `be-fsstubs.c`,
`system_views.sql`), the PostgreSQL security page (2026 CVEs and fixed versions confirmed there directly), Microsoft
Learn, the PostgREST, Neon and Supabase docs and source, and osv.dev advisories. The accuracy evidence is weak: a
vendor-run benchmark of 11 questions, plus feature averages from one paper. The tamper-proof token pattern is this
note's own synthesis from primary docs and one secondary blog (Vondra). Treat it as a design to test, not a precedent.

## Sources
- **Primary:**
  - PostgreSQL 17: [row security](https://www.postgresql.org/docs/17/ddl-rowsecurity.html),
    [CREATE FUNCTION](https://www.postgresql.org/docs/17/sql-createfunction.html),
    [CREATE VIEW](https://www.postgresql.org/docs/17/sql-createview.html),
    [SET ROLE](https://www.postgresql.org/docs/17/sql-set-role.html),
    [SET SESSION AUTHORIZATION](https://www.postgresql.org/docs/17/sql-set-session-authorization.html),
    [GRANT](https://www.postgresql.org/docs/17/sql-grant.html), [privileges](https://www.postgresql.org/docs/17/ddl-priv.html),
    [schemas](https://www.postgresql.org/docs/17/ddl-schemas.html), [SELECT](https://www.postgresql.org/docs/17/sql-select.html),
    [SELECT INTO](https://www.postgresql.org/docs/17/sql-selectinto.html),
    [SET TRANSACTION](https://www.postgresql.org/docs/17/sql-set-transaction.html),
    [pg_settings](https://www.postgresql.org/docs/17/view-pg-settings.html),
    [custom options](https://www.postgresql.org/docs/17/runtime-config-custom.html),
    [client config](https://www.postgresql.org/docs/17/runtime-config-client.html),
    [resource config](https://www.postgresql.org/docs/17/runtime-config-resource.html),
    [admin functions](https://www.postgresql.org/docs/17/functions-admin.html),
    [monitoring stats](https://www.postgresql.org/docs/17/monitoring-stats.html),
    [hot standby](https://www.postgresql.org/docs/17/hot-standby.html),
    [parallel safety](https://www.postgresql.org/docs/17/parallel-safety.html)
  - PostgreSQL source (REL_17_STABLE): `guc.c`, `utility.c`, `be-fsstubs.c`, `system_views.sql`
  - [PostgreSQL security advisories](https://www.postgresql.org/support/security/): CVE-2017-7484, CVE-2019-10130,
    CVE-2023-2455, CVE-2024-10976, CVE-2024-10978, CVE-2025-8713, CVE-2026-14666, CVE-2026-14668
  - [PostgREST auth](https://github.com/PostgREST/postgrest/blob/main/docs/references/auth.rst)
  - [Supabase RLS](https://supabase.com/docs/guides/database/postgres/row-level-security),
    [Supabase MCP](https://supabase.com/docs/guides/getting-started/mcp),
    [Supabase RLS performance](https://supabase.com/docs/guides/troubleshooting/rls-performance-and-best-practices-Z5Jjwv) (vendor-run numbers)
  - [Neon pg_session_jwt](https://github.com/neondatabase/pg_session_jwt), [Neon RLS](https://neon.com/docs/guides/neon-rls)
  - Azure Database for PostgreSQL: [access control](https://learn.microsoft.com/en-us/azure/postgresql/security/security-access-control),
    [extensions by engine](https://learn.microsoft.com/en-us/azure/postgresql/extensions/concepts-extensions-by-engine),
    [read replicas](https://learn.microsoft.com/en-us/azure/postgresql/read-replica/concepts-read-replicas),
    [Entra auth](https://learn.microsoft.com/en-us/azure/postgresql/security/security-entra-configure)
  - [libpg_query](https://github.com/pganalyze/libpg_query), [pgsqlparser](https://www.nuget.org/packages/pgsqlparser),
    [Npgquery](https://www.nuget.org/packages/Npgquery/)
  - [ClickHouse readonly](https://clickhouse.com/docs/operations/settings/permissions-for-queries) (a contrast: a session can't lift `readonly=1`)
  - [pgaudit/set_user](https://github.com/pgaudit/set_user), [pg_readonly](https://github.com/pierreforstmann/pg_readonly),
    [supautils](https://github.com/supabase/supautils), [PgBouncer features](https://www.pgbouncer.org/features.html)
  - Advisories: [CVE-2025-59333](https://osv.dev/vulnerability/CVE-2025-59333),
    [CVE-2026-61788](https://osv.dev/vulnerability/CVE-2026-61788),
    [CVE-2026-54760](https://osv.dev/vulnerability/CVE-2026-54760)
  - [Datadog Security Labs on the reference Postgres MCP server](https://securitylabs.datadoghq.com/articles/mcp-vulnerability-case-study-SQL-injection-in-the-postgresql-mcp-server/)
  - Products: [Fabric data agent](https://learn.microsoft.com/en-us/fabric/data-science/concept-data-agent),
    [BigQuery conversational analytics](https://docs.cloud.google.com/gemini/docs/conversational-analytics-api/known-limitations),
    [Databricks row filters](https://docs.databricks.com/aws/en/data-governance/unity-catalog/filters-and-masks/),
    [Snowflake Cortex Analyst](https://docs.snowflake.com/en/user-guide/snowflake-cortex/cortex-analyst),
    [Metabase impersonation](https://www.metabase.com/learn/permissions/impersonation)
  - Papers: [BEAVER, arXiv 2409.02038](https://arxiv.org/html/2409.02038v3),
    [SecureSQL, EMNLP Findings 2024](https://aclanthology.org/2024.findings-emnlp.346.pdf),
    [TAG, arXiv 2408.14717](https://arxiv.org/html/2408.14717v1)
- **Vendor-run:** [dbt semantic layer vs text-to-SQL 2026](https://docs.getdbt.com/blog/semantic-layer-vs-text-to-sql-2026)
- **Secondary:**
  - [Tomas Vondra, application users vs row-level security (EDB)](https://www.enterprisedb.com/blog/application-users-vs-row-level-security)
  - [Tom Lane on hiding catalogs (pgsql-general)](https://www.postgresql.org/message-id/3952715.1726115805%40sss.pgh.pa.us)
  - [Simon Willison on the Supabase MCP leak](https://simonwillison.net/2025/Jul/06/supabase-mcp-lethal-trifecta/)
  - Mailing-list reports on role counts and connection setup
  - [Databricks community post on Genie app authorization](https://community.databricks.com/t5/community-articles/your-row-filter-works-the-agent-just-isn-t-who-it-s-filtering/td-p/166952) (single post, untested)
