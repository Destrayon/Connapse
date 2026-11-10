# How should CON-303 keep agents' table queries read-only and bounded, on PostgreSQL 17 and Azure Flexible Server?
**Date:** 2026-11-09
**Status:** Reviewed
**Built on:** tables-container-2026-11-02.md, con-301-table-storage-2026-11-09.md (Connapse MCP was not connected, so no corpus search)

## Executive summary
Grants are the only real boundary for an agent's table queries; read-only transactions and per-role settings are not. A login that holds `USAGE` on the tables schemas and `SELECT` on their tables, and nothing else, blocks every bypass in the epic's list from writing durable data, reading files, running programs or reaching other servers. Read-only mode and session limits only hold if the agent can't send its own statements, so Connapse sends exactly one compiled `SELECT` per call, with positional parameters, which Npgsql passes to the server unsplit, so a second statement is a server error. On Azure Flexible Server, the server admin can create that login and set its `statement_timeout`, `work_mem`, `lock_timeout` and `default_transaction_read_only`. Whether it can set `temp_file_limit` per role is undocumented. Connapse tries it, and if Azure refuses it logs a warning and the server-wide parameter applies instead.

## Research brief
**Question:** How should CON-303 make agent table queries unable to write, escape read-only mode or run unbounded, on self-hosted PostgreSQL 17 and on Azure Flexible Server?
**Sub-questions:** (1) What does Azure Flexible Server let its admin do with a new login role: create it, set per-role limits, set `temp_file_limit`, and which extensions can be enabled? (2) For each bypass in the epic research, what stops it in PostgreSQL 17? (3) How do Npgsql's positional parameters, connection reset and batches behave, and what does `ALTER DEFAULT PRIVILEGES` cover?
**Out of scope:** joins and the MCP/REST tools (ticket 8), hidden columns and per-document permissions (ticket 7).
**Success criteria:** a blocker named for every bypass, an answer on Azure per-role settings, and Npgsql facts the runner can rely on.

## Findings: Azure Flexible Server and the reader login
The Azure server admin can create and configure the reader login; only `temp_file_limit` per role is uncertain.
- **Creating the login: yes (high).** The server admin has `NOSUPERUSER, INHERIT, CREATEDB, CREATEROLE`, and Microsoft tells admins to use it "to create less privileged users and roles." Only Microsoft's `azuresu` is a superuser. `azure_pg_admin` is a "pseudo-superuser" (primary: Azure docs, security-manage-database-users and security-access-control).
- **Per-role settings: yes (high).** PostgreSQL lets a `CREATEROLE` role change defaults for roles it has ADMIN OPTION on, and since PostgreSQL 16 the creator gets ADMIN OPTION on every role it creates (primary: PostgreSQL 17 `ALTER ROLE` and role-attributes docs). Azure documents per-role parameters via `ALTER USER` but gives no examples (primary, single source: Azure concepts-parameters).
- **`temp_file_limit` per role: unknown.** PostgreSQL allows it only for superusers or roles granted `SET` on the parameter, and no Azure page says `azure_pg_admin` has that grant. Azure documents it only as a server parameter (dynamic, default -1). **Decision:** Connapse runs `ALTER ROLE … SET temp_file_limit`, and on error 42501 logs a warning that tells the operator to set the server parameter `temp_file_limit` in the portal or CLI.
- **dblink and postgres_fdw: not available by default (high).** `azure.extensions` is empty by default on PostgreSQL 17 and `CREATE EXTENSION` fails for anything not on that list. Both can be allow-listed, so an operator must not add them.
- **Server-file roles (`pg_read_server_files`, `pg_execute_server_program`): undocumented on Azure.** Only forum answers say they are blocked. The reader never gets them either way.
- **Extra:** on PostgreSQL 16 and 17 Azure grants `pg_read_all_data` to `azure_pg_admin`. The reader login must not be made a member of `azure_pg_admin`.

## Findings: what stops each bypass in PostgreSQL 17
Every bypass is stopped by the reader's grants plus Connapse never sending more than one compiled statement; read-only mode alone stops none of them for durable tables.
- **`COMMIT;` then a write.** A `COMMIT` does end the read-only transaction, and a pooled session can then be changed. What it can't do is write a durable table, because the role has no write grants. Datadog's write-up of the PostgreSQL MCP server injection reaches the same conclusion: restricted privileges hold "even outside the transaction" (secondary: Datadog Security Labs). Connapse also blocks the escape itself: there is no SQL input, and one positional-parameter statement can't carry a second statement.
- **`SET default_transaction_read_only = off`, `set_config`, `SET TRANSACTION READ WRITE`.** All are user-settable. The PostgreSQL docs call read-only "a high-level notion" that "does not prevent all writes to disk" (primary). Grants stop the writes, and these commands can't be expressed in a request.
- **`SET ROLE` / `RESET ROLE`.** `SET ROLE` needs the SET option on the target role, which the reader has on none; `RESET ROLE` only returns to the session user (primary).
- **`pg_read_file`, `COPY … TO PROGRAM`, server-side `lo_import`.** Superuser-only by default, or need `pg_read_server_files`, `pg_write_server_files` or `pg_execute_server_program`, none of which the reader has (primary).
- **dblink, postgres_fdw.** `CREATE EXTENSION` needs superuser unless the extension is trusted, and neither is on PostgreSQL 17's trusted list; a trusted extension would still need `CREATE` on the database, which the reader lacks (primary).
- **`pg_sleep`, huge sorts, cartesian joins.** Not expressible: the compiler has no function calls, no joins and no subqueries. Limits back that up. `statement_timeout`, `work_mem` and `lock_timeout` are set on the role and again with `SET LOCAL` per query, and the client sets its own command timeout. `temp_file_limit` is the one setting the reader can't change itself. `work_mem` applies per sort or hash step, not per query.
- **Residual: temp tables and large objects.** PUBLIC has `TEMPORARY` on databases by default, and any role can likely create large objects. Both are blocked inside a read-only transaction and are reachable only through a `COMMIT` escape, which Connapse's one-statement rule prevents. Revoking `TEMPORARY` from PUBLIC is a database-wide change, so Connapse doesn't do it; operators who want it can.
- **Residual: the reader's own session defaults.** PostgreSQL lets an ordinary role run `ALTER ROLE` … `SET` on itself, so raw SQL on the reader login could, for example, set its own default `statement_timeout` to 0. Three things contain this: agents never send SQL; the runner sets every limit again with `set_config(..., true)` inside each transaction, which beats the role default; and each process start re-applies the role defaults. Settings that need superuser (such as `temp_file_limit`) are out of the reader's reach.
- **Residual: catalog reads.** `pg_catalog`, including `pg_roles` and who is connected in `pg_stat_activity`, is readable by everyone. Other schemas' tables are not, since PostgreSQL grants PUBLIC nothing on tables or non-public schemas. Only the compiler's own queries reach the server, so an agent can't read the catalog anyway.
- **Database upgraded from PostgreSQL 14 or earlier:** PUBLIC still has `CREATE` on schema `public` there (primary: ddl-schemas). A test checks the reader can't create in `public` on the test server, which is a fresh PostgreSQL 17.

