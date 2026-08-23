# Phase 5 — Remove compatibility shims and drop the connector columns

Closes #353, and with it epic #348. Milestone v0.4.0.

## The decision this phase rests on

Dropping `containers.connector_type` and `containers.connector_config` is only safe if the
backfill from issue #350 has already turned every external container into a source. It has not run
anywhere: the backfill shipped in this same unreleased milestone, and the latest release is
v0.3.2 from March. Worse, `MigrateAsync()` runs in the startup block before hosted services
start, so a drop migration would remove the columns *before* the backfill ever read them.

Re-expressing the backfill in SQL inside the migration was rejected: `ConnectorConfigMapper.NameFor`
derives connection names from a SHA-256 of a dedup key, and a second implementation that
disagreed by one character would double-create connections for anyone who had already run
the C# path.

**The user's call: treat v0.4.0 as a clean break.** No deployment needs to carry external
containers across the v0.3.2 → v0.4.0 boundary, so the backfill is deleted rather than
sequenced, and the columns go with it.

## Phases

### 1. Schema and domain model
Drop both columns; remove `ConnectorType` and `ConnectorConfig` from `ContainerEntity`,
`Container`, `CreateContainerRequest`, and `PostgresContainerStore`; delete
`IContainerStore.UpdateConnectorConfigAsync`.
**Done when:** the model snapshot has no connector columns and `Connapse.Storage` builds.

### 2. Delete the backfill
Remove `SourceBackfillService`, `ConnectorConfigMapper`, `IConnectorConfigMapper`,
`BackfillModels`, `SourceBackfillHostedService`, their DI registration, and their tests.
**Done when:** no type named `*Backfill*` remains outside test names that describe behaviour
rather than the deleted class.

### 3. Endpoints and UI
Remove the compat read and `ToContainerShape` from `GET /api/containers/{id}`, the connector
branches in create, the `connectorType` field in stats, `POST /test-connection`, the
`RequireManagedStorage` guards, and the connector references in `Home.razor`,
`FileBrowser.razor`, and `McpTools`.
**Done when:** no endpoint or component reads a container's connector type.

### 4. ConnectorFactory
Drop `IConnectorFactory.Create(Container)` and the three legacy `Create*Connector(Container)`
helpers. Only `Create(Source, Connection)` survives.
**Done when:** the factory has one public overload.

### 5. Tests and docs
Update every test that constructs a container with a connector type; refresh the ten stale
doc references to deleted components.
**Done when:** `dotnet test` is green and the docs describe the shipped system.

## Deliberately out of scope

`ICloudScopeService.GetScopesAsync` takes a `Container` and maps `S3`/`AzureBlob` to a cloud
provider. With containers managed-only it can only ever return null, so per-user cloud scope
becomes structurally unreachable. This is not introduced here — #350 already made it inert for
any deployment that ran the backfill — but Phase 5 is where it stops being expressible at all.
REST search compounds it: it resolves containers only and 404s on a source ID, so a source is
searchable through MCP alone, unscoped. Filed separately rather than fixed inside a removal PR.

## Log

- **2026-08-21** — Plan written; branch `feature/353-remove-compat-shims` created off `main` at a8452e9.
- **2026-08-21** — Phases 1–4 done. Columns dropped, backfill deleted, compat read gone,
  `IConnectorFactory` down to its source overload. Two things the plan did not anticipate:
  every container-side connector lookup became a direct `IManagedStorageProvider` call
  (ingestion, reindex, summary jobs, upload, MCP `get_document`), and cloud scope had to be
  **retargeted to sources rather than deleted** — pointing it at a managed-only container could
  only ever return null, so leaving it in place would have been enforcement that cannot fire.
  Verified: `dotnet test` 807 unit + 328 integration, 0 failed; the drop migration applies to a
  live Postgres (integration tests failed on the missing column until their raw SQL was updated,
  which is the migration proving it ran).
- **2026-08-21** — Phase 5 split out: `docs/connectors.md` is a document about the old model and
  needs rewriting rather than patching, so it ships as a stacked docs PR rather than pushing this
  one further past the 300-line convention.
- **2026-08-21** — Both PRs open. #375 (code, closes #353) targets `main`; #377 (docs, closes #376,
  filed for the untracked doc debt) is stacked on it. `connectors.md` rewritten as
  "Connections, Sources, and Containers"; `api.md` gains a Sources section; `architecture.md` and
  `mcp-tools.md` updated; one README line. Verified no reference to a deleted component survives
  outside sentences that describe it as removed. **Epic #348 is complete pending review.**
