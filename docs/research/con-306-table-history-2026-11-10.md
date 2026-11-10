# How should a synced table keep its row history, and answer "as seen at" and "what changed"?
**Date:** 2026-11-10
**Status:** Reviewed
**Built on:** docs/plans/con-e6-tables-container.md (Memory and history, ticket 9); docs/research/con-301-table-storage-2026-11-09.md. No Connapse corpus search was available in this session.

## Executive summary
Connapse re-syncs a CSV by re-reading the whole file, so history has to be built by diffing full snapshots. Of the systems surveyed, only dbt snapshots (check strategy) do that; Fivetran's history mode refuses fully re-imported tables and Airbyte removed its history tables. The recommended design follows dbt and SQL:2011: every row version carries a half-open `[seen_from, seen_until)` interval in two `timestamptz` columns, with `'infinity'` for current rows; a re-sync closes changed and removed rows and inserts new versions, set-based, in one transaction; an as-of read is `seen_from <= t AND seen_until > t`. "What changed" should be a net change per key between two times, as Snowflake's default CHANGES and Iceberg's `net_changes` give, because an agent wants the difference, not the churn. The main open risk is the key: CSVs declare none, and a wrong key corrupts history silently (dbt's own warning), so Connapse infers one conservatively and re-checks it on every load.

## Research brief
**Question:** How should Connapse keep history for synced tables so agents can read a table as Connapse saw it at a time and ask what changed?

**Sub-questions:**
1. How do existing tools model row versions from repeated syncs (columns, change detection, deletes, as-of predicates)?
2. What should a "what changed" answer look like: every event or net change, and with what boundaries?
3. How should PostgreSQL store it (two columns or a range, indexes, PG18 temporal features, Azure extensions, set-based re-sync, bloat)?
4. How should a key be chosen when a CSV declares none?

**Out of scope:** valid time (when a change happened at the source; the plan states seen time only), memory's history model, MCP/REST tools (ticket 8), retention.

**Success criteria:** a design the CON-306 PR can implement, with its sources.

## Findings by sub-question

### 1. Row-version models in dbt, SQL:2011 engines, Fivetran and Airbyte
All surveyed systems store a version's validity as a start and an end; the end is exclusive except in Fivetran.
- **dbt snapshots** add `dbt_valid_from`/`dbt_valid_to` (NULL or a configurable far-future value via `dbt_valid_to_current` for current rows) and `dbt_scd_id`. The check strategy compares columns null-safely; without `updated_at`, validity comes from the run's time, which is exactly Connapse's "when Connapse saw it". `hard_deletes` can ignore, invalidate (close) or record deletes. dbt warns the unique key must be unique and does not say what happens otherwise. (primary: docs.getdbt.com/docs/build/snapshots; dbt-adapters strategies.sql, helpers.sql, snapshot_merge.sql)
- **SQL Server temporal tables**: `ValidFrom`/`ValidTo`, current rows end at 9999-12-31; `AS OF t` is `ValidFrom <= t AND ValidTo > t`; several updates to one key in one transaction make zero-length versions that `FOR SYSTEM_TIME` hides. (primary: learn.microsoft.com temporal overview)
- **MariaDB** system-versioned tables: `ROW_START`/`ROW_END`, current rows end at 2038-01-19; `FROM..TO` is start-inclusive, end-exclusive. (primary: mariadb.com docs)
- **Fivetran history mode**: `_fivetran_start`/`_fivetran_end`/`_fivetran_active`; superseded versions end 1 ms before the next one starts (closed intervals); tables re-imported in full each sync are not supported. (primary: fivetran.com history-mode docs)
- **Airbyte** removed its SCD history tables in Destinations V2. (primary: docs.airbyte.com)

### 2. "What changed" answer shapes: every event vs net change
Delta CDF returns every event (`insert`, `update_preimage`, `update_postimage`, `delete`, inclusive bounds). Snowflake CHANGES/streams default to the net difference between two points: a row inserted and deleted inside the window is not returned, and an update is a DELETE+INSERT pair. Iceberg's changelog returns every event unless `net_changes=true`, and pairs deletes with inserts into `UPDATE_BEFORE`/`UPDATE_AFTER` using identifier columns. BigQuery CHANGES pairs a DELETE with an UPDATE row per update and does not document net vs events. (primary: docs.delta.io, docs.snowflake.com changes and streams-intro, iceberg.apache.org spark-procedures, cloud.google.com time-series-functions)
- Net change answers "what is different now vs then" directly and keeps the answer small for an agent's context; it hides churn inside the window.
- Every system that reports updates (rather than delete+insert) needs a stable key per row.
- Half-open windows tile without double counting.

### 3. PostgreSQL storage
- Two scalar `timestamptz` columns with `'infinity'` for current rows give a plain `seen_from <= $t AND seen_until > $t` predicate and ordinary btree/partial indexes; a `tstzrange` needs GiST for containment. (primary: postgresql.org rangetypes; the no-OR benefit is inference)
- PG18 `WITHOUT OVERLAPS` keys are constraints on range columns via GiST and need `btree_gist` for scalar keys; they are not automatic history. Azure Flexible Server offers `btree_gist` and `temporal_tables`, not `periods`. Connapse Cloud's Postgres version isn't guaranteed to be 18, so neither is relied on. (primary: postgresql.org 18 CREATE TABLE; learn.microsoft.com Azure extensions)
- The dbt Postgres merge is two statements: UPDATE to close, INSERT for new versions, from a staged set. (primary: dbt-adapters snapshot_merge.sql)
- Closing rows in place leaves one dead tuple per changed row for autovacuum, far less churn than rebuilding the table each sync; real growth is the history itself. (primary: postgresql.org routine-vacuuming, storage-hot)