## Findings: Npgsql and default privileges
Npgsql 10 sends positional-parameter commands unchanged, resets pooled sessions, and has no read-only transaction option.
- **Positional parameters are never split (high).** With only unnamed parameters (`$1`, `$2`), Npgsql skips its SQL rewriting and sends the text as one extended-protocol statement; the source comment says a semicolon then makes "PostgreSQL … error (this behavior is by-design)." Confirmed in `NpgsqlCommand.cs` at v8.0.0, v10.0.0 and main (primary: Npgsql source; 6.0 release notes). A command with **no** parameters is still split on semicolons, so the compiler always binds at least one parameter or runs inside an `NpgsqlBatch` command, which is never split.
- **Session reset (high).** Npgsql resets a pooled connection with `DISCARD ALL` (or equivalent statements that keep prepared statements), sent with the first statement after the connection is reopened. `No Reset On Close` would skip it, so the reader's pool leaves it off (primary: Npgsql performance and connection-string docs).
- **Read-only transactions (medium).** Npgsql 10 has no read-only option on `BeginTransaction`; one exists only on unreleased main. `BeginTransaction` sends nothing until the first command, so running `SET TRANSACTION READ ONLY` as that command meets PostgreSQL's rule that it come before any query (inference from Npgsql source plus PostgreSQL docs).
- **Batches (high).** An `NpgsqlBatch` sends its commands in one round trip, each as its own statement; if one fails the rest are skipped (primary: Npgsql basic-usage docs).
- **Default privileges (high).** `ALTER DEFAULT PRIVILEGES IN SCHEMA s GRANT SELECT ON TABLES TO r` covers tables later created in `s` by the role that ran it, and nothing that already exists, so Connapse pairs it with `GRANT SELECT ON ALL TABLES IN SCHEMA s`. Dropping the schema removes the default-privileges entry automatically (source: `aclchk.c`, single source); dropping the reader role first needs `DROP OWNED BY` (primary).

## Decision
- The compiler turns a read or aggregate request into one `SELECT` using only the catalog's generated names, quoted, with every value a positional parameter.
- The reader is a `LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS` role with its own pool. It gets `USAGE` on a container's tables schema and `SELECT` on its tables (now and by default later), on first query.
- Each query runs in one batch: `SET TRANSACTION READ ONLY`, the limits via `set_config(…, true)`, then the `SELECT`, then a rollback. Role-level settings and the client's command timeout back this up.
- `temp_file_limit` is set on the role where allowed; otherwise the operator sets the server parameter.

## Conflicts and uncertainties
- **Azure `temp_file_limit` and server-file roles** are undocumented; nothing here was run against a live Azure server, so the fallback path is what makes the outcome safe either way.
- **Large-object creation by any role** is believed but not documented. It is unreachable while only compiled `SELECT`s run.
- **The read-only transaction route** rests on Npgsql source, not docs.

## Gaps
- No live Azure Flexible Server test (the PM's rule: no data on shared servers; there is no test Azure server).
- Not measured: how much memory `work_mem` × parallel workers can reach on the smallest Azure tier.

## Sources
**Primary:**
- PostgreSQL 17 docs: sql-createrole, sql-alterrole, role-attributes, sql-set-role, sql-set-transaction, runtime-config-client, runtime-config-resource, functions-admin, sql-copy, predefined-roles, lo-funcs, sql-createextension, contrib, ddl-priv, ddl-schemas, sql-grant, sql-alterdefaultprivileges, sql-createfunction, catalog-pg-authid, monitoring-stats
- PostgreSQL source: `src/backend/catalog/aclchk.c` (REL_17_STABLE)
- Azure Database for PostgreSQL Flexible Server docs: security-manage-database-users, security-access-control, concepts-parameters, parameters-resource-usage-disk, server-parameters-table-customized-options, concepts-extensions-considerations, concepts-extensions-versions
- Npgsql docs: connection-string-parameters, performance, basic-usage, release-notes/6.0
- Npgsql source: `src/Npgsql/NpgsqlCommand.cs` (v8.0.0, v10.0.0, main)

**Secondary:**
- Datadog Security Labs, "MCP vulnerability case study: SQL injection in the PostgreSQL MCP server"
- Microsoft Q&A threads on server-file access in Flexible Server (unofficial)
