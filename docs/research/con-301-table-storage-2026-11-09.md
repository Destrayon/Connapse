# How should a tables container store its tables, catalog them, and swap in a reload atomically?
**Date:** 2026-11-09
**Status:** Reviewed
**Superseded in part (2026-11-10, CON-319):** there is no longer a separate tables container type. Any documents container holds any number of tables beside its files, and memory containers hold none; read "tables container" below as "a container's tables".
**Built on:** con-300-tables-container-kind-2026-11-09.md, docs/plans/con-e6-tables-container.md (Connapse MCP was not connected, so no corpus search)

## Executive summary
CON-301 gives each tables container its own PostgreSQL schema (`tables_<container id>`), with a catalog in Connapse's own schema that records every table. A reload never rewrites a live table. It loads into a new table with a fresh generated name, then moves the catalog entry to point at it, all in one transaction. A reader therefore sees either the old table or the new one, and never waits on a table lock. Old tables go on a retired list and are dropped after a grace period. Deleting the container drops its schema with `CASCADE` in the same transaction that deletes the container row. The main uncertainty is scale: tens of thousands of schemas or tables make catalog memory, `pg_dump` and lock counts expensive. That doesn't matter at today's sizes, but would if Cloud tenants grew large.

## Research brief
**Question:** How should Connapse store a tables container's tables (a schema per container), catalog them, swap in a reloaded table without readers seeing half a load, and drop everything when the container is deleted?
**Sub-questions:** (1) What do schema-per-tenant designs cost and risk in Postgres, including on Azure Flexible Server? (2) Which swap technique gives readers an atomic view with the least locking? (3) How do comparable products (dbt, Metabase, Baserow, Airbyte, NocoDB) do it?
**Out of scope:** parsing and type inference (ticket 5), the agent read role and query tool (ticket 6).
**Success criteria:** a named swap technique, plus what to do on container create and delete.

## Findings: schema per container
Schema per container is sound at hundreds to low thousands of schemas. PostgreSQL has no hard limit on schema count (primary: PostgreSQL docs).
- **Costs grow with object count.** Each backend's relcache and catcache grow with the tables it touches. `pg_dump` time and autovacuum work grow with the number of relations (secondary: community write-ups on schema-per-tenant).
- **`DROP SCHEMA … CASCADE` takes one lock per relation.** The shared lock table holds `max_locks_per_transaction` × connections entries, by default 64 × connections. That setting is static on Azure Flexible Server. A container with thousands of tables could exhaust it, and the fix would be dropping in batches (primary: PostgreSQL docs on lock management).
- **Privileges on Azure.** On Azure Flexible Server, `azure_pg_admin` is not a superuser. `CREATE` on the database is enough for `CREATE SCHEMA` (primary: Azure docs).
- **Names.** `tables_` followed by a 32-character hex container id is 39 bytes, under the 63-byte identifier limit. Identifiers can't be query parameters, so generated names use only `[a-z0-9_]` and are still quoted.
- **Advisory locks.** Use transaction-scoped advisory locks only (`pg_advisory_xact_lock`). A session-scoped lock can outlive a pooled connection that Npgsql has closed but not yet reset.

## Findings: atomic swap
A catalog pointer flip is the safest swap (primary: PostgreSQL docs on transactional DDL and lock levels). The load runs in one transaction:
1. `CREATE TABLE t_<new id>`;
2. binary `COPY` the rows in;
3. update the catalog row to point at the new table.

Transactional DDL makes the new table and the catalog change appear together at commit, or not at all. A crash rolls back the half-built table.

Readers never touch an `ACCESS EXCLUSIVE` lock. A rename swap (`ALTER TABLE … RENAME`) takes one. That makes every new reader queue behind it while it waits for long-running readers to finish. A fresh name per load also avoids Npgsql's "cached plan must not change result type" (0A000) error, which a same-name swap with changed columns can raise.

The old table goes on a retired list and is dropped after a grace period longer than the statement timeout, with `SET LOCAL lock_timeout`. A reader that resolved the old name just before the flip finishes normally. A reader that resolves a name and then hits "relation does not exist" (42P01) should re-resolve once; that belongs in ticket 6.

TRUNCATE plus COPY in one transaction is atomic too. But TRUNCATE takes `ACCESS EXCLUSIVE` for the whole load, so it blocks readers for the entire load. Partition attach/detach adds complexity and gains nothing here.

## Findings: how other products do it
- **dbt** (primary: the `table.sql` materialization macro in dbt-adapters) loads into a `__dbt_tmp` table. In one transaction it then renames the live table to `__dbt_backup` and the temporary table to the live name. It drops the backup after the commit, and re-applies grants because they don't follow a rename.
- **Airbyte** documents the same rename-at-the-end swap for overwrite syncs. It warns that permissions and views added outside Airbyte can be lost (secondary: Airbyte docs, read from search snippets).
- **Metabase uploads** (primary: `upload/impl.clj`) replace by running TRUNCATE and then INSERT in separate transactions. Readers can briefly see an empty table, which is exactly the failure CON-301 must avoid.
- **Baserow** (primary: its source code) keeps all user tables in one schema with opaque `database_table_<id>` and `field_<id>` names; real names live in its catalog. It serializes schema changes with `SELECT … FOR UPDATE` on its catalog rows.
- **NocoDB** uses a schema per base by default (secondary: its docs).

Two lessons carry over:
- keep the physical names opaque and the real headers in the catalog, as Baserow does;
- serialize changes to a table by locking its catalog row.

## Decision
Connapse uses the pointer flip.
- **Create:** the container's schema is created in the same transaction as the container row.
- **Load:** every load ensures the schema exists, which covers tables containers created before this ticket.
- **Reload:** a reload locks the catalog row (`FOR UPDATE`), builds the new table, flips the pointer, and records the old table as retired.
- **Delete:** container delete takes `pg_advisory_xact_lock` on the container, sets `lock_timeout`, drops the schema with `CASCADE`, and deletes the container row. Catalog rows go with it by foreign-key cascade.

## Conflicts and uncertainties
- **dbt and Airbyte rename instead of flipping a pointer.** That fits tools whose readers query a fixed table name. Connapse's readers resolve the name from the catalog (ticket 6), so a pointer flip avoids the exclusive lock that a rename needs. If an outside BI tool ever needs a stable table name, a view per table could be re-pointed in the same transaction. Both dbt and Airbyte warn that views and grants don't survive a rename; re-pointing a view avoids that.
- **Where the lock-count limit bites** for one `DROP SCHEMA` depends on connection count and server settings. It hasn't been measured on Azure.

## Gaps
- No measurement of catalog memory at Connapse's real table counts.
- NocoDB's schema naming and Citus's `DROP SCHEMA` behaviour on distributed schemas were not verified.
- Fivetran was not researched.

## Sources
**Primary:**
- PostgreSQL docs (DDL, explicit locking, lock management, identifiers)
- Azure Database for PostgreSQL Flexible Server docs (roles and privileges)
- dbt-adapters `table.sql` and `exceptions/cache.py`
- Metabase `upload/impl.clj` and `driver/sql_jdbc.clj`
- Baserow `table/constants.py`, `table/models.py`, `db/schema.py`, `fields/handler.py`
- Npgsql docs (auto-prepare, connection reset)

**Secondary:**
- Airbyte typing-and-deduping docs
- NocoDB environment-variable docs
- Citus DDL reference
- community write-ups on schema-per-tenant at scale
