# Where a tables container kind touches Connapse (CON-300)
**Date:** 2026-11-09
**Status:** Reviewed
**Superseded (2026-11-10, CON-319):** the `tables` container kind this note maps was dropped before release. Any documents container now holds any number of tables beside its files, and the migration `DropTablesContainerKind` turns any tables container into a documents one.
**Built on:** [con-e6-tables-container.md](../plans/con-e6-tables-container.md) ("Container type"), [tables-container-2026-11-02.md](tables-container-2026-11-02.md)

## Executive summary
Containers already have a kind (CON-227): `ContainerKind { Documents, Memory }`, stored as text in `containers.kind`
and fixed at creation. A tables container is a third value. The risk is not the column but the hand-written mappings
that read every non-memory value as documents, and the checks that refuse only memory. Every text search goes through
one place, `SearchKnowledge.ResolveAsync`, which already refuses memory containers, so tables are refused there too.

## Model and storage
- `Connapse.Core/Models/StorageModels.cs`: `ContainerKind`, `Container.Kind`, `CreateContainerRequest.Kind`.
- `Connapse.Storage/Data/KnowledgeDbContext.cs`: the `kind` text conversion maps anything not `memory` to
  `documents`. `PostgresMemorySpaceStore` repeats that mapping twice for raw SQL rows. All three must learn `tables`.
- The column is `varchar(16)` defaulting to `documents` (`20261103010402_AddContainerKind`), so `tables` needs no
  column change. A check constraint on the allowed values makes the migration this ticket needs, and stops an unknown
  value being read back as documents.

## Create and read surfaces
- `CreateContainer` (Application) validates the kind; REST `POST /api/containers` takes `kind` through the global
  `JsonStringEnumConverter`; MCP `container_create` parses `kind` text with `ParseKind`; `Home.razor`'s create modal
  hard-codes documents.
- `ListContainers` lists only documents containers, so it must widen to documents and tables (memory spaces stay out,
  CON-291). MCP `container_list`/`container_describe` label each container through `SearchableOwner`.
- `docs/api/openapi.json` is committed and checked by two spec tests, so it must be regenerated.

## Text search
- Entry points: REST container and source search, MCP `search_knowledge`, the four MCP code tools (via `CodeAsker`),
  and the Blazor search page. All resolve their scope through `SearchKnowledge.ResolveAsync`.
- No caller searches several containers at once; a null `ContainerId` at the `IKnowledgeSearch` layer is unused.
- Searching a memory container answers `409 memory-container` before any search runs. Tables gets the same shape:
  `409 tables-container`, pointing to the table tools to come.
- The Blazor search page fills its container list from an unfiltered `ListAsync`; it should list documents only.
- Text ingestion (upload, append, reindex) refuses only memory. Until table loading lands (build ticket 5), refusing
  it for tables keeps passages and embeddings out of tables containers, as the design requires.

## Not blocking the 126/180 bar
The tables search runs through the table tools (build tickets 4–8), which read the catalog, not chunks. Nothing here
constrains them: the kind is a plain enum value the later tickets can branch on, and refusing text ingestion is a
single check ticket 5 replaces with table loading.

## Sources (all primary: this repository at `origin/release/0.5.0` 18225496)
`StorageModels.cs`, `KnowledgeDbContext.cs`, `PostgresMemorySpaceStore.cs`, `ContainerOperations.cs`,
`SearchOperations.cs`, `CodeOperations.cs`, `ContainersEndpoints.cs`, `SearchEndpoints.cs`, `McpTools.cs`,
`Home.razor`, `Search.razor`, `FileBrowser.razor`, `UploadOperations.cs`, `AppendOperations.cs`,
`ContainerMaintenance.cs`, `SettingsActions.cs`.
