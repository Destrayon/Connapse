# How should Connapse trim tables, rows and columns to what each caller may read?
**Date:** 2026-11-10
**Status:** Reviewed
**Built on:** rls-raw-sql-agents-2026-11-10.md, con-324-rls-token-implementation-2026-11-10.md (no Connapse corpus available in this session)

## Executive summary
No vendor trims table metadata to the caller's rows for them; the ones that say anything either block previews and
statistics (BigQuery, SQL Server) or compute counts over permitted rows only (Snowflake `COUNT(*)`). Connapse's
`table_describe` therefore computes its row count and sample rows by running a query through the same filters as a
read, and drops every table-wide field that a hidden row can change: the catalog count, version and loaded-at time.
Hidden columns are refused wherever a request names them, with the same error as a column that doesn't exist, as
Looker and PostgreSQL do. Merged tables carry a per-row source-document column that both the compiler's `WHERE` and a
row-level-security policy check. The main uncertainty is that several vendors' catalog counts are inferred to be
unfiltered from their docs' silence, not stated.

## Research brief
**Question:** How should rows, tables, columns and describe output follow source-document permissions and
admin-hidden columns, failing closed when access can't be checked?
**Sub-questions:** (1) hidden-column semantics across select, filter, order, group and aggregate, and the error
shown; (2) which describe metadata leaks hidden rows; (3) per-row provenance for merged tables and its RLS policy.
**Out of scope:** hidden whole tables (ticket 15), MCP/REST tools (ticket 8), raw SQL (CON-326), timing side channels.
**Success criteria:** a design each part of which a planted-leak test can check.

## Hidden columns: refuse every use, answer as for a missing column
- Microsoft Data API builder rejects an excluded field in `$select`, `$filter`, `$orderby` and, in GraphQL, group-by and
  aggregate arguments; its MCP `describe_entities` lists only allowed columns (learn.microsoft.com authorization
  overview; data-api-builder source `RestRequestContext.cs`, `DataApiBuilderException.cs`, `DescribeEntitiesTool.cs`;
  primary). It answers 403 for excluded and 400 for missing fields, which confirms existence.
- PostgreSQL needs `SELECT` on every column a query uses, in any clause; `count(*)` needs none; the error names the
  table, not the column; `information_schema.columns` omits ungranted columns (PostgreSQL 17 SELECT and
  information_schema docs, `execMain.c`; primary).
- Looker's access grants answer "no longer exists … or you don't have access to it", one message for both cases
  (Looker docs; primary). Cube.dev gives hidden and denied members the same error (Cube source; primary).
- Snowflake projection policies allow a hidden column in `WHERE` (Snowflake docs; primary), which lets range filters
  recover values; Connapse does not copy that.
- **Decision:** a hidden column is treated exactly as a nonexistent one: not listed, not in select-all, refused in
  select, filter, order, group by and aggregates, and left out of error column lists and ambiguity counts.

## Describe metadata: compute from visible rows, drop change fields
- Snowflake: with a row access policy, `COUNT(*)` returns statistics "only based on what is permissible to access";
  `INFORMATION_SCHEMA.TABLES.ROW_COUNT` and `LAST_ALTERED` say nothing about policies (Snowflake docs; primary docs,
  inferred unfiltered).
- BigQuery: previews and table sampling are incompatible with row-level security; error messages and timing are
  listed as inference channels (Google Cloud docs; primary).
- SQL Server: `DBCC SHOW_STATISTICS` reports unfiltered data and is restricted; CDC and change tracking leak filtered
  rows and keys (Microsoft Learn row-level security; primary). Change and version metadata leak.
- LangChain `SQLDatabase` samples three rows with `LIMIT` and no `ORDER BY` (langchain-community source; primary).
- **Decision:** describe shows the visible row count and three sample rows from a trimmed query ordered by content
  (source document first, then visible columns), so changing a hidden row can't change which visible rows are sampled.
  It omits the catalog count, version and loaded-at, and shows rejects only from readable documents and visible columns.

## Merged tables: a per-row source column, checked twice
- Azure AI Search fails closed: a failed permission check returns 5xx, never a partial result, and a document passes if
  any one permission source passes (Microsoft Learn; primary). Elasticsearch ORs role queries and warns aggregates can
  reveal terms from inaccessible documents (Elastic docs; primary).
- PostgreSQL policy expressions run per row before the user's conditions; a NULL or empty readable set hides all rows
  (PostgreSQL 17 row-security docs; primary). A non-correlated sub-select becomes an InitPlan run once (Tom Lane on
  pgsql-hackers; secondary). Supabase measured `= ANY(ARRAY(select f()))` at 2–3 ms versus timeouts for the bare call,
  and advises filtering in the query as well as in RLS (Supabase docs; secondary).
- CVE-2014-8161 and CVE-2019-10130 show error text and statistics from hidden rows as real leaks (postgresql.org;
  primary).
- **Decision:** a merged load stores each row's source document in a hidden `_document uuid NOT NULL` column and the
  catalog's `source_documents`. The table is listed when any source is readable. The compiler adds
  `"_document" = ANY($n)` with the readable sources; the RLS policy checks
  `"_document" = ANY ((SELECT tables_auth.readable_documents())::uuid[])`, keeping the call uncorrelated.

## Conflicts and uncertainties
- One worker read #1206's single-document policy as per-row. It isn't: it compares a literal, so it is already an
  InitPlan (confirmed in #1206's EXPLAIN). The per-row merged policy is the one that needs the uncorrelated form.
- `x = ANY ((SELECT f()))` is a parse trap per con-324's prototype; the cast form is verified by this PR's tests.
- Catalog counts at Snowflake and BigQuery being unfiltered is inferred from silence.

## Gaps — what we did not find
- No vendor guidance on rejected-row lists or schema inference from hidden rows; treating them as leaks is analogy.
- No source on whether to list an object all of whose sources are unreadable; Connapse doesn't list it.
- Hidden columns are enforced by the compiler only. Column grants (database enforcement) conflict with #1206's
  table-wide grants and are left to CON-326, which is the first path where an agent writes SQL.

## Source quality assessment
Mostly primary: vendor docs and source for DAB, PostgreSQL, Snowflake, BigQuery, SQL Server, Looker, Cube, Elastic,
Azure AI Search. Supabase's performance numbers and the InitPlan mailing-list post are secondary.

## Sources
- Primary: learn.microsoft.com (DAB authorization overview; Azure AI Search RBAC enforcement and security trimming; SQL
  Server row-level security); github.com/Azure/data-api-builder; postgresql.org docs (sql-select, infoschema-columns,
  ddl-rowsecurity, xfunc-volatility, indexes-types) and CVE-2014-8161, CVE-2019-10130; docs.snowflake.com
  (projection policies, security-row-intro, info-schema tables, show-tables); docs.cloud.google.com (BigQuery
  row-level and column-level security; Looker required_access_grants, field hidden); Cube.dev source; Elastic
  document- and field-level security; langchain-community `sql_database.py`.
- Secondary: Supabase "RLS performance and best practices"; pgsql-hackers posts by Tom Lane, Robert Haas, Bruce Momjian.