### 4. Choosing a key for a CSV with no declared key
Frictionless Table Schema stores a key as `primaryKey` (fields, non-null). No surveyed tool infers a key from data: Metabase adds a generated row id, Airbyte takes it from the source or the user, Fivetran hashes the whole row when there is no key (single-source). dbt warns non-unique keys give "unexpected snapshot results" and null keys fail to match. (primary: datapackage.org table-schema, metabase.com uploads, docs.getdbt.com unique_key; secondary: Fivetran missing-primary-keys page)
- Recommended: infer a single column only when its header looks like an identifier and its values are unique and non-null in this load; re-check every load; never guess composite keys.

## Recommendation for CON-306
1. A table gets history when a key column is inferred: header `id`, `key`, `code`, `sku`, `uuid`, `guid`, or ending in `id` as a separate word or camel-case suffix (`order_id`, `Order ID`, `CustomerID`); text or whole-number type; unique and non-null in the load. The first such column wins. It is stored as Frictionless `primaryKey`.
2. History tables carry `_seen_from` and `_seen_until` (`'infinity'` when current) and a unique partial index on the key over current rows.
3. A re-sync with the same columns and a still-valid key loads the file into a fresh table, then, in the same transaction, closes current rows whose key is gone or whose values differ (`IS DISTINCT FROM` on the whole row, null-safe as in dbt's check strategy) and inserts the new versions; deletes close the row (dbt `invalidate`). All versions written by one sync share one seen time read from the database clock after the table's lock is held, so seen times never run backwards.
4. A table without a key replaces its rows and keeps no history, as the plan says. If the columns change or the key stops being unique, history restarts from that sync; the catalog records when the current history began, and earlier times are refused rather than answered wrongly.
5. Reads take an optional `seen_as_of`; with none they see current rows. "What changed" returns the net change per key between `since` and `until` (now by default): each changed key as an `old` row and a `new` row, added keys as `new` only, removed keys as `old` only, ordered by key.

## Conflicts and uncertainties
- The researchers disagreed on keyless tables: the Postgres/keys report recommends whole-row-hash versioning (Fivetran style); the epic plan says a table without a key keeps no history. This PR follows the plan; row-hash history can be added later without changing the read surface.
- Fivetran's closed intervals conflict with everyone else's half-open ones; half-open is chosen.
- Whether Snowflake collapses several updates into one pair, and whether Delta returns each intermediate update, are inferred from wording. BigQuery's net-vs-events behaviour is undocumented.
- dbt's exact `row_changed` expression for the check strategy was not opened; the column-wise null-safe comparison is described in its docs.

## Gaps — what we did not find
- No tool documented inferring a primary key from data; Connapse's inference rule is its own.
- DuckDB, ydata-profiling and Great Expectations pages could not be read.
- No retention guidance specific to snapshot-diff history was found.

## Source quality assessment
Almost every claim rests on primary documentation or source code (dbt-adapters macros, vendor docs, PostgreSQL docs). The Fivetran keyless-hash detail and the Airbyte column names are single-source and secondary; neither is load-bearing.

## Sources
**Primary**
- https://docs.getdbt.com/docs/build/snapshots
- https://docs.getdbt.com/reference/snapshot-configs
- https://docs.getdbt.com/reference/resource-configs/unique_key
- https://github.com/dbt-labs/dbt-adapters/blob/main/dbt-adapters/src/dbt/include/global_project/macros/materializations/snapshots/strategies.sql
- https://github.com/dbt-labs/dbt-adapters/blob/main/dbt-adapters/src/dbt/include/global_project/macros/materializations/snapshots/helpers.sql
- https://github.com/dbt-labs/dbt-adapters/blob/main/dbt-postgres/src/dbt/include/postgres/macros/materializations/snapshot_merge.sql
- https://learn.microsoft.com/en-us/sql/relational-databases/tables/temporal/overview
- https://mariadb.com/docs/server/reference/sql-structure/temporal-tables/system-versioned-tables
- https://fivetran.com/docs/core-concepts/sync-modes/history-mode
- https://docs.airbyte.com/platform/using-airbyte/core-concepts/sync-modes/incremental-append-deduped
- https://docs.airbyte.com/release_notes/upgrading_to_destinations_v2
- https://docs.delta.io/latest/delta-change-data-feed.html
- https://docs.databricks.com/aws/en/delta/delta-change-data-feed
- https://docs.snowflake.com/en/user-guide/streams-intro
- https://docs.snowflake.com/en/sql-reference/constructs/changes
- https://docs.snowflake.com/en/sql-reference/constructs/at-before
- https://docs.cloud.google.com/bigquery/docs/reference/standard-sql/time-series-functions
- https://iceberg.apache.org/docs/latest/spark-procedures/
- https://www.postgresql.org/docs/17/rangetypes.html
- https://www.postgresql.org/docs/18/sql-createtable.html
- https://www.postgresql.org/docs/17/routine-vacuuming.html
- https://www.postgresql.org/docs/17/storage-hot.html
- https://www.postgresql.org/docs/17/sql-copy.html
- https://learn.microsoft.com/en-us/azure/postgresql/extensions/concepts-extensions-versions
- https://datapackage.org/standard/table-schema/
- https://www.metabase.com/docs/latest/exploration-and-organization/uploads

**Secondary**
- https://fivetran.com/docs/connectors/databases/troubleshooting/missing-primary-keys-issues
