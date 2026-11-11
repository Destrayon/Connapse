# CON-304 — People only see the tables, rows and columns they're allowed to

Ticket 7 of [con-e6-tables-container.md](con-e6-tables-container.md). Research:
[con-304-table-permission-trimming-2026-11-10.md](../research/con-304-table-permission-trimming-2026-11-10.md).
Stacked on PR #1206 (CON-324, row-level security); merge after it.

1. **Models and catalog** — `TableLoad.SourceDocuments` (merged loads), `TableInfo` source documents and hidden
   columns, `TableReject.DocumentId`, `TableAccess` any-source visibility and an unchecked state, migration. Done when
   it builds.
2. **Storage** — merged loads write `_document`, the RLS policy checks it per row, the store hides columns by name
   and keeps them across reloads. Done when storage builds.
3. **Compiler and service** — hidden columns behave as missing, merged tables get the readable-source `WHERE`,
   `DescribeAsync` returns trimmed count, samples and rejects, unchecked access answers nothing. Done when it builds.
4. **Application** — `ListTables` resolves access through `IDocumentAccess`, fails closed, trims each listed table;
   admin `tables.hideColumns` operation. Done when unit tests pass.
5. **Planted-leak tests** — merged rows, hidden columns in every clause, describe unchanged when only hidden rows
   change, unchecked access, RLS on the merged policy. Done when they pass.
6. **Ship** — Release build with CI flags, unit and table integration tests, PR into `epic/con-e6-tables`.

## Progress

- Phase 1: `TableLoad`/`TableInfo`/`TableReject`/`TableAccess` in `TableModels.cs`, `TableQueryModels.cs`; new
  interface members `DescribeAsync`, `SetHiddenColumnsAsync`. Migration moved to Phase 2 (needs a building Storage).
- Phase 2: `PostgresTableStore`, `TableDdl`, entity + context, migration `TableSourceDocumentsAndHiddenColumns`.
  Storage builds. Not yet run against a database.
- Phase 3: `TableQueryCompiler` (visible-only lookup, `AppendWhere`, `CompileCount`, `CompileSample`) and
  `TableQueryService` (unchecked → error, `DescribeAsync`, `VisibleRejects`). Next: Application `ListTables`.
- Phase 4: `ListTables` resolves access through `IDocumentAccess` (`TableRules.AccessAsync`), returns
  `AccessUnchecked` (503) when that fails, lists only readable tables and trims each (`TableRules.Trim`; partial merged
  tables count readable rows via new `ITableQueryService.CountAsync` and drop version/loaded time). New admin
  `tables.hideColumns` op + `PUT .../hidden-columns`; openapi.json and capabilities.json updated. Unit + bUnit tests
  pass. Next: planted-leak integration tests.
- Phase 5: `TablePermissionTrimmingTests` (15 tests) — merged rows through the service and on the RLS policy alone,
  hidden columns in every clause read as missing, describe identical when only unreadable rows change, unchecked
  access. Found and fixed: on a fresh database the first table build failed (`tables_auth` didn't exist until the
  first query), so `PostgresTableStore` now provisions the reader first. All 90 table integration tests pass.
  Next: Release build with CI flags, PR.
- Phase 6: Release build (`--no-incremental -warnaserror`), format and all unit tests clean after adding
  `docs/problems.md` entries and keeping `ContainerTables.razor` inside its logic budget. Commit 2bd95417, PR #1208
  into `epic/con-e6-tables`; ClaudePM told. Next: wait for CI/review.
